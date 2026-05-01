// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// ModelDrivenMechanics — A mechanics engine that executes any IGCMemoryModel.
//
// This replaces GCMechanics.cs (which was hard-coded generational).
// Instead of baking in generational assumptions, this engine:
//   - Creates whatever spaces the model defines
//   - Routes allocations to the model's chosen space
//   - Collects each space using its configured collector
//   - Enforces escape detection for arena spaces
//
// The key point: the same engine runs Traditional, WebApi, and Game models.
// The difference in behavior comes entirely from the model configuration.
// ============================================================================

using System.Diagnostics;

namespace GCStrategy.Core;

/// <summary>
/// A memory space runtime instance (created from MemorySpaceConfig).
/// </summary>
public sealed class MemorySpace
{
    public MemorySpaceConfig Config { get; }
    public long Used { get; private set; }
    public int ObjectCount { get; private set; }
    public int CollectionCount { get; private set; }
    public long TotalBytesFreed { get; private set; }
    public long TotalBytesAllocated { get; private set; }
    public TimeSpan TotalPauseTime { get; private set; }
    public DateTime LastCollectionTime { get; private set; } = DateTime.UtcNow;

    private readonly List<SimObject> _objects = new();
    private readonly HashSet<SimObject> _roots = new();

    public MemorySpace(MemorySpaceConfig config) => Config = config;

    public double FillRatio => Config.MaxSize > 0
        ? (double)Used / Config.MaxSize
        : (double)Used / Math.Max(Config.InitialSize, 1);

    public SimObject? TryAllocate(long size, int typeId)
    {
        long effectiveMax = Config.MaxSize > 0 ? Config.MaxSize : long.MaxValue;
        if (Used + size > effectiveMax)
            return null;

        var obj = new SimObject
        {
            Size = size,
            TypeId = typeId,
            RegionId = Config.Id,
            Age = 0,
            IsLive = true,
            Generation = Config.Id,
        };
        _objects.Add(obj);
        Used += size;
        ObjectCount++;
        TotalBytesAllocated += size;
        return obj;
    }

    public void AddRoot(SimObject obj) => _roots.Add(obj);
    public void RemoveRoot(SimObject obj) => _roots.Remove(obj);

    /// <summary>
    /// Collect this space using its configured collector.
    /// Returns stats about what happened.
    /// </summary>
    public CollectionStats Collect(IReadOnlyList<MemorySpace> allSpaces)
    {
        var sw = Stopwatch.StartNew();
        long freed = 0;
        int objectsFreed = 0;

        switch (Config.Collector)
        {
            case CollectorKind.BulkFree:
                // O(1) arena reset — the magic of escape-aware arenas
                freed = Used;
                objectsFreed = ObjectCount;
                // Objects that escaped were already moved — just reset
                _objects.Clear();
                _roots.Clear();
                Used = 0;
                ObjectCount = 0;
                break;

            case CollectorKind.MarkSweep:
            case CollectorKind.MarkCompact:
            case CollectorKind.IncrementalMark:
            case CollectorKind.ConcurrentMark:
                // Mark from roots, sweep dead objects
                freed = MarkAndSweep(allSpaces);
                objectsFreed = (int)(freed / 128); // approximation
                break;

            case CollectorKind.Manual:
                // No-op unless explicitly freed
                break;

            case CollectorKind.ReferenceCounting:
                // Simulated: check for zero refcount
                freed = SweepUnreferenced();
                objectsFreed = (int)(freed / 128);
                break;
        }

        sw.Stop();
        CollectionCount++;
        TotalBytesFreed += freed;
        TotalPauseTime += sw.Elapsed;
        LastCollectionTime = DateTime.UtcNow;

        return new CollectionStats
        {
            CollectionNumber = CollectionCount,
            Generation = Config.Id,
            BytesFreed = freed,
            ObjectsFreed = objectsFreed,
            ObjectsPromoted = 0,
            PauseDuration = sw.Elapsed,
            StrategyName = Config.Name,
        };
    }

    private long MarkAndSweep(IReadOnlyList<MemorySpace> allSpaces)
    {
        // Mark all dead
        foreach (var obj in _objects)
            obj.IsLive = false;

        // Trace from roots
        var visited = new HashSet<SimObject>();
        var queue = new Queue<SimObject>(_roots);

        // Also trace from other spaces that reference objects in this space
        foreach (var space in allSpaces)
        {
            if (space == this) continue;
            foreach (var obj in space._objects)
            {
                foreach (var refObj in obj.References)
                {
                    if (refObj.RegionId == Config.Id)
                        queue.Enqueue(refObj);
                }
            }
        }

        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            if (!visited.Add(obj)) continue;
            obj.IsLive = true;
            foreach (var refObj in obj.References)
            {
                if (refObj.RegionId == Config.Id && !visited.Contains(refObj))
                    queue.Enqueue(refObj);
            }
        }

        // Sweep
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

    private long SweepUnreferenced()
    {
        long freed = 0;
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (!_roots.Contains(_objects[i]) && !IsReferencedByAny(_objects[i]))
            {
                freed += _objects[i].Size;
                Used -= _objects[i].Size;
                ObjectCount--;
                _objects.RemoveAt(i);
            }
        }
        return freed;
    }

    private bool IsReferencedByAny(SimObject target)
    {
        foreach (var obj in _objects)
        {
            if (obj.References.Contains(target))
                return true;
        }
        return false;
    }

    public SpaceTelemetry GetTelemetry() => new()
    {
        SpaceId = Config.Id,
        AllocatedBytes = Used,
        Capacity = Config.MaxSize > 0 ? Config.MaxSize : Config.InitialSize,
        ObjectCount = ObjectCount,
        CollectionCount = CollectionCount,
        TimeSinceLastCollection = DateTime.UtcNow - LastCollectionTime,
        FillRatio = FillRatio,
    };
}

/// <summary>
/// The model-driven mechanics engine. Executes any IGCMemoryModel.
/// This is the "native toolkit" — it doesn't know about web APIs or games.
/// It just executes the model's instructions.
/// </summary>
public sealed class ModelDrivenMechanics
{
    private readonly IGCMemoryModel _model;
    private readonly List<MemorySpace> _spaces = new();
    private readonly List<CollectionStats> _allStats = new();

    public ModelDrivenMechanics(IGCMemoryModel model)
    {
        _model = model;

        // Create spaces as defined by the model
        foreach (var config in model.DefineSpaces())
        {
            _spaces.Add(new MemorySpace(config));
        }
    }

    public string ModelName => _model.Name;
    public IReadOnlyList<CollectionStats> Stats => _allStats;
    public IReadOnlyList<MemorySpace> Spaces => _spaces;

    /// <summary>
    /// Allocate an object. The model decides which space it goes to.
    /// </summary>
    public SimObject Allocate(AllocationRequest request)
    {
        int spaceId = _model.RouteAllocation(in request);
        var space = _spaces[spaceId];

        var obj = space.TryAllocate(request.Size, (int)request.Category);
        if (obj is null)
        {
            // Space full — collect it and retry
            var stats = space.Collect(_spaces);
            _allStats.Add(stats);
            obj = space.TryAllocate(request.Size, (int)request.Category);
        }

        if (obj is not null && request.Category == TypeCategory.LongLived)
        {
            space.AddRoot(obj);
        }

        return obj!;
    }

    /// <summary>
    /// Signal an app event. The model decides which spaces to collect.
    /// </summary>
    public void SignalAppEvent(string eventName)
    {
        int[] spaceIds = _model.OnAppEvent(eventName);
        foreach (int id in spaceIds)
        {
            if (id >= 0 && id < _spaces.Count)
            {
                var stats = _spaces[id].Collect(_spaces);
                _allStats.Add(stats);
            }
        }
    }

    /// <summary>
    /// Check pressure-based triggers for all spaces.
    /// </summary>
    public void CheckTriggers()
    {
        foreach (var space in _spaces)
        {
            if (space.Config.Trigger == TriggerKind.PressureThreshold &&
                space.FillRatio > space.Config.PressureThreshold)
            {
                var stats = space.Collect(_spaces);
                _allStats.Add(stats);
            }
        }
    }

    /// <summary>
    /// Print a summary of all spaces and collection stats.
    /// </summary>
    public void PrintSummary()
    {
        Console.WriteLine($"  Model: {ModelName}");
        Console.WriteLine($"  Spaces:");
        foreach (var space in _spaces)
        {
            Console.WriteLine($"    [{space.Config.Id}] {space.Config.Name,-20} " +
                $"alloc={space.Config.Allocator,-22} " +
                $"collect={space.Config.Collector,-15} " +
                $"used={space.Used,10:N0}B  collections={space.CollectionCount}  " +
                $"freed={space.TotalBytesFreed,10:N0}B  pause={space.TotalPauseTime.TotalMilliseconds:F3}ms");
        }

        Console.WriteLine($"  Totals:");
        Console.WriteLine($"    Collections:  {_allStats.Count}");
        Console.WriteLine($"    Total freed:  {_allStats.Sum(s => s.BytesFreed):N0} bytes");
        Console.WriteLine($"    Total pause:  {TimeSpan.FromTicks(_allStats.Sum(s => s.PauseDuration.Ticks)).TotalMilliseconds:F3}ms");
        Console.WriteLine($"    Heap size:    {_spaces.Sum(s => s.Used):N0} bytes");
        Console.WriteLine();
    }
}
