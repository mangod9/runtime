// Policy-driven perf-over-time demo for simplegc (M1d).
//
// Compares two configurations of the same workload:
//
//   --no-policy : default route is MarkSweep; no managed policy. Long-lived
//                 objects pile up in the mark-sweep region every cycle, and
//                 collect cost grows linearly with population.
//
//   (default)   : default route is MarkSweep AND a managed BasicPolicy is
//                 active. Once a type's age/survival profile is recognized
//                 (after ~2 collections of stable survival), the policy
//                 promotes future allocations of that type to the perm
//                 region. Mark-sweep working set plateaus; collect cost
//                 stops growing.
//
// Honest framing: this is *site-assisted* routing, not autonomous
// adaptive allocation. The workload prefetches the policy's recommendation
// once per cycle and uses SetThreadRoute to bracket the long-lived
// allocation block. The policy *guides* the workload's routing decisions.
//
// Per-cycle CSV output:
//   cycle,phase,alloc_us,collect_us,ms_live_bytes,ms_freelist_bytes,
//     ms_bumped_bytes,decisions,route_for_long
//
// where phase ∈ {warmup, measure}. Three warmup cycles run first to let
// the runtime / first-allocation paths settle.

using System.Diagnostics;
using System.Globalization;
using SimpleGC.Policy;

internal static class Program
{
    private const int WarmupCycles      = 3;
    private const int MeasurementCycles = 30;
    private const int TransientPerCycle = 2000;
    private const int LongLivedPerCycle = 200;
    private const int RootArrayCapacity = LongLivedPerCycle * (WarmupCycles + MeasurementCycles); // 6600

    private static int Main(string[] args)
    {
        bool usePolicy = !args.Contains("--no-policy");
        Console.WriteLine($"# policy-demo: usePolicy={usePolicy}, transient/cycle={TransientPerCycle}, longlived/cycle={LongLivedPerCycle}");

        // Default-route everything to mark-sweep so the demo's interesting
        // alloc traffic actually exercises the MS region. The PolicyHost
        // brackets its own bookkeeping in ForcePerm so this doesn't
        // pollute the signal.
        SimpleGCInterop.SetDefaultRoute((int)Route.MarkSweep);

        // Turn on per-MT tracking even in --no-policy mode so the two
        // configurations differ ONLY in whether the policy callback fires
        // (not in tracking overhead).
        SimpleGCInterop.EnableMtTracking(1);

        // Roots array: deliberately allocated WITHOUT a ForcePerm bracket so
        // it lands in mark-sweep (under the global default). This matters
        // because simplegc's mark walk only traces references *within* the
        // mark-sweep region — a perm-allocated array holding MS pointers
        // would not trace into MS, and the long-lived items would be swept
        // out from under us.
        var roots = new LongLivedItem?[RootArrayCapacity];
        int rootCursor = 0;

        PolicyHost? host = null;
        // Always start a host. In --policy mode use BasicPolicy; in --no-policy
        // mode use PassivePolicy (reads snapshots but never promotes). This
        // gives both runs the same observability path so any difference in
        // collect time is attributable to routing decisions, not to whether
        // the snapshot machinery is running.
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        try
        {
            IPolicy policy = usePolicy
                ? new BasicPolicy { PromoteTo = Route.ForcePerm }
                : new PassivePolicy();
            host = PolicyHost.Start(policy);
            Console.WriteLine($"# policy-demo: {policy.GetType().Name} started");
        }
        finally
        {
            SimpleGCInterop.SetThreadRoute((int)Route.Default);
        }

        ulong longLivedMt = (ulong)typeof(LongLivedItem).TypeHandle.Value.ToInt64();

        // CSV header (bracketed in ForcePerm so the string buffers don't
        // pollute the MS working set).
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        Console.WriteLine("cycle,phase,alloc_us,collect_us,ms_live_bytes,ms_freelist_bytes,ms_bumped_bytes,decisions,route_for_long");
        SimpleGCInterop.SetThreadRoute((int)Route.Default);

        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;
        long firstMeasureCollectUs = -1;
        long lastMeasureCollectUs  = -1;
        long firstMeasureLiveBytes = -1;
        long lastMeasureLiveBytes  = -1;

        int totalCycles = WarmupCycles + MeasurementCycles;
        for (int cycle = 0; cycle < totalCycles; cycle++)
        {
            bool warmup = cycle < WarmupCycles;

            // Prefetch the policy's recommendation for the LongLivedItem
            // type ONCE per cycle, so route changes apply only at cycle
            // boundaries (no flapping mid-loop).
            Route longLivedRoute = host?.GetRouteFor<LongLivedItem>() ?? Route.Default;

            // ---- Allocation phase ----
            long allocStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < TransientPerCycle; i++)
            {
                // Transient: stays at the global default (MarkSweep).
                _ = new TransientItem(i, i + 1, i + 2);
            }
            // Long-lived: bracket with the policy's route hint.
            int savedRoute = (int)Route.Default;
            if (longLivedRoute != Route.Default)
            {
                savedRoute = SimpleGCInterop.GetThreadRoute();
                SimpleGCInterop.SetThreadRoute((int)longLivedRoute);
            }
            for (int i = 0; i < LongLivedPerCycle && rootCursor < roots.Length; i++)
            {
                roots[rootCursor++] = new LongLivedItem(cycle, i, rootCursor);
            }
            if (longLivedRoute != Route.Default)
            {
                SimpleGCInterop.SetThreadRoute(savedRoute);
            }
            long allocEnd = Stopwatch.GetTimestamp();
            long allocUs = (long)((allocEnd - allocStart) * tickToUs);

            // ---- Collect phase ----
            long collectStart = Stopwatch.GetTimestamp();
            int rc = SimpleGCInterop.CollectMarkSweep();
            long collectEnd = Stopwatch.GetTimestamp();
            long collectUs = (long)((collectEnd - collectStart) * tickToUs);
            if (rc < 0)
            {
                Console.Error.WriteLine($"# CollectMarkSweep failed: {rc}");
                return 2;
            }

            // ---- Measurement / logging (bracketed in ForcePerm) ----
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            try
            {
                MarkSweepStats stats;
                unsafe { SimpleGCInterop.GetMarkSweepStats(&stats); }

                int decisions = host?.Decisions ?? 0;
                string phase = warmup ? "warmup" : "measure";
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,3},{1,7},{2,8},{3,8},{4,12},{5,12},{6,12},{7,4},{8}",
                    cycle, phase, allocUs, collectUs,
                    stats.BytesLiveAfterCollect, stats.BytesFreelist, stats.BytesBumped,
                    decisions, longLivedRoute));

                if (!warmup)
                {
                    if (firstMeasureCollectUs < 0)
                    {
                        firstMeasureCollectUs = collectUs;
                        firstMeasureLiveBytes = (long)stats.BytesLiveAfterCollect;
                    }
                    lastMeasureCollectUs = collectUs;
                    lastMeasureLiveBytes = (long)stats.BytesLiveAfterCollect;
                }
            }
            finally
            {
                SimpleGCInterop.SetThreadRoute((int)Route.Default);
            }
        }

        // ---- Summary (bracketed in ForcePerm) ----
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        try
        {
            int nonNull = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] is not null) nonNull++;
            }

            // End-of-run snapshot: was the LongLivedItem MT fully traced?
            // This is independent of ms_live_bytes accounting; it reads the
            // per-MT survived counters that ms_promote_callback populates.
            ulong llSurvivedCount = 0, llSurvivedBytes = 0;
            uint  llAge = 0;
            byte  llRoute = 0;
            ulong llCount = 0, llBytes = 0;
            unsafe
            {
                const int Capacity = 256;
                RoutingEntry* buf = stackalloc RoutingEntry[Capacity];
                uint total = SimpleGCInterop.GetRoutingSnapshot(buf, Capacity);
                int n = (int)Math.Min(total, (uint)Capacity);
                for (int i = 0; i < n; i++)
                {
                    if (buf[i].MtToken == longLivedMt)
                    {
                        llSurvivedCount = buf[i].SurvivedCount;
                        llSurvivedBytes = buf[i].SurvivedBytes;
                        llAge           = buf[i].AgeCollections;
                        llRoute         = buf[i].CurrentRoute;
                        llCount         = buf[i].AllocCount;
                        llBytes         = buf[i].AllocBytes;
                        break;
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("# Summary");
            Console.WriteLine($"#   first measured cycle: collect={firstMeasureCollectUs}us live={firstMeasureLiveBytes}B");
            Console.WriteLine($"#   last  measured cycle: collect={lastMeasureCollectUs}us live={lastMeasureLiveBytes}B");
            if (host is not null)
            {
                Console.WriteLine($"#   policy invocations  : {host.Invocations}");
                Console.WriteLine($"#   policy decisions    : {host.Decisions}");
            }
            Console.WriteLine($"#   live retained items : {rootCursor} long-lived objects");
            Console.WriteLine($"#   non-null root entries: {nonNull} of {roots.Length}");
            Console.WriteLine($"#   LongLivedItem MT diag:");
            Console.WriteLine($"#     alloc count={llCount} alloc bytes={llBytes}");
            Console.WriteLine($"#     survived count={llSurvivedCount} survived bytes={llSurvivedBytes}");
            Console.WriteLine($"#     age={llAge} route={(Route)llRoute}");
        }
        finally
        {
            SimpleGCInterop.SetThreadRoute((int)Route.Default);
        }

        host?.Dispose();
        // Keep root array alive past summary
        GC.KeepAlive(roots);
        return 0;
    }
}

// Reference-light scalar-only sealed types so allocation traffic is
// dominated by the MT we want to study, not by string/byte[] payloads
// that have their own MTs.

internal sealed class TransientItem
{
    public long A, B, C;
    public TransientItem(long a, long b, long c) { A = a; B = b; C = c; }
}

internal sealed class LongLivedItem
{
    // Sized to make per-item bytes visible against the transient noise floor:
    // 16 longs + header = ~144B. Thirty cycles x 200/cycle x ~144B = ~864 KB
    // accumulated in the no-policy MS region by the end of the run.
    public long A0, A1, A2, A3, A4, A5, A6, A7;
    public long B0, B1, B2, B3, B4, B5, B6, B7;
    public LongLivedItem(long a, long b, long c)
    {
        A0 = a; A1 = b; A2 = c;
    }
}

/// <summary>Diagnostic policy that reads snapshots but never promotes.
/// Used in --no-policy runs to keep both code paths identical except for
/// the policy decision itself.</summary>
internal sealed class PassivePolicy : IPolicy
{
    public ulong LastLongLivedSurvived;
    public ulong LastLongLivedSurvivedBytes;
    public uint  LastLongLivedAge;
    public ulong LastLongLivedMt;
    public PassivePolicy()
    {
        LastLongLivedMt = (ulong)typeof(LongLivedItem).TypeHandle.Value.ToInt64();
    }
    public void OnPostCollection(IPolicyContext ctx)
    {
        var snap = ctx.Snapshot;
        for (int i = 0; i < snap.Length; i++)
        {
            ref readonly RoutingEntry e = ref snap[i];
            if (e.MtToken == LastLongLivedMt)
            {
                LastLongLivedSurvived      = e.SurvivedCount;
                LastLongLivedSurvivedBytes = e.SurvivedBytes;
                LastLongLivedAge           = e.AgeCollections;
                return;
            }
        }
    }
}
