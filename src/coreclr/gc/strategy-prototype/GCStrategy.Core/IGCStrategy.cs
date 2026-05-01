// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// IGCStrategy — The interface an LLM-generated strategy must implement.
//
// This represents the POLICY layer. The mechanics layer (native C++ in production)
// calls into this interface to make decisions. The strategy never touches raw
// memory directly — it only returns decisions that the mechanics executes.
// ============================================================================

namespace GCStrategy.Core;

/// <summary>
/// Telemetry snapshot the mechanics provides to the strategy before asking for a decision.
/// </summary>
public readonly record struct GCTelemetry
{
    /// <summary>Total bytes currently allocated across all regions.</summary>
    public long TotalAllocatedBytes { get; init; }

    /// <summary>Number of live objects (post-mark).</summary>
    public int LiveObjectCount { get; init; }

    /// <summary>Number of dead objects (post-mark, pre-sweep).</summary>
    public int DeadObjectCount { get; init; }

    /// <summary>Total regions currently in use.</summary>
    public int ActiveRegionCount { get; init; }

    /// <summary>Bytes allocated since last collection.</summary>
    public long BytesSinceLastCollection { get; init; }

    /// <summary>Time since last collection.</summary>
    public TimeSpan TimeSinceLastCollection { get; init; }

    /// <summary>Current memory pressure (0.0 to 1.0).</summary>
    public double MemoryPressure { get; init; }

    /// <summary>Application-defined context (e.g., "request completed", "frame boundary").</summary>
    public string? AppEvent { get; init; }

    /// <summary>Generation of the region being considered (0, 1, 2).</summary>
    public int Generation { get; init; }
}

/// <summary>
/// A collection plan returned by the strategy. The mechanics executes this plan.
/// </summary>
public readonly record struct CollectionPlan
{
    public CollectionAction Action { get; init; }
    public int TargetGeneration { get; init; }
    public TimeSpan? TimeBudget { get; init; }
    public int[]? RegionIdsToFree { get; init; }
    public bool Compact { get; init; }

    public static CollectionPlan None => new() { Action = CollectionAction.None };

    public static CollectionPlan Collect(int generation, bool compact = false)
        => new() { Action = CollectionAction.Collect, TargetGeneration = generation, Compact = compact };

    public static CollectionPlan FreeRegions(int[] regionIds)
        => new() { Action = CollectionAction.BulkFreeRegions, RegionIdsToFree = regionIds };

    public static CollectionPlan IncrementalMark(TimeSpan budget)
        => new() { Action = CollectionAction.IncrementalMark, TimeBudget = budget };
}

public enum CollectionAction
{
    None,
    Collect,
    BulkFreeRegions,
    IncrementalMark,
}

/// <summary>
/// Promotion decision for a single object.
/// </summary>
public enum PromotionDecision
{
    /// <summary>Keep in current generation.</summary>
    Keep,
    /// <summary>Promote to next generation.</summary>
    Promote,
    /// <summary>Object is dead, reclaim.</summary>
    Dead,
}

/// <summary>
/// The strategy interface. An LLM generates an implementation of this.
/// The mechanics layer calls these methods — the strategy MUST NOT allocate managed objects.
/// </summary>
public interface IGCStrategy
{
    /// <summary>
    /// Called when an allocation triggers a threshold. Should the GC collect?
    /// </summary>
    CollectionPlan ShouldCollect(in GCTelemetry telemetry);

    /// <summary>
    /// Called for each surviving object after mark phase. Should it be promoted?
    /// </summary>
    PromotionDecision ShouldPromote(int objectAge, long objectSize, int currentGeneration);

    /// <summary>
    /// Called when an app event occurs (e.g., request boundary, frame tick).
    /// Gives the strategy a chance to trigger proactive collection.
    /// </summary>
    CollectionPlan OnAppEvent(string eventName, in GCTelemetry telemetry);

    /// <summary>
    /// Strategy name for diagnostics.
    /// </summary>
    string Name { get; }
}
