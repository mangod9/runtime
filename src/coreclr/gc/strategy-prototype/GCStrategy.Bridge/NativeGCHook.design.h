// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// NativeGCHook.h — What would be added to the standalone GC DLL (clrgc.dll)
//
// This is a DESIGN DOCUMENT showing the C++ code that would be added to the
// GC to call managed strategy function pointers.
//
// Location: src/coreclr/gc/strategyhook.h (new file in production)
// ============================================================================

/*

#pragma once

#include <cstdint>

// ─────────────────────────────────────────────────────────────────────────────
// Blittable structs matching the managed NativeGCTelemetry / NativeCollectionPlan
// ─────────────────────────────────────────────────────────────────────────────

struct NativeGCTelemetry
{
    int64_t TotalAllocatedBytes;
    int32_t LiveObjectCount;
    int32_t DeadObjectCount;
    int32_t ActiveRegionCount;
    int64_t BytesSinceLastCollection;
    int64_t TimeSinceLastCollectionTicks;
    double  MemoryPressure;
    int32_t Generation;
};

enum CollectionAction : int32_t
{
    ACTION_NONE = 0,
    ACTION_COLLECT = 1,
    ACTION_BULK_FREE_REGIONS = 2,
    ACTION_INCREMENTAL_MARK = 3,
};

struct NativeCollectionPlan
{
    int32_t Action;            // CollectionAction
    int32_t TargetGeneration;
    int64_t TimeBudgetTicks;
    int32_t Compact;           // bool as int
};

struct NativePromotionPolicy
{
    int32_t MinPromotionAge;
    int64_t LargeObjectThreshold;
    int32_t MaxGeneration;
};

// ─────────────────────────────────────────────────────────────────────────────
// Function pointer types for managed strategy callbacks
// ─────────────────────────────────────────────────────────────────────────────

typedef NativeCollectionPlan (__cdecl *ShouldCollectFn)(NativeGCTelemetry* telemetry);
typedef void (__cdecl *PostCollectionFn)(int64_t bytesFreed, int32_t objectsFreed, int64_t pauseTicks);

// ─────────────────────────────────────────────────────────────────────────────
// Global strategy callback slots
// Managed code writes these during app initialization via a registration API.
// The GC reads them at safe points (allocation slow path, post-collection).
// ─────────────────────────────────────────────────────────────────────────────

struct GCStrategyCallbacks
{
    ShouldCollectFn     shouldCollect;      // Called on allocation slow path
    PostCollectionFn    postCollection;     // Called after RestartEE
    NativePromotionPolicy promotionPolicy;  // Read during mark phase (no callback)
    volatile bool       registered;         // True once managed has registered
};

// Single global instance
extern GCStrategyCallbacks g_strategyCallbacks;

// ─────────────────────────────────────────────────────────────────────────────
// Registration API — called by managed code at startup
// Exported from clrgc.dll so managed can P/Invoke to register callbacks
// ─────────────────────────────────────────────────────────────────────────────

extern "C" __declspec(dllexport) void GC_RegisterStrategy(
    ShouldCollectFn shouldCollect,
    PostCollectionFn postCollection,
    NativePromotionPolicy* promotionPolicy
);

// ─────────────────────────────────────────────────────────────────────────────
// Hook implementation — inserted into allocation.cpp
// ─────────────────────────────────────────────────────────────────────────────

// This would be inserted in allocation.cpp around line 3902, BEFORE the call
// to GarbageCollectGeneration:
//
//     // === STRATEGY HOOK: Ask managed strategy before triggering GC ===
//     if (g_strategyCallbacks.registered && g_strategyCallbacks.shouldCollect)
//     {
//         NativeGCTelemetry telemetry = build_telemetry();
//         NativeCollectionPlan plan = g_strategyCallbacks.shouldCollect(&telemetry);
//
//         if (plan.Action == ACTION_NONE)
//         {
//             // Strategy says don't collect — grow instead
//             // (expand heap, allocate new region, etc.)
//             goto expand_heap;
//         }
//
//         // Use the strategy's recommended generation
//         gen_number = plan.TargetGeneration;
//         // ... proceed with GarbageCollectGeneration
//     }
//
//     vm_heap->GarbageCollectGeneration(gen_number, gr);
//     // === END STRATEGY HOOK ===
//
// ─────────────────────────────────────────────────────────────────────────────
// Post-collection hook — inserted after RestartEE in collect.cpp
// ─────────────────────────────────────────────────────────────────────────────
//
//     GCToEEInterface::RestartEE(true);
//
//     // === STRATEGY HOOK: Notify managed of collection results ===
//     if (g_strategyCallbacks.registered && g_strategyCallbacks.postCollection)
//     {
//         g_strategyCallbacks.postCollection(bytes_freed, objects_freed, pause_ticks);
//     }
//     // === END STRATEGY HOOK ===
//
// ─────────────────────────────────────────────────────────────────────────────
// Promotion using policy table — inserted in mark phase (replaces callback)
// ─────────────────────────────────────────────────────────────────────────────
//
//     // Instead of calling managed (can't, EE is suspended), read the policy:
//     bool should_promote = false;
//     if (g_strategyCallbacks.registered)
//     {
//         auto& policy = g_strategyCallbacks.promotionPolicy;
//         int age = object_age(obj);
//         size_t size = object_size(obj);
//
//         if (age >= policy.MinPromotionAge)
//             should_promote = true;
//         else if (size > policy.LargeObjectThreshold && age >= (policy.MinPromotionAge - 2))
//             should_promote = true;
//     }
//     else
//     {
//         // Default promotion logic
//         should_promote = (object_age(obj) >= 2);
//     }
//

*/

// This file is a design document. The actual C++ would live in:
// - src/coreclr/gc/strategyhook.h (struct definitions)
// - src/coreclr/gc/strategyhook.cpp (GC_RegisterStrategy implementation)
// - src/coreclr/gc/allocation.cpp (ShouldCollect hook)
// - src/coreclr/gc/collect.cpp (PostCollection hook)
// - src/coreclr/gc/mark.cpp (promotion policy evaluation)
