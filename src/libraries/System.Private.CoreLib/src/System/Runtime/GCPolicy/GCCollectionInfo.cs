// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.GCPolicy;

/// <summary>
/// Information about a garbage collection that has just completed.
/// </summary>
/// <remarks>
/// Passed to <see cref="IGCPolicy.OnPostCollection(in GCCollectionInfo)"/>
/// after each collection so the policy can observe collection-driven
/// signals (promotion volume, frequency, heap pressure) without polling
/// global counters. All values reflect post-collect state.
/// </remarks>
internal readonly struct GCCollectionInfo
{
    /// <summary>The generation that was collected (0, 1, or 2).</summary>
    public int Generation { get; init; }

    /// <summary>Monotonic index identifying this collection within the process.</summary>
    public long Index { get; init; }

    /// <summary><see langword="true"/> if this was a background (concurrent)
    /// collection; <see langword="false"/> for a blocking foreground collection.</summary>
    public bool IsConcurrent { get; init; }

    /// <summary>Bytes that survived this collection (i.e. were promoted out
    /// of the collected generation, or remained in gen2).</summary>
    public long PromotedBytes { get; init; }

    /// <summary>Total managed heap size in bytes after this collection.</summary>
    public long HeapSizeBytes { get; init; }

    /// <summary>Bytes of fragmentation (free space inside committed segments)
    /// after this collection.</summary>
    public long FragmentedBytes { get; init; }

    /// <summary>Total bytes committed by the GC after this collection.</summary>
    public long TotalCommittedBytes { get; init; }

    /// <summary>Sum of stop-the-world pauses recorded for this collection.
    /// For a foreground collection this is the single pause; for a background
    /// collection this is the sum of the initial and final marks. Excludes
    /// time spent in concurrent (non-pausing) work.</summary>
    public TimeSpan PauseDuration { get; init; }
}
