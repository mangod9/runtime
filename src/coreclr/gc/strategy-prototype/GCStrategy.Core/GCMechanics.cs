// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// GCMechanics — Simulated native heap mechanics.
//
// In production this would be C++ code (the verified mechanics layer).
// Here we simulate it in C# using NativeMemory to demonstrate the architecture.
// The mechanics layer:
//   - Manages raw memory (regions, pages)
//   - Performs mark/sweep/compact operations
//   - Calls into IGCStrategy for DECISIONS only
// ============================================================================

using System.Runtime.InteropServices;

namespace GCStrategy.Core;

/// <summary>
/// Represents a heap region (a contiguous block of memory holding objects).
/// </summary>
public sealed class HeapRegion
{
    public int Id { get; }
    public int Generation { get; set; }
    public long Capacity { get; }
    public long Used { get; private set; }
    public int ObjectCount { get; private set; }
    public int LiveObjectCount { get; private set; }

    private readonly List<SimObject> _objects = new();

    public HeapRegion(int id, long capacity, int generation)
    {
        Id = id;
        Capacity = capacity;
        Generation = generation;
    }

    public SimObject? TryAllocate(long size, int typeId)
    {
        if (Used + size > Capacity)
            return null;

        var obj = new SimObject
        {
            Size = size,
            TypeId = typeId,
            RegionId = Id,
            Age = 0,
            IsLive = true,
            Generation = Generation,
        };
        _objects.Add(obj);
        Used += size;
        ObjectCount++;
        LiveObjectCount++;
        return obj;
    }

    public IReadOnlyList<SimObject> Objects => _objects;

    public void MarkAllDead()
    {
        foreach (var obj in _objects)
            obj.IsLive = false;
        LiveObjectCount = 0;
    }

    public void MarkLive(SimObject obj)
    {
        if (!obj.IsLive)
        {
            obj.IsLive = true;
            LiveObjectCount++;
        }
    }

    public long Sweep()
    {
        long freed = 0;
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (!_objects[i].IsLive)
            {
                freed += _objects[i].Size;
                Used -= _objects[i].Size;
                ObjectCount--;
                _objects.RemoveAt(i);
            }
        }
        return freed;
    }

    public void Reset()
    {
        _objects.Clear();
        Used = 0;
        ObjectCount = 0;
        LiveObjectCount = 0;
    }
}

/// <summary>
/// A simulated managed object.
/// </summary>
public sealed class SimObject
{
    public long Size { get; init; }
    public int TypeId { get; init; }
    public int RegionId { get; set; }
    public int Age { get; set; }
    public bool IsLive { get; set; }
    public int Generation { get; set; }
    public List<SimObject> References { get; } = new();
}

/// <summary>
/// Statistics from a GC collection event.
/// </summary>
public readonly record struct CollectionStats
{
    public int CollectionNumber { get; init; }
    public int Generation { get; init; }
    public long BytesFreed { get; init; }
    public int ObjectsFreed { get; init; }
    public int ObjectsPromoted { get; init; }
    public TimeSpan PauseDuration { get; init; }
    public string StrategyName { get; init; }
}

/// <summary>
/// The mechanics layer — simulates native heap management.
/// Delegates all policy decisions to an IGCStrategy.
/// </summary>
public sealed class GCMechanics
{
    private readonly IGCStrategy _strategy;
    private readonly List<HeapRegion> _regions = new();
    private readonly List<SimObject> _roots = new();
    private readonly List<CollectionStats> _stats = new();

    private int _nextRegionId;
    private long _totalAllocatedSinceLastGC;
    private DateTime _lastCollectionTime = DateTime.UtcNow;
    private int _collectionCount;

    private const long DefaultRegionSize = 1024 * 1024; // 1MB regions

    public GCMechanics(IGCStrategy strategy)
    {
        _strategy = strategy;
        // Start with one Gen0 region
        _regions.Add(new HeapRegion(_nextRegionId++, DefaultRegionSize, generation: 0));
    }

    public IReadOnlyList<CollectionStats> Stats => _stats;
    public string StrategyName => _strategy.Name;

    public long TotalAllocated => _regions.Sum(r => r.Used);
    public int TotalObjects => _regions.Sum(r => r.ObjectCount);
    public int TotalRegions => _regions.Count;

    /// <summary>
    /// Allocate an object. May trigger collection based on strategy decision.
    /// </summary>
    public SimObject Allocate(long size, int typeId, bool isRoot = false)
    {
        // Try to allocate in existing Gen0 region
        var gen0Region = _regions.FirstOrDefault(r => r.Generation == 0);
        var obj = gen0Region?.TryAllocate(size, typeId);

        if (obj is null)
        {
            // Allocation failed — ask strategy what to do
            var telemetry = BuildTelemetry();
            var plan = _strategy.ShouldCollect(telemetry);
            ExecutePlan(plan);

            // Try again with a new region if needed
            gen0Region = _regions.FirstOrDefault(r => r.Generation == 0 && r.Used + size <= r.Capacity);
            if (gen0Region is null)
            {
                gen0Region = new HeapRegion(_nextRegionId++, DefaultRegionSize, generation: 0);
                _regions.Add(gen0Region);
            }

            obj = gen0Region.TryAllocate(size, typeId);
        }

        if (obj is not null)
        {
            _totalAllocatedSinceLastGC += size;
            if (isRoot)
                _roots.Add(obj);
        }

        return obj!;
    }

    /// <summary>
    /// Signal an application event to the strategy.
    /// </summary>
    public void SignalAppEvent(string eventName)
    {
        var telemetry = BuildTelemetry(eventName);
        var plan = _strategy.OnAppEvent(eventName, telemetry);
        ExecutePlan(plan);
    }

    /// <summary>
    /// Add/remove root references (simulates stack/static roots).
    /// </summary>
    public void AddRoot(SimObject obj) => _roots.Add(obj);
    public void RemoveRoot(SimObject obj) => _roots.Remove(obj);

    private void ExecutePlan(CollectionPlan plan)
    {
        switch (plan.Action)
        {
            case CollectionAction.None:
                break;

            case CollectionAction.Collect:
                PerformCollection(plan.TargetGeneration, plan.Compact);
                break;

            case CollectionAction.BulkFreeRegions:
                BulkFreeRegions(plan.RegionIdsToFree ?? []);
                break;

            case CollectionAction.IncrementalMark:
                // Simplified: just do a gen0 collect with time tracking
                PerformCollection(0, compact: false);
                break;
        }
    }

    private void PerformCollection(int generation, bool compact)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // MARK PHASE: Mark all objects in target generation as dead
        var targetRegions = _regions.Where(r => r.Generation <= generation).ToList();
        foreach (var region in targetRegions)
            region.MarkAllDead();

        // Trace from roots
        var visited = new HashSet<SimObject>();
        var queue = new Queue<SimObject>(_roots.Where(o => o.Generation <= generation));
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            if (!visited.Add(obj)) continue;

            var region = _regions.FirstOrDefault(r => r.Id == obj.RegionId);
            region?.MarkLive(obj);

            foreach (var refObj in obj.References)
            {
                if (refObj.Generation <= generation && !visited.Contains(refObj))
                    queue.Enqueue(refObj);
            }
        }

        // PROMOTION: Ask strategy about each surviving object
        int promoted = 0;
        foreach (var region in targetRegions)
        {
            foreach (var obj in region.Objects.Where(o => o.IsLive))
            {
                var decision = _strategy.ShouldPromote(obj.Age, obj.Size, obj.Generation);
                switch (decision)
                {
                    case PromotionDecision.Promote:
                        obj.Generation = Math.Min(obj.Generation + 1, 2);
                        obj.Age++;
                        promoted++;
                        break;
                    case PromotionDecision.Keep:
                        obj.Age++;
                        break;
                }
            }
        }

        // SWEEP PHASE: Free dead objects
        long totalFreed = 0;
        int objectsFreed = 0;
        foreach (var region in targetRegions)
        {
            int before = region.ObjectCount;
            totalFreed += region.Sweep();
            objectsFreed += before - region.ObjectCount;
        }

        sw.Stop();
        _collectionCount++;
        _totalAllocatedSinceLastGC = 0;
        _lastCollectionTime = DateTime.UtcNow;

        _stats.Add(new CollectionStats
        {
            CollectionNumber = _collectionCount,
            Generation = generation,
            BytesFreed = totalFreed,
            ObjectsFreed = objectsFreed,
            ObjectsPromoted = promoted,
            PauseDuration = sw.Elapsed,
            StrategyName = _strategy.Name,
        });
    }

    private void BulkFreeRegions(int[] regionIds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long freed = 0;
        int objectsFreed = 0;

        foreach (var id in regionIds)
        {
            var region = _regions.FirstOrDefault(r => r.Id == id);
            if (region is not null)
            {
                freed += region.Used;
                objectsFreed += region.ObjectCount;
                region.Reset();
            }
        }

        sw.Stop();
        _collectionCount++;
        _totalAllocatedSinceLastGC = 0;
        _lastCollectionTime = DateTime.UtcNow;

        _stats.Add(new CollectionStats
        {
            CollectionNumber = _collectionCount,
            Generation = 0,
            BytesFreed = freed,
            ObjectsFreed = objectsFreed,
            ObjectsPromoted = 0,
            PauseDuration = sw.Elapsed,
            StrategyName = _strategy.Name,
        });
    }

    private GCTelemetry BuildTelemetry(string? appEvent = null)
    {
        return new GCTelemetry
        {
            TotalAllocatedBytes = TotalAllocated,
            LiveObjectCount = _regions.Sum(r => r.LiveObjectCount),
            DeadObjectCount = _regions.Sum(r => r.ObjectCount - r.LiveObjectCount),
            ActiveRegionCount = _regions.Count,
            BytesSinceLastCollection = _totalAllocatedSinceLastGC,
            TimeSinceLastCollection = DateTime.UtcNow - _lastCollectionTime,
            MemoryPressure = (double)TotalAllocated / (_regions.Count * DefaultRegionSize),
            AppEvent = appEvent,
            Generation = 0,
        };
    }
}
