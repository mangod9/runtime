// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace System.Runtime.GCPolicy;

/// <summary>
/// Runs registered <see cref="IGCPolicy"/> instances on a dedicated thread
/// after each garbage collection observed by the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// On Windows CoreCLR the dispatcher waits on an auto-reset event signaled
/// by the runtime at the end of every collection (GC.cpp's do_post_gc).
/// On other platforms (Unix, Mono, NativeAOT), or if the native event
/// cannot be acquired, the dispatcher falls back to polling
/// <see cref="GC.GetGCMemoryInfo"/> at <see cref="PollIntervalMilliseconds"/>
/// cadence. The contract on <see cref="IGCPolicy"/> is identical across
/// both paths.
/// </para>
/// <para>
/// Pay-for-play: when no policy is registered the dispatcher thread parks
/// on a <see cref="ManualResetEventSlim"/> and consumes no CPU. The native
/// signal event is created lazily on first registration; if no application
/// ever registers a policy the runtime never creates the event.
/// </para>
/// </remarks>
internal static partial class GCPolicyDispatcher
{
    private const int PollIntervalMilliseconds = 5;
    private const int SignalWaitTimeoutMilliseconds = 1000;

    private static readonly object s_lock = new();
    private static readonly List<IGCPolicy> s_policies = new();
    private static readonly ManualResetEventSlim s_haveWork = new(initialState: false);
    private static Thread? s_thread;
    private static long s_lastSeenIndex;

    // Set lazily by the first Register call. Null means "use polling
    // fallback" — either we're on a non-Windows platform, on Mono/NAOT,
    // or the QCall returned 0.
    private static WaitHandle? s_collectSignal;
    private static bool s_signalAcquireAttempted;

    public static void Register(IGCPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (s_lock)
        {
            if (ContainsByReference(policy))
            {
                return;
            }

            s_policies.Add(policy);

            if (s_thread is null)
            {
                if (!s_signalAcquireAttempted)
                {
                    s_signalAcquireAttempted = true;
                    s_collectSignal = TryAcquireCollectionSignal();
                }

                // Capture the baseline synchronously before starting the
                // thread so we don't replay collections that happened before
                // registration.
                s_lastSeenIndex = GC.GetGCMemoryInfo().Index;

                Thread t = new Thread(DispatcherLoop)
                {
                    Name = ".NET GC Policy Dispatcher",
                    IsBackground = true,
                };
                s_thread = t;
                t.UnsafeStart();
            }

            s_haveWork.Set();
        }
    }

    public static void Unregister(IGCPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (s_lock)
        {
            for (int i = s_policies.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_policies[i], policy))
                {
                    s_policies.RemoveAt(i);
                    break;
                }
            }

            if (s_policies.Count == 0)
            {
                s_haveWork.Reset();
            }
        }
    }

    private static bool ContainsByReference(IGCPolicy policy)
    {
        for (int i = 0; i < s_policies.Count; i++)
        {
            if (ReferenceEquals(s_policies[i], policy))
            {
                return true;
            }
        }

        return false;
    }

    private static void DispatcherLoop()
    {
        IGCPolicy[]? snapshotBuffer = null;
        WaitHandle? signal = s_collectSignal;

        while (true)
        {
            // Park while no policies are registered (true pay-for-play).
            s_haveWork.Wait();

            // Wait for the next collection. When the native signal is
            // available we wake within microseconds of GC's SetEvent;
            // otherwise we poll at PollIntervalMilliseconds. The signal's
            // generous timeout doubles as a heartbeat in case the runtime
            // and the dispatcher disagree about handle ownership.
            if (signal is not null)
            {
                signal.WaitOne(SignalWaitTimeoutMilliseconds);
            }
            else
            {
                Thread.Sleep(PollIntervalMilliseconds);
            }

            int count;
            lock (s_lock)
            {
                count = s_policies.Count;
                if (count == 0)
                {
                    // Raced with an Unregister that emptied the list; loop
                    // back to wait on s_haveWork.
                    continue;
                }

                if (snapshotBuffer is null || snapshotBuffer.Length < count)
                {
                    snapshotBuffer = new IGCPolicy[Math.Max(count, 4)];
                }

                for (int i = 0; i < count; i++)
                {
                    snapshotBuffer[i] = s_policies[i];
                }
            }

            GCMemoryInfo info = GC.GetGCMemoryInfo();
            // Index uniquely identifies a collection. Any change (up or
            // down — a BGC may publish with a smaller index than a more
            // recent FGC) signals a new collection has been observed.
            if (info.Index == s_lastSeenIndex)
            {
                continue;
            }

            s_lastSeenIndex = info.Index;

            GCCollectionInfo collectionInfo = BuildCollectionInfo(info);

            for (int i = 0; i < count; i++)
            {
                IGCPolicy p = snapshotBuffer[i];
                snapshotBuffer[i] = null!; // release reference held by buffer
                try
                {
                    p.OnPostCollection(in collectionInfo);
                }
                catch (Exception)
                {
                    // Swallow: per IGCPolicy contract, implementations must
                    // not throw, but we don't propagate if they do.
                }
            }

            // Absorb any GC the policy itself induced so we don't ping-pong
            // on policy-induced collections.
            s_lastSeenIndex = GC.GetGCMemoryInfo().Index;
        }
    }

    private static GCCollectionInfo BuildCollectionInfo(GCMemoryInfo info)
    {
        TimeSpan totalPause = TimeSpan.Zero;
        ReadOnlySpan<TimeSpan> pauses = info.PauseDurations;
        for (int i = 0; i < pauses.Length; i++)
        {
            totalPause += pauses[i];
        }

        return new GCCollectionInfo
        {
            Generation = info.Generation,
            Index = info.Index,
            IsConcurrent = info.Concurrent,
            PromotedBytes = info.PromotedBytes,
            HeapSizeBytes = info.HeapSizeBytes,
            FragmentedBytes = info.FragmentedBytes,
            TotalCommittedBytes = info.TotalCommittedBytes,
            PauseDuration = totalPause,
        };
    }

    /// <summary>
    /// Acquires the runtime's post-collection notification handle and wraps
    /// it in a <see cref="WaitHandle"/>. Returns <c>null</c> on platforms
    /// or runtimes where a native signal isn't available, in which case the
    /// dispatcher uses the polling fallback. Never throws.
    /// </summary>
#if !MONO && !NATIVEAOT
    private static NativeWaitHandle? TryAcquireCollectionSignal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr handle;
        try
        {
            handle = GetPolicyNotificationHandle();
        }
        catch
        {
            return null;
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new NativeWaitHandle(handle);
        }
        catch
        {
            return null;
        }
    }
#else
    private static WaitHandle? TryAcquireCollectionSignal() => null;
#endif

#if !MONO && !NATIVEAOT
    [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "GCInterface_GetPolicyNotificationHandle")]
    private static partial IntPtr GetPolicyNotificationHandle();

    /// <summary>
    /// Adopts an OS event handle owned by the runtime so we can call
    /// <see cref="WaitHandle.WaitOne(int)"/> on it without taking ownership
    /// (the handle is process-lifetime, closed by CRT shutdown).
    /// </summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(IntPtr handle)
        {
            SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
        }
    }
#endif

    /// <summary>
    /// Prototype self-test: registers an internal counting policy, induces
    /// the requested number of collections, waits briefly for the dispatcher
    /// to drain, and returns the number of callbacks observed.
    /// </summary>
    /// <remarks>
    /// Used by external smoke-tests that cannot construct an
    /// <see cref="IGCPolicy"/> directly because the interface is internal.
    /// </remarks>
    internal static int RunSelfTest(int collections)
    {
        SelfTestPolicy probe = new SelfTestPolicy();
        Register(probe);
        try
        {
            for (int i = 0; i < collections; i++)
            {
                GC.Collect();
                Thread.Sleep(PollIntervalMilliseconds * 4);
            }

            // Wait up to ~1s for at least one callback to land.
            for (int i = 0; i < 100 && Volatile.Read(ref probe.Callbacks) == 0; i++)
            {
                Thread.Sleep(10);
            }
        }
        finally
        {
            Unregister(probe);
        }

        return Volatile.Read(ref probe.Callbacks);
    }

    private sealed class SelfTestPolicy : IGCPolicy
    {
        public int Callbacks;

        public void OnPostCollection(in GCCollectionInfo info)
        {
            Interlocked.Increment(ref Callbacks);
        }
    }

    /// <summary>
    /// Allocator-driven smoke-test. Registers a counting policy, performs
    /// the requested number of allocations to provoke organic collections,
    /// then returns the observed callback count alongside per-generation
    /// collection deltas observed by the runtime.
    /// </summary>
    internal static long[] RunAllocSelfTest(int iterations, int chunkBytes)
    {
        SelfTestPolicy probe = new SelfTestPolicy();
        Register(probe);

        long start0 = GC.CollectionCount(0);
        long start1 = GC.CollectionCount(1);
        long start2 = GC.CollectionCount(2);

        try
        {
            // Retain ~10% of allocations so we exercise gen1/gen2 paths.
            byte[]?[] keepAlive = new byte[]?[Math.Max(iterations / 10, 1)];
            int keepIdx = 0;

            for (int i = 0; i < iterations; i++)
            {
                byte[] arr = new byte[chunkBytes];
                arr[0] = (byte)i;
                if ((i % 10) == 0)
                {
                    keepAlive[keepIdx++ % keepAlive.Length] = arr;
                }
            }

            // Allow the dispatcher to drain any pending signal.
            for (int i = 0; i < 200 && Volatile.Read(ref probe.Callbacks) == 0; i++)
            {
                Thread.Sleep(10);
            }
            Thread.Sleep(200);

            GC.KeepAlive(keepAlive);
        }
        finally
        {
            Unregister(probe);
        }

        long delta0 = GC.CollectionCount(0) - start0;
        long delta1 = GC.CollectionCount(1) - start1;
        long delta2 = GC.CollectionCount(2) - start2;

        return new long[] { Volatile.Read(ref probe.Callbacks), delta0, delta1, delta2 };
    }
}
