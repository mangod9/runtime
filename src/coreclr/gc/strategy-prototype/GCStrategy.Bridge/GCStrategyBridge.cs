// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// GCStrategyBridge — The managed↔native bridge for GC strategy callbacks.
//
// This is the critical piece: it exposes managed strategy methods as native
// function pointers that the GC DLL can call at safe points.
//
// SAFETY RULES:
// 1. [UnmanagedCallersOnly] methods MUST NOT allocate managed objects
// 2. They MUST NOT throw exceptions
// 3. They MUST be fast (called on allocation slow path)
// 4. They can only be called when EE is NOT suspended
//
// ARCHITECTURE:
// ┌─ Managed ─────────────────────────────────┐
// │  App calls GCStrategyBridge.Register(...)  │
// │  → stores fn ptrs in native GC DLL        │
// └────────────────────────────────────────────┘
//          │
//          ▼ function pointers
// ┌─ Native GC (clrgc.dll) ───────────────────┐
// │  Allocation slow path calls fn ptr         │
// │  → "ShouldCollect?" → gets answer          │
// │  → executes collection with native code    │
// └────────────────────────────────────────────┘
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GCStrategy.Core;

namespace GCStrategy.Bridge;

/// <summary>
/// Represents the native-compatible collection plan passed across the boundary.
/// This is a blittable struct that can cross the managed/native boundary without marshalling.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeCollectionPlan
{
    public int Action;           // CollectionAction enum value
    public int TargetGeneration;
    public long TimeBudgetTicks; // TimeSpan.Ticks
    public int Compact;          // bool as int (blittable)

    public static NativeCollectionPlan FromManaged(CollectionPlan plan) => new()
    {
        Action = (int)plan.Action,
        TargetGeneration = plan.TargetGeneration,
        TimeBudgetTicks = plan.TimeBudget?.Ticks ?? 0,
        Compact = plan.Compact ? 1 : 0,
    };

    public static NativeCollectionPlan None => new() { Action = (int)CollectionAction.None };
}

/// <summary>
/// Native-compatible telemetry struct passed from native GC to managed strategy.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeGCTelemetry
{
    public long TotalAllocatedBytes;
    public int LiveObjectCount;
    public int DeadObjectCount;
    public int ActiveRegionCount;
    public long BytesSinceLastCollection;
    public long TimeSinceLastCollectionTicks;
    public double MemoryPressure;
    public int Generation;

    public GCTelemetry ToManaged() => new()
    {
        TotalAllocatedBytes = TotalAllocatedBytes,
        LiveObjectCount = LiveObjectCount,
        DeadObjectCount = DeadObjectCount,
        ActiveRegionCount = ActiveRegionCount,
        BytesSinceLastCollection = BytesSinceLastCollection,
        TimeSinceLastCollection = TimeSpan.FromTicks(TimeSinceLastCollectionTicks),
        MemoryPressure = MemoryPressure,
        Generation = Generation,
    };
}

/// <summary>
/// Native-compatible promotion policy table.
/// Since promotion decisions happen during EE suspension (can't call managed),
/// we express promotion as a pre-computed policy table that native code evaluates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePromotionPolicy
{
    /// <summary>Minimum age before promotion is considered.</summary>
    public int MinPromotionAge;

    /// <summary>Objects larger than this are promoted sooner (at MinPromotionAge - 2).</summary>
    public long LargeObjectThreshold;

    /// <summary>Max generation to promote to.</summary>
    public int MaxGeneration;
}

/// <summary>
/// Function pointer types for the native↔managed bridge.
/// These match what the native GC DLL expects.
/// </summary>
public static unsafe class GCStrategyFunctionPointers
{
    // The native GC calls this to ask "should I collect?"
    // Signature: NativeCollectionPlan ShouldCollect(NativeGCTelemetry* telemetry)
    public static delegate* unmanaged[Cdecl]<NativeGCTelemetry*, NativeCollectionPlan> ShouldCollect;

    // The native GC calls this after collection completes (for telemetry/adaptation)
    // Signature: void PostCollection(long bytesFreed, int objectsFreed, long pauseTicks)
    public static delegate* unmanaged[Cdecl]<long, int, long, void> PostCollection;

    // The promotion policy table (read by native during mark phase — no callback needed)
    public static NativePromotionPolicy PromotionPolicy;
}

/// <summary>
/// The bridge that registers a managed IGCStrategy with the native GC.
/// This is what an application calls at startup to install its custom strategy.
/// </summary>
public static unsafe class GCStrategyBridge
{
    private static IGCStrategy? s_strategy;

    /// <summary>
    /// Register a strategy. In production, this would write the function pointers
    /// to the native GC DLL's global slots. Here we demonstrate the mechanism.
    /// </summary>
    public static void Register(IGCStrategy strategy)
    {
        s_strategy = strategy;

        // Store the function pointers that native GC will call
        GCStrategyFunctionPointers.ShouldCollect = &ShouldCollectNative;
        GCStrategyFunctionPointers.PostCollection = &PostCollectionNative;

        // Set the promotion policy table (native reads this directly, no callback)
        UpdatePromotionPolicy();

        Console.WriteLine($"[GCStrategyBridge] Registered strategy: {strategy.Name}");
        Console.WriteLine($"[GCStrategyBridge] ShouldCollect fn ptr: 0x{(nint)GCStrategyFunctionPointers.ShouldCollect:X}");
        Console.WriteLine($"[GCStrategyBridge] PostCollection fn ptr: 0x{(nint)GCStrategyFunctionPointers.PostCollection:X}");
    }

    /// <summary>
    /// Simulate what the native GC would do: call the managed strategy via fn pointer.
    /// This proves the mechanism works — native code would do exactly this call.
    /// </summary>
    public static NativeCollectionPlan SimulateNativeCall(NativeGCTelemetry telemetry)
    {
        // This is what the C++ GC code would look like:
        // NativeCollectionPlan plan = g_strategyCallbacks.ShouldCollect(&telemetry);
        return GCStrategyFunctionPointers.ShouldCollect(&telemetry);
    }

    // ═══════════════════════════════════════════════════════════════
    // [UnmanagedCallersOnly] methods — callable from native code
    // These MUST NOT allocate, throw, or trigger GC
    // ═══════════════════════════════════════════════════════════════

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeCollectionPlan ShouldCollectNative(NativeGCTelemetry* telemetry)
    {
        // Convert native telemetry to managed, ask strategy, convert result back
        // Note: s_strategy is a static field read — no allocation
        if (s_strategy is null)
            return NativeCollectionPlan.None;

        var managedTelemetry = telemetry->ToManaged();
        var plan = s_strategy.ShouldCollect(in managedTelemetry);

        return NativeCollectionPlan.FromManaged(plan);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PostCollectionNative(long bytesFreed, int objectsFreed, long pauseTicks)
    {
        // Strategy can use this to adapt its thresholds over time
        // For now, just update the promotion policy
        UpdatePromotionPolicy();
    }

    private static void UpdatePromotionPolicy()
    {
        if (s_strategy is null) return;

        // Ask the strategy for a sample promotion decision to infer its policy
        // In a real implementation, the strategy would expose these as properties
        var policy = new NativePromotionPolicy
        {
            MinPromotionAge = 2,
            LargeObjectThreshold = 8192,
            MaxGeneration = 2,
        };

        // Probe the strategy to determine its actual thresholds
        for (int age = 0; age < 20; age++)
        {
            if (s_strategy.ShouldPromote(age, 256, 0) == PromotionDecision.Promote)
            {
                policy.MinPromotionAge = age;
                break;
            }
        }

        for (long size = 256; size <= 65536; size *= 2)
        {
            if (s_strategy.ShouldPromote(policy.MinPromotionAge - 1, size, 0) == PromotionDecision.Promote)
            {
                policy.LargeObjectThreshold = size;
                break;
            }
        }

        GCStrategyFunctionPointers.PromotionPolicy = policy;
    }
}
