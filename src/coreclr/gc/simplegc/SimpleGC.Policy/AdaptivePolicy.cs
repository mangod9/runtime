using System.Diagnostics;

namespace SimpleGC.Policy;

/// <summary>
/// Pay-for-play managed-poll adaptive routing policy (M1q.1).
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
/// <para><b>Pay-for-play.</b> No native callback is registered. The
/// policy spawns a <em>single</em> dedicated background thread that
/// polls <see cref="SimpleGCInterop.GetRoutingSnapshot"/> every
/// <see cref="PollInterval"/>. The thread self-stops on
/// <see cref="QuiescenceConsecutivePolls"/> consecutive polls with no new
/// decisions, on <see cref="MaxDuration"/>, or on <see cref="Stop"/>.
/// When stopped the thread exits and the only steady-state cost is the
/// per-allocation <c>mt_record</c> bookkeeping (turned on by
/// <see cref="SimpleGCInterop.EnableMtTracking"/>). Apps that don't
/// instantiate this class pay nothing: <c>g_mtTrackingEnabled</c> stays
/// 0, the per-MT table writes are skipped on the slow path.</para>
///
/// <para><b>No GC callback.</b> Unlike <see cref="PolicyHost"/>, no
/// <c>simplegc_register_routing_policy</c> registration is performed.
/// This sidesteps the <c>[UnmanagedCallersOnly]</c> re-entrancy hazard
/// observed when the post-collect callback fires on a Kestrel worker
/// thread mid-traffic and corrupts JIT/reflection caches.</para>
///
/// <para><b>Thread affinity matters.</b> The polling thread sets its
/// per-thread route to <see cref="Route.ForcePerm"/> for its lifetime so
/// any unintended allocations on the policy thread land in the perm
/// region instead of growing mark-sweep. <see cref="System.Threading.Thread"/>
/// is used (not <see cref="System.Threading.Tasks.Task"/>) because
/// <c>async</c>/<c>await</c> can migrate the continuation to a different
/// thread-pool thread, breaking that pin.</para>
///
/// <para><b>Recommended usage.</b></para>
/// <code>
/// // BEFORE host.Build()
/// SimpleGCInterop.EnableMtTracking(1);
///
/// // ... host.Build(), host.StartAsync() ...
///
/// // AFTER host.StartAsync(), before warmup
/// using var adaptive = AdaptivePolicy.Start(new AdaptivePolicy
/// {
///     AllocCountThreshold = 128,
///     PollInterval        = TimeSpan.FromMilliseconds(250),
///     MaxDuration         = TimeSpan.FromSeconds(30),
/// });
/// // self-stops on quiescence; explicit Stop() is also safe.
/// </code>
/// </remarks>
public sealed class AdaptivePolicy : IDisposable
{
    /// <summary>Snapshot buffer size. Must match the native
    /// <c>kMtTableCapacity</c> (8192) so we don't silently miss rows.</summary>
    private const int SnapshotCapacity = 8192;

    /// <summary>Minimum cumulative allocation count before a type is
    /// promoted. 128 matches M1p.0's native counter threshold.</summary>
    public uint AllocCountThreshold { get; init; } = 128;

    /// <summary>Optional minimum age (mark-sweep walks survived) before
    /// promotion. 0 = don't require survival evidence; promote on alloc
    /// count alone. Increase to 1 or 2 for workloads with transient
    /// startup bursts that should NOT be pinned to perm forever.</summary>
    public uint AgeThreshold { get; init; }

    /// <summary>Optional minimum survived bytes. Pairs with
    /// <see cref="AgeThreshold"/>.</summary>
    public ulong MinSurvivedBytes { get; init; }

    /// <summary>Route promoted MTs land in.</summary>
    public Route PromoteTo { get; init; } = Route.ForcePerm;

    /// <summary>Interval between polls.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum total duration before the policy stops itself.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Stop after this many consecutive polls produce zero new
    /// decisions (and at least <see cref="MinPollsBeforeStop"/> polls
    /// total have run). Increase for workloads where types arrive late.</summary>
    public int QuiescenceConsecutivePolls { get; init; } = 8;

    /// <summary>Minimum poll count before the quiescence rule can fire.
    /// Prevents stopping during a brief lull at startup.</summary>
    public int MinPollsBeforeStop { get; init; } = 8;

    /// <summary>If true, also call <see cref="SimpleGCInterop.EnableMtTracking"/>
    /// when the loop exits. Default is to leave tracking ON so diagnostic
    /// snapshots remain available; set false for absolute pay-for-play.</summary>
    public bool DisableMtTrackingOnStop { get; init; }

    private static AdaptivePolicy? s_running;
    private static readonly object s_lock = new();

    private System.Threading.Thread? _thread;
    private readonly System.Threading.ManualResetEventSlim _stop = new(false);
    private int _decisionsMade;
    private int _polls;
    private int _disposed;

    /// <summary>Number of MTs the policy has promoted so far.</summary>
    public int DecisionsMade => System.Threading.Volatile.Read(ref _decisionsMade);

    /// <summary>Number of polls executed (including the final no-decision ones).</summary>
    public int Polls => System.Threading.Volatile.Read(ref _polls);

    /// <summary>True once the polling thread has exited.</summary>
    public bool HasStopped => _thread is null || !_thread.IsAlive;

    /// <summary>Start the configured policy. Throws if another instance
    /// is already running (single dedicated thread is sufficient).</summary>
    public static AdaptivePolicy Start(AdaptivePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (s_lock)
        {
            if (s_running is not null)
            {
                throw new InvalidOperationException("An AdaptivePolicy is already running. Dispose it first.");
            }

            // Tracking is required for the snapshot to contain rows.
            // Auto-route-default is required for SetRoute writes to actually
            // affect future allocations (see simplegc.cpp:879 — chunk-flip
            // reads per-MT routes only when this flag is on).
            SimpleGCInterop.EnableMtTracking(1);
            SimpleGCInterop.EnableAutoRouteDefault(1);

            policy._thread = new System.Threading.Thread(policy.RunLoop)
            {
                Name        = "SimpleGC.AdaptivePolicy",
                IsBackground = true,
            };
            s_running = policy;
            policy._thread.Start();
            return policy;
        }
    }

    /// <summary>Signal the polling thread to exit. Safe to call from any
    /// thread, including the policy thread itself.</summary>
    public void Stop() => _stop.Set();

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // best-effort
        }
        _stop.Dispose();
        lock (s_lock)
        {
            if (ReferenceEquals(s_running, this)) s_running = null;
        }
    }

    private void RunLoop()
    {
        // Pin our route to perm for the lifetime of the loop so any
        // unintended allocation we do (none expected, but defence-in-depth)
        // lands in perm rather than churning mark-sweep.
        int saved = SimpleGCInterop.GetThreadRoute();
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);

        // One-shot allocation; reused across all polls.
        var buffer = new RoutingEntry[SnapshotCapacity];

        try
        {
            var sw = Stopwatch.StartNew();
            int quiet = 0;

            while (!_stop.IsSet && sw.Elapsed < MaxDuration)
            {
                int newDecisions;
                unsafe
                {
                    fixed (RoutingEntry* p = buffer)
                    {
                        newDecisions = ScanAndDecide(p, buffer.Length);
                    }
                }

                int totalPolls = System.Threading.Interlocked.Increment(ref _polls);
                if (newDecisions == 0)
                {
                    quiet++;
                }
                else
                {
                    quiet = 0;
                    System.Threading.Interlocked.Add(ref _decisionsMade, newDecisions);
                }

                if (quiet >= QuiescenceConsecutivePolls && totalPolls >= MinPollsBeforeStop)
                {
                    break;
                }

                if (_stop.Wait(PollInterval))
                {
                    break;
                }
            }
        }
        finally
        {
            SimpleGCInterop.SetThreadRoute(saved);

            if (DisableMtTrackingOnStop)
            {
                // Maximally pay-for-play: stop accumulating per-allocation
                // counters once we've made our decisions. Diagnostic
                // snapshots will still see whatever was already accumulated.
                SimpleGCInterop.EnableMtTracking(0);
            }
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
