// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// WebApiMemoryModel — Memory model for web API workloads.
//
// Instead of a generic 3-generation heap, this defines:
//   Space 0: "request-arena" — bulk-free at request end, NO tracing
//   Space 1: "shared-heap" — generational for long-lived objects
//   Space 2: "buffer-pool" — fixed-size pool for I/O buffers
//
// The key win: request-scoped objects (majority of allocations in a web API)
// NEVER go through mark-sweep. They are bulk-freed in O(1) when the request ends.
// This eliminates the most expensive GC operation for the most common case.
// ============================================================================

namespace GCStrategy.Core.MemoryModels;

public sealed class WebApiMemoryModel : IGCMemoryModel
{
    public string Name => "WebApi Memory Model (Arena + Generational + Pool)";

    public MemorySpaceConfig[] DefineSpaces() =>
    [
        new MemorySpaceConfig
        {
            Id = 0,
            Name = "request-arena",
            Allocator = AllocatorKind.ThreadLocalBumpPointer,
            Collector = CollectorKind.BulkFree,
            Barrier = BarrierKind.EscapeDetect,
            Trigger = TriggerKind.AppEvent,
            TriggerEvents = ["request.end"],
            InitialSize = 256 * 1024,   // 256KB per request arena
            MaxSize = 4 * 1024 * 1024,  // 4MB max (large requests)
            AllowPinning = false,        // pinned objects go to shared-heap
            AllowFinalizers = false,     // finalizable objects go to shared-heap
            EscapePolicy = EscapePolicy.PromoteTo(1),
        },
        new MemorySpaceConfig
        {
            Id = 1,
            Name = "shared-heap",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.ConcurrentMark,
            Barrier = BarrierKind.CardTable,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.8,
            InitialSize = 16 * 1024 * 1024, // 16MB
            MaxSize = 0,                     // unbounded
            AllowPinning = true,
            AllowFinalizers = true,
        },
        new MemorySpaceConfig
        {
            Id = 2,
            Name = "buffer-pool",
            Allocator = AllocatorKind.FixedPool,
            Collector = CollectorKind.MarkSweep,
            Barrier = BarrierKind.None,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.9,
            PoolObjectSize = 4096,           // 4KB buffers
            InitialSize = 1 * 1024 * 1024,   // 1MB pool
            MaxSize = 16 * 1024 * 1024,      // 16MB max
            AllowPinning = true,
            AllowFinalizers = false,
        },
    ];

    public int RouteAllocation(in AllocationRequest request)
    {
        // Pinned or finalizable → must go to shared heap (arenas can't handle these)
        if (request.Pinned || request.HasFinalizer)
            return 1;

        // Buffer-sized allocations → pool
        if (request.Category == TypeCategory.Buffer && request.Size <= 4096)
            return 2;

        // Request-scoped or unknown ephemeral → arena
        if (request.Category is TypeCategory.Scoped or TypeCategory.Ephemeral or TypeCategory.Unknown)
            return 0;

        // Long-lived (singletons, caches) → shared heap
        return 1;
    }

    public int[] OnAppEvent(string eventName)
    {
        return eventName switch
        {
            "request.end" => [0],     // Bulk-free the request arena
            "low_traffic" => [1, 2],  // Clean up shared heap + pool during idle
            _ => [],
        };
    }

    public void OnTelemetryUpdate(ReadOnlySpan<SpaceTelemetry> spaceTelemetry)
    {
        // Could adapt: if request arenas are consistently hitting MaxSize,
        // signal that the app might need larger arenas
    }
}
