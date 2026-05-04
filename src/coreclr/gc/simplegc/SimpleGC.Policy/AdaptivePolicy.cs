using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimpleGC.Policy;

/// <summary>
/// Pay-for-play managed adaptive routing policy (M1q.1 / M1r.1).
/// </summary>
/// <remarks>
/// <para><b>Goal.</b> Close the gap between the static
/// <see cref="ManagedSeed"/>-style hand-curated lists and M1p.0's native
/// count-based promotion: have <em>managed</em> code observe per-MT
/// allocation behaviour during warmup and call <see cref="SimpleGCInterop.SetRoute"/>
/// for types that cross a configurable threshold. The decision lives in
/// C# (this file); the substrate is the existing simplegc routing
/// table.</para>
///
/// <para><b>M1r.1 callback architecture.</b> Earlier (M1q.1) the policy
/// ran on a managed polling thread because the original post-collect
/// callback was invoked inline on the worker thread that tripped the
/// auto-collect threshold. Under Kestrel concurrency that thread had
/// stale alloc-context state plus middleware frames above it on the
/// stack, and the [UnmanagedCallersOnly] reverse-pinvoke transition
/// corrupted JIT/reflection caches. M1r.1 introduces a dedicated
/// native dispatcher thread (parked on a condvar in simplegc.cpp) that
/// fires the callback off the alloc path; the callback runs in
/// isolation on that thread, never on a worker. AdaptivePolicy uses
/// that dispatcher: register a callback, the dispatcher invokes us
/// every <see cref="PollInterval"/> (and after collects). No managed
/// polling thread is created.</para>
///
/// <para><b>Pay-for-play.</b> Apps that don't instantiate this class
/// register no callback and pay nothing — the dispatcher thread
/// remains parked on its condvar with unbounded wait. When this class
/// is started, the dispatcher transitions to periodic mode; when
/// disposed it returns to unbounded wait.</para>
///
/// <para><b>Self-stop.</b> The callback self-unregisters once it has
/// observed <see cref="QuiescenceConsecutivePolls"/> consecutive
/// invocations with no new decisions and at least
/// <see cref="MinPollsBeforeStop"/> total invocations, OR after
/// <see cref="MaxDuration"/> wall time elapses. After self-stop the
/// dispatcher returns to unbounded wait — zero steady-state cost.</para>
///
/// <para><b>Recommended usage.</b></para>
/// <code>
/// // BEFORE host.Build()
/// using var adaptive = AdaptivePolicy.Start(new AdaptivePolicy
/// {
///     AllocCountThreshold = 128,
///     PollInterval        = TimeSpan.FromMilliseconds(250),
///     MaxDuration         = TimeSpan.FromSeconds(30),
/// });
/// // self-stops on quiescence; explicit Dispose() is also safe.
/// </code>
/// </remarks>
public sealed class AdaptivePolicy : IDisposable
{
    /// <summary>Routing-policy ABI version expected by the runtime
    /// (matches simplegc.cpp:kRoutingPolicyAbiVersion).</summary>
    private const uint RoutingPolicyAbiVersion = 1;

    /// <summary>Snapshot buffer size. Must match the native
    /// <c>kMtTableCapacity</c> (8192) so we don't silently miss rows.</summary>
    private const int SnapshotCapacity = 8192;

    /// <summary>Minimum cumulative allocation count before a type is
    /// promoted. 128 matches M1p.0's native counter threshold.</summary>
    public uint AllocCountThreshold { get; init; } = 128;

    /// <summary>Optional minimum age (mark-sweep walks survived) before
    /// promotion. 0 = don't require survival evidence; promote on alloc
    /// count alone.</summary>
    public uint AgeThreshold { get; init; }

    /// <summary>Optional minimum survived bytes. Pairs with
    /// <see cref="AgeThreshold"/>.</summary>
    public ulong MinSurvivedBytes { get; init; }

    /// <summary>Route promoted MTs land in.</summary>
    public Route PromoteTo { get; init; } = Route.ForcePerm;

    /// <summary>Interval at which the dispatcher invokes the callback
    /// when there's nothing to signal it. Maps to
    /// <c>simplegc_set_callback_period_ms</c>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum total duration before the policy self-unregisters.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Self-unregister after this many consecutive invocations
    /// produce zero new decisions (and at least
    /// <see cref="MinPollsBeforeStop"/> invocations total have run).</summary>
    public int QuiescenceConsecutivePolls { get; init; } = 8;

    /// <summary>Minimum invocation count before the quiescence rule can fire.</summary>
    public int MinPollsBeforeStop { get; init; } = 8;

    /// <summary>If true, also call <see cref="SimpleGCInterop.EnableMtTracking"/>
    /// with 0 when the loop exits. Default is to leave tracking ON so
    /// diagnostic snapshots remain available; set true for absolute
    /// pay-for-play.</summary>
    public bool DisableMtTrackingOnStop { get; init; }

    private static AdaptivePolicy? s_running;
    private static readonly object s_lock = new();

    // Pre-allocated snapshot buffer. Reused across all callback
    // invocations so the callback itself does not allocate.
    private readonly RoutingEntry[] _buffer = new RoutingEntry[SnapshotCapacity];

    private readonly Stopwatch _sw = new();
    private int _decisionsMade;
    private int _polls;
    private int _quietConsecutive;
    private int _disposed;
    private int _selfStopped;

    // M1r.3: substrate-context observation. Updated on every dispatcher
    // tick from the M1r.2 ABI getters; readable by the host for
    // end-of-run telemetry. Doubles as the trigger for our decommit
    // calls — when collect_id ticks past _lastSeenCollectId, a new
    // collect just finished and we can poke RequestDecommitMarkSweep.
    private MemoryPressure _lastPressure;
    private LastCollect    _lastCollect;
    private ulong          _lastSeenCollectId;
    private ulong          _decommitsObserved;     // # of new-collect events seen
    private ulong          _bytesDecommittedTotal; // sum of RequestDecommit returns
    private ulong          _bytesFreelistDecommittedTotal; // M1r.4: sum of RequestFreelistDecommit returns

    /// <summary>Number of MTs the policy has promoted so far.</summary>
    public int DecisionsMade => Volatile.Read(ref _decisionsMade);

    /// <summary>Number of callback invocations observed (including
    /// final no-decision ones).</summary>
    public int Polls => Volatile.Read(ref _polls);

    /// <summary>True once the callback has self-unregistered (or
    /// <see cref="Dispose"/> has run).</summary>
    public bool HasStopped => Volatile.Read(ref _selfStopped) != 0 || Volatile.Read(ref _disposed) != 0;

    /// <summary>M1r.3: most-recent <see cref="MemoryPressure"/> snapshot
    /// the policy observed (zero-initialized until the first tick).</summary>
    public MemoryPressure LastPressure { get { lock (s_lock) return _lastPressure; } }

    /// <summary>M1r.3: most-recent <see cref="LastCollect"/> snapshot.</summary>
    public LastCollect LastCollect { get { lock (s_lock) return _lastCollect; } }

    /// <summary>M1r.3: number of distinct mark-sweep collects the policy
    /// has observed via the M1r.2 LastCollect ABI.</summary>
    public ulong CollectsObserved => Volatile.Read(ref _decommitsObserved);

    /// <summary>M1r.3: cumulative bytes returned to the OS by the policy's
    /// post-collect <see cref="SimpleGCInterop.RequestDecommitMarkSweep"/>
    /// calls. May be 0 even after many collects: under the M1m sub-arena
    /// allocator the bump pointer tracks committed tightly, so trailing
    /// slack is rare. The loop is still exercised — Phase B (page-granular
    /// freelist decommit) is what will unlock real reclaims.</summary>
    public ulong BytesDecommittedTotal => Volatile.Read(ref _bytesDecommittedTotal);

    /// <summary>M1r.4: cumulative bytes returned to the OS by the policy's
    /// post-collect <see cref="SimpleGCInterop.RequestFreelistDecommit"/>
    /// calls. Counts the FULL slot size of each freed slot (header
    /// committed prefix included), which is the right number for
    /// "how much freelist capacity has been retired" rather than
    /// "literally how many bytes left RAM" — the latter is one page
    /// less per slot.</summary>
    public ulong BytesFreelistDecommittedTotal => Volatile.Read(ref _bytesFreelistDecommittedTotal);

    /// <summary>Start the configured policy. Throws if another instance
    /// is already running (the substrate has only one callback slot).</summary>
    public static AdaptivePolicy Start(AdaptivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (s_lock)
        {
            if (s_running is not null)
            {
                throw new InvalidOperationException("An AdaptivePolicy is already running. Dispose it first.");
            }

            // Bracket registration so any incidental allocation lands in perm.
            int saved = SimpleGCInterop.GetThreadRoute();
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            try
            {
                // Tracking is required for the snapshot to contain rows.
                // Auto-route-default is required for SetRoute writes to actually
                // affect future allocations (see simplegc.cpp — chunk-flip
                // reads per-MT routes only when this flag is on).
                SimpleGCInterop.EnableMtTracking(1);
                SimpleGCInterop.EnableAutoRouteDefault(1);

                // Configure the dispatcher's periodic wake interval. The
                // dispatcher also fires after collects regardless of period.
                int periodMs = (int)Math.Clamp(policy.PollInterval.TotalMilliseconds, 0, int.MaxValue);
                SimpleGCInterop.SetCallbackPeriodMs((uint)periodMs);

                s_running = policy;
                policy._sw.Start();

                unsafe
                {
                    delegate* unmanaged<void> fn = &NativeCallbackThunk;
                    uint rv = SimpleGCInterop.RegisterRoutingPolicy(RoutingPolicyAbiVersion, (IntPtr)fn);
                    if (rv != 1)
                    {
                        s_running = null;
                        throw new InvalidOperationException(
                            $"simplegc_register_routing_policy returned {rv}.");
                    }
                }

                return policy;
            }
            finally
            {
                SimpleGCInterop.SetThreadRoute(saved);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        lock (s_lock)
        {
            if (Volatile.Read(ref _selfStopped) == 0)
            {
                // Bracket the unregistration so any incidental allocation
                // lands in perm.
                int saved = SimpleGCInterop.GetThreadRoute();
                SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
                try
                {
                    SimpleGCInterop.RegisterRoutingPolicy(RoutingPolicyAbiVersion, IntPtr.Zero);
                }
                finally
                {
                    SimpleGCInterop.SetThreadRoute(saved);
                }
            }
            if (ReferenceEquals(s_running, this)) s_running = null;
        }

        if (DisableMtTrackingOnStop)
        {
            SimpleGCInterop.EnableMtTracking(0);
        }
    }

    [UnmanagedCallersOnly]
    private static void NativeCallbackThunk()
    {
        var policy = s_running;
        policy?.OnDispatcherTick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnDispatcherTick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Volatile.Read(ref _selfStopped) != 0) return;

        // Bracket policy work so anything we allocate lands in perm.
        // The dispatcher thread is auto-attached on first reverse
        // P/Invoke — its initial t_forceRoute is Default; pin to perm
        // for the duration of the callback.
        int saved = SimpleGCInterop.GetThreadRoute();
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        try
        {
            int newDecisions;
            unsafe
            {
                fixed (RoutingEntry* p = _buffer)
                {
                    newDecisions = ScanAndDecide(p, _buffer.Length);
                }
            }

            // M1r.3: read substrate context via the M1r.2 ABI. The
            // getters are pure reads (no STW) and run on the dispatcher
            // thread, so they're safe to call every tick.
            ObserveSubstrate();

            int totalPolls = Interlocked.Increment(ref _polls);
            if (newDecisions == 0)
            {
                _quietConsecutive++;
            }
            else
            {
                _quietConsecutive = 0;
                Interlocked.Add(ref _decisionsMade, newDecisions);
            }

            bool quiescent = _quietConsecutive >= QuiescenceConsecutivePolls
                          && totalPolls >= MinPollsBeforeStop;
            bool expired   = _sw.Elapsed >= MaxDuration;

            if (quiescent || expired)
            {
                SelfUnregister();
            }
        }
        catch
        {
            // Swallow. A buggy policy must never destabilize the
            // runtime; the worst-case outcome is stale routing.
        }
        finally
        {
            SimpleGCInterop.SetThreadRoute(saved);
        }
    }

    /// <summary>
    /// M1r.3: observe substrate state via the M1r.2 ABI and act on it.
    /// </summary>
    /// <remarks>
    /// <para>The <see cref="MemoryPressure"/> snapshot is cached for the
    /// host to read; future revisions can use it to gate promotion
    /// (e.g. stop promoting when <c>PermUsed &gt; 0.8 * cap</c>).</para>
    /// <para>The <see cref="LastCollect"/> snapshot is the trigger for
    /// our post-collect cleanup: when <see cref="LastCollect.CollectId"/>
    /// has advanced past <see cref="_lastSeenCollectId"/>, a new
    /// mark-sweep cycle just completed. Sweep already does
    /// trailing-dead-zone bump-rewind (see ms_sweep_locked, M1l). The
    /// only thing that can shrink resident set further today is asking
    /// the OS to return committed-but-unused pages above bump.</para>
    /// <para>Under M1m sub-arenas the bump pointer tracks committed
    /// tightly, so the call usually returns 0 — that's expected. The
    /// loop is still exercised; the value of going through it is
    /// twofold: (1) it proves the M1r.2 ABI works in a real consumer
    /// (the AdaptivePolicy callback, not just the kestrel-bench smoke),
    /// and (2) when sweep produces a substantial trailing dead run
    /// (workload with bursty, short-lived MS objects) the call returns
    /// real bytes and peakWS shrinks.</para>
    /// </remarks>
    private void ObserveSubstrate()
    {
        unsafe
        {
            MemoryPressure mp = default;
            LastCollect    lc = default;
            uint mpV = SimpleGCInterop.GetMemoryPressure(&mp);
            uint lcV = SimpleGCInterop.GetLastCollect(&lc);

            if (mpV == MemoryPressure.SupportedAbiVersion)
            {
                lock (s_lock) _lastPressure = mp;
            }

            if (lcV == LastCollect.SupportedAbiVersion)
            {
                lock (s_lock) _lastCollect = lc;

                if (lc.CollectId > _lastSeenCollectId)
                {
                    _lastSeenCollectId = lc.CollectId;
                    Interlocked.Increment(ref _decommitsObserved);

                    ulong returned = SimpleGCInterop.RequestDecommitMarkSweep(0);
                    if (returned != 0)
                    {
                        Interlocked.Add(ref _bytesDecommittedTotal, returned);
                    }

                    // M1r.4: ask the substrate to also walk the freelist
                    // and decommit interior pages of slots that have any
                    // whole-page interior. Pre-M1r.4 this would have been
                    // a no-op; under M1r.4 the typical Fortunes pattern
                    // (~28 MB freelist with 99% interior dead bytes)
                    // returns most of it.
                    ulong fl = SimpleGCInterop.RequestFreelistDecommit(0);
                    if (fl != 0)
                    {
                        Interlocked.Add(ref _bytesFreelistDecommittedTotal, fl);
                    }
                }
            }
        }
    }

    private void SelfUnregister()
    {
        if (Interlocked.Exchange(ref _selfStopped, 1) != 0) return;
        // Already inside the dispatcher callback: thread route is
        // already pinned to perm by OnDispatcherTick. Just clear the
        // native callback slot.
        SimpleGCInterop.RegisterRoutingPolicy(RoutingPolicyAbiVersion, IntPtr.Zero);

        if (DisableMtTrackingOnStop)
        {
            SimpleGCInterop.EnableMtTracking(0);
        }
    }

    /// <summary>Read the current snapshot and apply the promotion rule to
    /// each Default-routed MT that crosses the configured thresholds.
    /// Returns the number of newly applied <see cref="SimpleGCInterop.SetRoute"/>
    /// writes.</summary>
    private unsafe int ScanAndDecide(RoutingEntry* p, int capacity)
    {
        uint emitted = SimpleGCInterop.GetRoutingSnapshot(p, (uint)capacity);
        int valid = (int)Math.Min(emitted, (uint)capacity);
        int newDecisions = 0;
        byte targetRoute = (byte)PromoteTo;

        for (int i = 0; i < valid; i++)
        {
            ref RoutingEntry e = ref p[i];

            if (e.CurrentRoute != (byte)Route.Default) continue;
            if (e.AllocCount   < AllocCountThreshold)  continue;
            if (e.AgeCollections < AgeThreshold)       continue;
            if (e.SurvivedBytes  < MinSurvivedBytes)   continue;

            if (SimpleGCInterop.SetRoute(e.MtToken, targetRoute) != 0)
            {
                newDecisions++;
            }
        }

        return newDecisions;
    }
}
