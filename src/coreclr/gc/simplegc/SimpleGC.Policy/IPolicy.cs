namespace SimpleGC.Policy;

/// <summary>
/// Context passed to <see cref="IPolicy.OnPostCollection"/>. Provides a
/// mutable view of the routing decisions the policy can update; exposes the
/// snapshot read from the native runtime.
/// </summary>
public interface IPolicyContext
{
    /// <summary>Number of valid rows in <see cref="Snapshot"/>.</summary>
    int SnapshotCount { get; }

    /// <summary>The snapshot buffer. Only the first <see cref="SnapshotCount"/>
    /// entries are valid; the rest are stale data.</summary>
    ReadOnlySpan<RoutingEntry> Snapshot { get; }

    /// <summary>Set the route for the given MT. Returns <c>true</c> if the
    /// MT was recognized, <c>false</c> otherwise.</summary>
    bool SetRoute(ulong mtToken, Route route);

    /// <summary>Number of times <see cref="SetRoute"/> has flipped a
    /// route to non-default since the policy was started.</summary>
    int Decisions { get; }

    /// <summary>Number of times <see cref="IPolicy.OnPostCollection"/> has
    /// been invoked since the policy was started.</summary>
    int Invocations { get; }
}

/// <summary>
/// A managed policy that is consulted after every mark-sweep collection.
/// Implementations look at per-MethodTable behavior (allocation count,
/// survival, age) and decide which region future allocations should land in.
/// </summary>
/// <remarks>
/// Policies must be:
/// <list type="bullet">
///   <item><description><b>Bounded.</b> The callback runs on the GC thread post-RestartEE
///     and should return promptly.</description></item>
///   <item><description><b>Reentrancy-safe.</b> Other threads may be allocating during
///     the call. <see cref="PolicyHost"/> wraps invocation in a thread-route
///     bracket so allocations made <em>by the policy</em> land in perm.</description></item>
///   <item><description><b>Idempotent.</b> Re-running a decision must produce the same
///     result given the same snapshot.</description></item>
/// </list>
/// </remarks>
public interface IPolicy
{
    /// <summary>Called once per mark-sweep collection after the runtime has
    /// resumed managed threads. Use <paramref name="ctx"/> to read the
    /// per-MT snapshot and update routing decisions.</summary>
    void OnPostCollection(IPolicyContext ctx);
}
