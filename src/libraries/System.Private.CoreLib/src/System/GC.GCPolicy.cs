// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.GCPolicy;

namespace System;

public static partial class GC
{
    /// <summary>
    /// Registers an <see cref="IGCPolicy"/> instance that will receive a
    /// callback after each garbage collection observed by the runtime.
    /// </summary>
    /// <param name="policy">The policy to register. Reference identity
    /// is used; registering the same instance twice is a no-op.</param>
    /// <remarks>
    /// The first call to this method lazily creates a dedicated dispatcher
    /// thread. Apps that never call <see cref="RegisterPolicy"/> incur no
    /// per-collection or background-thread overhead.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    internal static void RegisterPolicy(IGCPolicy policy)
        => GCPolicyDispatcher.Register(policy);

    /// <summary>
    /// Removes a previously registered <see cref="IGCPolicy"/>. After this
    /// call returns, at most one further callback may still arrive on the
    /// dispatcher thread for an in-flight notification.
    /// </summary>
    /// <param name="policy">The policy instance to remove. Reference
    /// identity is used; if <paramref name="policy"/> was not registered,
    /// the call is a no-op.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <see langword="null"/>.</exception>
    internal static void UnregisterPolicy(IGCPolicy policy)
        => GCPolicyDispatcher.Unregister(policy);

    /// <summary>
    /// Smoke-test helper: drives the policy dispatcher through the requested
    /// number of synthetic collections and reports how many callbacks fired.
    /// Intended for prototype validation only.
    /// </summary>
    internal static int RunPolicyDispatcherSelfTest(int collections)
        => GCPolicyDispatcher.RunSelfTest(collections);

    /// <summary>
    /// Smoke-test helper: drives the policy dispatcher under natural
    /// allocation pressure (no <c>GC.Collect()</c>) and reports the
    /// observed callback count alongside the runtime's own collection
    /// counters. Intended for prototype validation that the GC's
    /// post-collection signal fires for organic collections.
    /// </summary>
    /// <param name="iterations">Number of allocation rounds.</param>
    /// <param name="chunkBytes">Per-round byte[] size.</param>
    /// <returns>
    /// An array of four longs:
    ///   [0] callback count observed by the dispatcher,
    ///   [1] gen0 collection delta during the run,
    ///   [2] gen1 collection delta during the run,
    ///   [3] gen2 collection delta during the run.
    /// </returns>
    internal static long[] RunPolicyDispatcherAllocSelfTest(int iterations, int chunkBytes)
        => GCPolicyDispatcher.RunAllocSelfTest(iterations, chunkBytes);
}
