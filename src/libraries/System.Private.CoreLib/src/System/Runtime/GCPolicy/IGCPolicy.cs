// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.GCPolicy;

/// <summary>
/// Application-supplied callback the GC invokes to inform high-level
/// policy decisions (e.g. routing long-lived types away from gen0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> The runtime invokes <see cref="OnPostCollection"/>
/// on a dedicated dispatcher thread that is created lazily when a policy
/// is first registered. The callback never runs on a worker / mutator
/// thread, never inside the stop-the-world pause, and never on the
/// finalizer thread. It is therefore safe to allocate, take locks, and
/// call back into <see cref="System.GC"/> APIs from this method, but
/// implementations should keep work bounded — collections may queue up
/// if the policy is slow.
/// </para>
/// <para>
/// <b>Pay-for-play.</b> When no policy is registered the GC performs no
/// per-collection bookkeeping for the policy hook and the dispatcher
/// thread is not created. Apps that do not call
/// <c>GC.RegisterPolicy</c> pay zero cost.
/// </para>
/// <para>
/// <b>Contract.</b> Implementations must not throw — exceptions
/// propagating out of <see cref="OnPostCollection"/> will be swallowed
/// by the dispatcher and logged but the runtime makes no other guarantee
/// about behavior in that case. Long-running or blocking implementations
/// will not stall the GC itself but may starve the dispatcher and cause
/// later collection notifications to be coalesced.
/// </para>
/// <para>
/// <b>Delivery semantics.</b> The dispatcher is best-effort: a callback
/// corresponds to the most recently published collection observed at the
/// time of dispatch. Multiple collections completing between dispatcher
/// wake-ups may be coalesced into a single notification representing the
/// latest one. A background collection that publishes its result with a
/// smaller <see cref="GCCollectionInfo.Index"/> than an earlier foreground
/// collection is still delivered (publication order, not numeric index,
/// drives delivery). Implementations should not rely on receiving a
/// notification for every collection.
/// </para>
/// </remarks>
internal interface IGCPolicy
{
    /// <summary>
    /// Called on the dispatcher thread for each collection observed.
    /// </summary>
    /// <param name="info">Information about the collection that just finished.</param>
    void OnPostCollection(in GCCollectionInfo info);
}
