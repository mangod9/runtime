// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// GameMemoryModel — Memory model for game/real-time workloads.
//
// Instead of generational GC with unpredictable pauses, this defines:
//   Space 0: "frame-arena" — double-buffered, flipped each frame, zero-cost free
//   Space 1: "persistent" — incremental mark with strict 1ms budget
//   Space 2: "asset-cache" — manual (loaded/unloaded with scenes)
//
// The key win: per-frame allocations (particles, UI, temp math) have ZERO GC cost.
// They're bulk-freed at frame boundary. Only persistent objects are traced, and
// only with a strict time budget that never blows the frame.
// ============================================================================

namespace GCStrategy.Core.MemoryModels;

public sealed class GameMemoryModel : IGCMemoryModel
{
    private int _currentFrame;

    public string Name => "Game Memory Model (Double-Buffer Arena + Budget Mark)";

    public MemorySpaceConfig[] DefineSpaces() =>
    [
        new MemorySpaceConfig
        {
            Id = 0,
            Name = "frame-arena-A",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.BulkFree,
            Barrier = BarrierKind.EscapeDetect,
            Trigger = TriggerKind.AppEvent,
            TriggerEvents = ["frame.end"],
            InitialSize = 512 * 1024,       // 512KB per frame
            MaxSize = 2 * 1024 * 1024,      // 2MB max
            AllowPinning = false,
            AllowFinalizers = false,
            EscapePolicy = EscapePolicy.PromoteTo(1),
        },
        new MemorySpaceConfig
        {
            Id = 1,
            Name = "frame-arena-B",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.BulkFree,
            Barrier = BarrierKind.EscapeDetect,
            Trigger = TriggerKind.AppEvent,
            TriggerEvents = ["frame.end"],
            InitialSize = 512 * 1024,
            MaxSize = 2 * 1024 * 1024,
            AllowPinning = false,
            AllowFinalizers = false,
            EscapePolicy = EscapePolicy.PromoteTo(2),
        },
        new MemorySpaceConfig
        {
            Id = 2,
            Name = "persistent",
            Allocator = AllocatorKind.BumpPointer,
            Collector = CollectorKind.IncrementalMark,
            Barrier = BarrierKind.RememberedSet,
            Trigger = TriggerKind.PressureThreshold,
            PressureThreshold = 0.7,
            IncrementalBudget = TimeSpan.FromMilliseconds(1), // strict 1ms
            InitialSize = 64 * 1024 * 1024,   // 64MB
            MaxSize = 512 * 1024 * 1024,       // 512MB
            AllowPinning = true,
            AllowFinalizers = true,
        },
        new MemorySpaceConfig
        {
            Id = 3,
            Name = "asset-cache",
            Allocator = AllocatorKind.FreeList,
            Collector = CollectorKind.Manual,
            Barrier = BarrierKind.None,
            Trigger = TriggerKind.Manual,
            InitialSize = 128 * 1024 * 1024,  // 128MB
            MaxSize = 1024 * 1024 * 1024,      // 1GB
            AllowPinning = true,
            AllowFinalizers = false,
        },
    ];

    public int RouteAllocation(in AllocationRequest request)
    {
        // Frame-scoped → current frame arena (alternates A/B)
        if (request.Category is TypeCategory.Ephemeral or TypeCategory.Unknown)
            return _currentFrame % 2; // 0 or 1

        // Cached assets → manual cache space
        if (request.Category == TypeCategory.Cached)
            return 3;

        // Everything else → persistent (incremental mark)
        return 2;
    }

    public int[] OnAppEvent(string eventName)
    {
        switch (eventName)
        {
            case "frame.end":
                // Free the PREVIOUS frame's arena (double-buffer: current writes, previous frees)
                int previousArena = (_currentFrame - 1) % 2;
                // Negative mod fix
                if (previousArena < 0) previousArena += 2;
                _currentFrame++;
                return [previousArena];

            case "scene.unload":
                // Manual cleanup of asset cache
                return [3];

            case "loading_screen":
                // Full cleanup opportunity
                return [2, 3];

            default:
                return [];
        }
    }

    public void OnTelemetryUpdate(ReadOnlySpan<SpaceTelemetry> spaceTelemetry)
    {
        // Could monitor: if persistent space incremental budget is consistently exhausted,
        // trigger a full collection during next loading screen
    }
}
