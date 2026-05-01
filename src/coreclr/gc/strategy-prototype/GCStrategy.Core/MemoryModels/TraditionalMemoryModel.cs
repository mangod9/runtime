// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// TraditionalMemoryModel — Represents today's generic .NET GC as a memory model.
//
// This shows that the CURRENT generational GC is just one configuration of
// the memory model framework — not a special case. The generic goo disappears;
// it's just a model with 3 generational spaces and mark-sweep-compact.
//
// When you compare this to WebApiMemoryModel or GameMemoryModel, you can see
// exactly what the generic GC does that is unnecessary for specific workloads.
// ============================================================================

namespace GCStrategy.Core.MemoryModels;

public sealed class TraditionalMemoryModel : IGCMemoryModel
{
    public string Name => "Traditional (.NET Generic GC as a Memory Model)";

    public MemorySpaceConfig[] DefineSpaces() =>
    [
        new MemorySpaceConfig
        {
            Id = 0,
            Name = "gen0",
            Allocator = AllocatorKind.ThreadLocalBumpPointer,
            Collector = CollectorKind.MarkCompact,       // ← always traces, always compacts
            Barrier = BarrierKind.CardTable,
            Trigger = TriggerKind.SpaceFull,
            InitialSize = 256 * 1024,
            MaxSize = 4 * 1024 * 1024,
            AllowPinning = true,
            AllowFinalizers = true,
        },
        new MemorySpaceConfig
        {
            Id = 1,
            Name = "gen1",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.MarkCompact,
            Barrier = BarrierKind.CardTable,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.75,
            InitialSize = 2 * 1024 * 1024,
            MaxSize = 0,
            AllowPinning = true,
            AllowFinalizers = true,
        },
        new MemorySpaceConfig
        {
            Id = 2,
            Name = "gen2",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.ConcurrentMark,
            Barrier = BarrierKind.CardTable,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.9,
            InitialSize = 16 * 1024 * 1024,
            MaxSize = 0,
            AllowPinning = true,
            AllowFinalizers = true,
        },
        new MemorySpaceConfig
        {
            Id = 3,
            Name = "LOH",
            Allocator = AllocatorKind.FreeList,
            Collector = CollectorKind.MarkSweep,         // ← never compacts (expensive for large objects)
            Barrier = BarrierKind.CardTable,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.9,
            InitialSize = 4 * 1024 * 1024,
            MaxSize = 0,
            AllowPinning = true,
            AllowFinalizers = true,
        },
    ];

    public int RouteAllocation(in AllocationRequest request)
    {
        // Large objects → LOH (no compaction, free-list)
        if (request.Size >= 85_000)
            return 3;

        // Everything else → Gen0 (will be promoted through gen1 → gen2 over time)
        return 0;
    }

    public int[] OnAppEvent(string eventName)
    {
        // Generic GC ignores app events — it doesn't understand app lifecycle
        return [];
    }

    public void OnTelemetryUpdate(ReadOnlySpan<SpaceTelemetry> spaceTelemetry)
    {
        // Generic GC has its own internal tuning (DATAS) — no app input
    }
}
