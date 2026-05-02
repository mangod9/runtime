// Policy-driven perf-over-time demo for simplegc (M1d / M1e).
//
// Compares THREE configurations of the same workload (M1f-a):
//
//   --gc-mode=simplegc-policy   (default; also: no flag)
//   --gc-mode=simplegc-nopolicy (also: --no-policy for back-compat)
//   --gc-mode=default
//
// In `default` mode the binary makes NO calls into simplegc.dll — the
// runtime is using whatever GC the host environment has loaded
// (typically the default Workstation or Server GC depending on
// DOTNET_gcServer). This lets the same workload be compared
// apples-to-apples across GCs.
//
// Honest framing for the simplegc modes: this is *site-assisted*
// routing, not autonomous adaptive allocation. The workload prefetches
// the policy's recommendation once per cycle and uses SetThreadRoute
// to bracket the long-lived allocation block. The policy *guides* the
// workload's routing decisions.
//
// Per-cycle CSV (simplegc modes only):
//   cycle,phase,alloc_us,collect_us,ms_live_bytes,ms_freelist_bytes,
//     ms_bumped_bytes,decisions,route_for_long
//
// End-of-run unified summary (ALL modes) — this is the cross-GC
// comparison surface:
//   * wall_clock_ms   — total Stopwatch ticks for the whole run
//   * total_alloc_mb  — GC.GetTotalAllocatedBytes(true) delta
//   * peak_wss_mb     — Process.WorkingSet64 sampled per cycle
//   * peak_managed_mb — GC.GetTotalMemory(false) sampled per cycle
//   * gen0/1/2 counts — GC.CollectionCount(N) deltas (default GC only)
//   * total_pause_ms  — GC.GetTotalPauseDuration() (default GC) or
//                       sum of per-cycle collect_us (simplegc modes)
//   * retained        — non-null roots[] entries (correctness check)

using System.Diagnostics;
using System.Globalization;
using SimpleGC.Policy;

internal enum GcMode
{
    SimplegcPolicy,
    SimplegcNoPolicy,
    Default,
}

internal enum Workload
{
    Current,
    Scope,
    Cache,
}

internal static class Program
{
    private const int WarmupCycles      = 3;
    private const int MeasurementCycles = 30;
    private const int TransientPerCycle = 2000;
    private const int LongLivedPerCycle = 200;
    private const int RootArrayCapacity = LongLivedPerCycle * (WarmupCycles + MeasurementCycles); // 6600

    private static GcMode ParseGcMode(string[] args)
    {
        foreach (string a in args)
        {
            if (a == "--gc-mode=default")            return GcMode.Default;
            if (a == "--gc-mode=simplegc-nopolicy")  return GcMode.SimplegcNoPolicy;
            if (a == "--gc-mode=simplegc-policy")    return GcMode.SimplegcPolicy;
            if (a == "--no-policy")                  return GcMode.SimplegcNoPolicy;
        }

        return GcMode.SimplegcPolicy;
    }

    private static Workload ParseWorkload(string[] args)
    {
        foreach (string a in args)
        {
            if (a == "--workload=current") return Workload.Current;
            if (a == "--workload=scope")   return Workload.Scope;
            if (a == "--workload=cache")   return Workload.Cache;
        }

        return Workload.Current;
    }

    private static int Main(string[] args)
    {
        GcMode mode = ParseGcMode(args);
        Workload workload = ParseWorkload(args);
        bool usePolicy   = mode == GcMode.SimplegcPolicy;
        bool useSimplegc = mode != GcMode.Default;
        Console.WriteLine($"# policy-demo: gc-mode={mode}, workload={workload}");

        return workload switch
        {
            Workload.Current => RunCurrentWorkload(mode, usePolicy, useSimplegc),
            Workload.Scope   => RunScopedWorkload (mode, usePolicy, useSimplegc),
            Workload.Cache   => RunCacheWorkload  (mode, usePolicy, useSimplegc),
            _ => 1,
        };
    }

    private static int RunCurrentWorkload(GcMode mode, bool usePolicy, bool useSimplegc)
    {
        Console.WriteLine($"# current workload: transient/cycle={TransientPerCycle}, longlived/cycle={LongLivedPerCycle}");

        // Default-route everything to mark-sweep so the demo's interesting
        // alloc traffic actually exercises the MS region. The PolicyHost
        // brackets its own bookkeeping in ForcePerm so this doesn't
        // pollute the signal.
        if (useSimplegc) SimpleGCInterop.SetDefaultRoute((int)Route.MarkSweep);

        // Turn on per-MT tracking even in --no-policy mode so the two
        // configurations differ ONLY in whether the policy callback fires
        // (not in tracking overhead).
        if (useSimplegc) SimpleGCInterop.EnableMtTracking(1);

        // Roots array: deliberately allocated WITHOUT a ForcePerm bracket so
        // it lands in mark-sweep (under the global default). This matters
        // because simplegc's mark walk only traces references *within* the
        // mark-sweep region — a perm-allocated array holding MS pointers
        // would not trace into MS, and the long-lived items would be swept
        // out from under us. (For the default GC mode this concern is moot.)
        var roots = new LongLivedItem?[RootArrayCapacity];
        int rootCursor = 0;

        PolicyHost? host = null;
        // Always start a host in simplegc modes. In --policy use BasicPolicy;
        // in --no-policy use PassivePolicy (reads snapshots but never
        // promotes). This gives both simplegc runs the same observability
        // path so any difference in collect time is attributable to routing
        // decisions, not to whether the snapshot machinery is running.
        if (useSimplegc)
        {
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
        }

        ulong longLivedMt = (ulong)typeof(LongLivedItem).TypeHandle.Value.ToInt64();

        // CSV header (bracketed in ForcePerm so the string buffers don't
        // pollute the MS working set). Suppress the per-cycle CSV in
        // default-GC mode — the column set is simplegc-specific.
        if (useSimplegc)
        {
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            Console.WriteLine("cycle,phase,alloc_us,collect_us,ms_live_bytes,ms_freelist_bytes,ms_bumped_bytes,decisions,route_for_long");
            SimpleGCInterop.SetThreadRoute((int)Route.Default);
        }
        else
        {
            Console.WriteLine("cycle,phase,alloc_us,wall_us,gen0,gen1,gen2,managed_kb,wss_kb");
        }

        // ---- Cross-GC metric setup (M1f-a) ----
        // These are captured for ALL gc-modes so the end-of-run summary
        // is directly comparable. For simplegc we additionally capture
        // the simplegc-specific collect_us trend.
        var process = Process.GetCurrentProcess();
        long crossWallStartTicks = Stopwatch.GetTimestamp();
        long crossAllocStart     = SafeTotalAllocatedBytes();
        int  crossGen0Start      = GC.CollectionCount(0);
        int  crossGen1Start      = GC.CollectionCount(1);
        int  crossGen2Start      = GC.CollectionCount(2);
        TimeSpan crossPauseStart = SafeTotalPauseDuration();
        long crossPeakWss        = 0;
        long crossPeakManaged    = 0;
        long crossSimplegcCollectUsSum = 0;

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
            // Long-lived: bracket with the policy's route hint (simplegc only).
            int savedRoute = (int)Route.Default;
            if (useSimplegc && longLivedRoute != Route.Default)
            {
                savedRoute = SimpleGCInterop.GetThreadRoute();
                SimpleGCInterop.SetThreadRoute((int)longLivedRoute);
            }
            for (int i = 0; i < LongLivedPerCycle && rootCursor < roots.Length; i++)
            {
                roots[rootCursor++] = new LongLivedItem(cycle, i, rootCursor);
            }
            if (useSimplegc && longLivedRoute != Route.Default)
            {
                SimpleGCInterop.SetThreadRoute(savedRoute);
            }
            long allocEnd = Stopwatch.GetTimestamp();
            long allocUs = (long)((allocEnd - allocStart) * tickToUs);

            // ---- Collect phase ----
            //
            // simplegc modes: explicitly drive a mark-sweep collection via the
            // P/Invoke. This is how simplegc is wired today (no auto-trigger
            // heuristic) and how the policy is invoked.
            //
            // default mode: do NOT call GC.Collect() — that would force a
            // gen2 every cycle and unfairly bias the comparison against
            // default GC, which is engineered to collect on-demand. Instead
            // we let the runtime decide and just record an "alloc-only"
            // wall time for the cycle. The cross-GC summary captures total
            // pause time independently via GC.GetTotalPauseDuration.
            long collectUs = 0;
            if (useSimplegc)
            {
                long collectStart = Stopwatch.GetTimestamp();
                int rc = SimpleGCInterop.CollectMarkSweep();
                long collectEnd = Stopwatch.GetTimestamp();
                collectUs = (long)((collectEnd - collectStart) * tickToUs);
                if (rc < 0)
                {
                    Console.Error.WriteLine($"# CollectMarkSweep failed: {rc}");
                    return 2;
                }
                if (!warmup) crossSimplegcCollectUsSum += collectUs;
            }

            // ---- Cross-GC sampling (peak WSS / managed) ----
            // Sampled every cycle; cheap.
            process.Refresh();
            long curWss     = process.WorkingSet64;
            long curManaged = GC.GetTotalMemory(forceFullCollection: false);
            if (curWss     > crossPeakWss)     crossPeakWss     = curWss;
            if (curManaged > crossPeakManaged) crossPeakManaged = curManaged;

            // ---- Measurement / logging (simplegc-bracketed) ----
            if (useSimplegc)
            {
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
            else
            {
                // Default-GC per-cycle row.
                string phase = warmup ? "warmup" : "measure";
                int gen0 = GC.CollectionCount(0) - crossGen0Start;
                int gen1 = GC.CollectionCount(1) - crossGen1Start;
                int gen2 = GC.CollectionCount(2) - crossGen2Start;
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,3},{1,7},{2,8},{3,8},{4,4},{5,4},{6,4},{7,10},{8,10}",
                    cycle, phase, allocUs, allocUs,
                    gen0, gen1, gen2,
                    curManaged / 1024, curWss / 1024));
            }
        }

        // ---- Cross-GC end-of-run snapshot (M1f-a, mode-agnostic) ----
        long crossWallEndTicks = Stopwatch.GetTimestamp();
        long wallTotalMs = (long)((crossWallEndTicks - crossWallStartTicks) * 1000.0
                                  / Stopwatch.Frequency);
        long allocTotalBytes = SafeTotalAllocatedBytes() - crossAllocStart;
        int gen0Total = GC.CollectionCount(0) - crossGen0Start;
        int gen1Total = GC.CollectionCount(1) - crossGen1Start;
        int gen2Total = GC.CollectionCount(2) - crossGen2Start;
        TimeSpan crossPauseEnd = SafeTotalPauseDuration();
        TimeSpan defaultPauseTotal = crossPauseEnd - crossPauseStart;

        // Last refresh of WSS / managed for the summary.
        process.Refresh();
        crossPeakWss     = Math.Max(crossPeakWss,     process.WorkingSet64);
        crossPeakManaged = Math.Max(crossPeakManaged, GC.GetTotalMemory(false));

        // ---- Summary (simplegc-bracketed when applicable) ----
        if (useSimplegc) SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        try
        {
            int nonNull = 0;
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] is not null) nonNull++;
            }

            // Simplegc-only: end-of-run snapshot for the LongLivedItem MT.
            ulong llSurvivedCount = 0, llSurvivedBytes = 0;
            uint  llAge = 0;
            byte  llRoute = 0;
            ulong llCount = 0, llBytes = 0;
            if (useSimplegc)
            {
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
            }

            Console.WriteLine();
            Console.WriteLine("# Summary");
            if (useSimplegc)
            {
                Console.WriteLine($"#   first measured cycle: collect={firstMeasureCollectUs}us live={firstMeasureLiveBytes}B");
                Console.WriteLine($"#   last  measured cycle: collect={lastMeasureCollectUs}us live={lastMeasureLiveBytes}B");
                if (host is not null)
                {
                    Console.WriteLine($"#   policy invocations  : {host.Invocations}");
                    Console.WriteLine($"#   policy decisions    : {host.Decisions}");
                }
            }
            Console.WriteLine($"#   live retained items : {rootCursor} long-lived objects");
            Console.WriteLine($"#   non-null root entries: {nonNull} of {roots.Length}");
            if (useSimplegc)
            {
                Console.WriteLine($"#   LongLivedItem MT diag:");
                Console.WriteLine($"#     alloc count={llCount} alloc bytes={llBytes}");
                Console.WriteLine($"#     survived count={llSurvivedCount} survived bytes={llSurvivedBytes}");
                Console.WriteLine($"#     age={llAge} route={(Route)llRoute}");
            }

            // ---- Cross-GC summary (ALL modes, fixed schema) ----
            // This is the comparison surface across gc-modes. The values
            // are emitted as a labeled CSV-ish block so external scripts
            // can grep "^XGC " to extract them.
            string xgcMode =
                mode == GcMode.SimplegcPolicy   ? "simplegc-policy" :
                mode == GcMode.SimplegcNoPolicy ? "simplegc-nopolicy" :
                                                   "default";
            long pauseTotalMs = useSimplegc
                ? crossSimplegcCollectUsSum / 1000
                : (long)defaultPauseTotal.TotalMilliseconds;
            string pauseSource = useSimplegc ? "simplegc-collect-sum" : "GC.GetTotalPauseDuration";
            Console.WriteLine();
            Console.WriteLine("# === Cross-GC summary (M1f-a) ===");
            Console.WriteLine($"XGC mode                 = {xgcMode}");
            Console.WriteLine($"XGC wall_clock_ms        = {wallTotalMs}");
            Console.WriteLine($"XGC total_alloc_mb       = {allocTotalBytes / 1024.0 / 1024.0:F3}");
            Console.WriteLine($"XGC peak_wss_mb          = {crossPeakWss / 1024.0 / 1024.0:F3}");
            Console.WriteLine($"XGC peak_managed_mb      = {crossPeakManaged / 1024.0 / 1024.0:F3}");
            Console.WriteLine($"XGC gen0_count           = {gen0Total}");
            Console.WriteLine($"XGC gen1_count           = {gen1Total}");
            Console.WriteLine($"XGC gen2_count           = {gen2Total}");
            Console.WriteLine($"XGC pause_total_ms       = {pauseTotalMs}    # source: {pauseSource}");
            Console.WriteLine($"XGC retained_items       = {rootCursor}");
            Console.WriteLine($"XGC retained_correct     = {(rootCursor == RootArrayCapacity ? "yes" : "NO")}");
        }
        finally
        {
            if (useSimplegc) SimpleGCInterop.SetThreadRoute((int)Route.Default);
        }

        host?.Dispose();
        // Keep root array alive past summary
        GC.KeepAlive(roots);

        return 0;
    }

    // ---- Cross-GC helper utilities ----
    //
    // GC.GetTotalAllocatedBytes / GC.GetTotalPauseDuration may not be
    // implemented on every standalone GC (simplegc may return 0 or zero
    // TimeSpan). Treat exceptions as "not available" so the demo still
    // works in simplegc modes.

    private static long SafeTotalAllocatedBytes()
    {
        try { return GC.GetTotalAllocatedBytes(precise: true); }
        catch { return 0; }
    }

    private static TimeSpan SafeTotalPauseDuration()
    {
        try { return GC.GetTotalPauseDuration(); }
        catch { return TimeSpan.Zero; }
    }

    // ====================================================================
    // M1f-c workload #4: per-request scoped allocation.
    //
    // Models a typical request handler: a tight burst of short-lived
    // allocations (deserialization, intermediate string/list state, etc.)
    // with NO escape — every allocation dies at the end of the request.
    //
    // simplegc modes wrap the request body in RequestBegin/RequestEnd, so
    // all allocations land in the request arena. RequestEnd rewinds the
    // bump pointer in O(1) — no marking, no sweeping, no per-object cost.
    //
    // default GC mode runs the same allocations into the normal managed
    // heap. Each request fills part of gen0; when gen0 budget hits, a
    // gen0 collection runs that has to trace all live objects to prove
    // these are dead.
    // ====================================================================

    private const int ScopeWarmupRequests = 1000;
    private const int ScopeRequests       = 50000;
    private const int ScopeAllocsPerReq   = 1000;

    private static int RunScopedWorkload(GcMode mode, bool usePolicy, bool useSimplegc)
    {
        Console.WriteLine($"# scoped workload: requests={ScopeRequests} (+{ScopeWarmupRequests} warmup), allocs/req={ScopeAllocsPerReq}");

        // For simplegc, default-route to perm so any allocation OUTSIDE a
        // request bracket lands in perm (cheap one-shot bump). Inside a
        // bracket, the request route takes over via t_activeArena.
        if (useSimplegc)
        {
            SimpleGCInterop.SetDefaultRoute((int)Route.ForcePerm);
            SimpleGCInterop.EnableMtTracking(1);
        }

        // ---- Cross-GC metric setup ----
        var process = Process.GetCurrentProcess();
        long crossWallStartTicks = Stopwatch.GetTimestamp();
        long crossAllocStart     = SafeTotalAllocatedBytes();
        int  crossGen0Start      = GC.CollectionCount(0);
        int  crossGen1Start      = GC.CollectionCount(1);
        int  crossGen2Start      = GC.CollectionCount(2);
        TimeSpan crossPauseStart = SafeTotalPauseDuration();
        long crossPeakWss        = 0;
        long crossPeakManaged    = 0;
        long crossSimplegcRequestEndUsSum = 0;

        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;

        Console.WriteLine("phase,req_index,duration_us,managed_kb,wss_kb");

        long sink = 0; // prevents dead-code elimination of allocations
        int totalRequests = ScopeWarmupRequests + ScopeRequests;
        for (int reqIndex = 0; reqIndex < totalRequests; reqIndex++)
        {
            bool warmup = reqIndex < ScopeWarmupRequests;
            long t0 = Stopwatch.GetTimestamp();

            if (useSimplegc) SimpleGCInterop.RequestBegin();

            // Per-request transient allocations. Mix of small payload sizes
            // mimics serialization intermediates / parse buffers.
            for (int i = 0; i < ScopeAllocsPerReq; i++)
            {
                var ri = new RequestItem(i, reqIndex);
                sink ^= ri.A ^ ri.B ^ ri.C;
            }

            long endStart = 0, endEnd = 0;
            if (useSimplegc)
            {
                endStart = Stopwatch.GetTimestamp();
                SimpleGCInterop.RequestEnd();
                endEnd = Stopwatch.GetTimestamp();
                if (!warmup)
                {
                    crossSimplegcRequestEndUsSum += (long)((endEnd - endStart) * tickToUs);
                }
            }

            long t1 = Stopwatch.GetTimestamp();
            long durationUs = (long)((t1 - t0) * tickToUs);

            // Sample WSS / managed every 250 requests to avoid log spam.
            if (reqIndex % 250 == 0 || reqIndex == totalRequests - 1)
            {
                process.Refresh();
                long curWss     = process.WorkingSet64;
                long curManaged = GC.GetTotalMemory(forceFullCollection: false);
                if (curWss     > crossPeakWss)     crossPeakWss     = curWss;
                if (curManaged > crossPeakManaged) crossPeakManaged = curManaged;
                string phase = warmup ? "warmup" : "measure";
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,7},{1,8},{2,12},{3,10},{4,10}",
                    phase, reqIndex, durationUs,
                    curManaged / 1024, curWss / 1024));
            }
        }

        // ---- End-of-run cross-GC summary ----
        long crossWallEndTicks = Stopwatch.GetTimestamp();
        long wallTotalMs = (long)((crossWallEndTicks - crossWallStartTicks) * 1000.0
                                  / Stopwatch.Frequency);
        long allocTotalBytes = SafeTotalAllocatedBytes() - crossAllocStart;
        int gen0Total = GC.CollectionCount(0) - crossGen0Start;
        int gen1Total = GC.CollectionCount(1) - crossGen1Start;
        int gen2Total = GC.CollectionCount(2) - crossGen2Start;
        TimeSpan crossPauseEnd = SafeTotalPauseDuration();
        TimeSpan defaultPauseTotal = crossPauseEnd - crossPauseStart;

        process.Refresh();
        crossPeakWss     = Math.Max(crossPeakWss,     process.WorkingSet64);
        crossPeakManaged = Math.Max(crossPeakManaged, GC.GetTotalMemory(false));

        string xgcMode = mode switch
        {
            GcMode.SimplegcPolicy   => "simplegc-policy",
            GcMode.SimplegcNoPolicy => "simplegc-nopolicy",
            _                       => "default",
        };
        long pauseTotalMs = useSimplegc
            ? crossSimplegcRequestEndUsSum / 1000
            : (long)defaultPauseTotal.TotalMilliseconds;
        string pauseSource = useSimplegc
            ? "simplegc-request-end-sum"
            : "GC.GetTotalPauseDuration";

        Console.WriteLine();
        Console.WriteLine("# === Cross-GC summary (M1f-c scope workload) ===");
        Console.WriteLine($"XGC mode                 = {xgcMode}");
        Console.WriteLine($"XGC workload             = scope");
        Console.WriteLine($"XGC requests             = {ScopeRequests} (+{ScopeWarmupRequests} warmup)");
        Console.WriteLine($"XGC allocs_per_request   = {ScopeAllocsPerReq}");
        Console.WriteLine($"XGC wall_clock_ms        = {wallTotalMs}");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC total_alloc_mb       = {0:F3}", allocTotalBytes / 1024.0 / 1024.0));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC peak_wss_mb          = {0:F3}", crossPeakWss / 1024.0 / 1024.0));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC peak_managed_mb      = {0:F3}", crossPeakManaged / 1024.0 / 1024.0));
        Console.WriteLine($"XGC gen0_count           = {gen0Total}");
        Console.WriteLine($"XGC gen1_count           = {gen1Total}");
        Console.WriteLine($"XGC gen2_count           = {gen2Total}");
        Console.WriteLine($"XGC pause_total_ms       = {pauseTotalMs}    # source: {pauseSource}");
        Console.WriteLine($"XGC sink                 = {sink}");

        return 0;
    }

    // ====================================================================
    // M1f-c workload #1: large static cache + transient churn.
    //
    // The pattern that simplegc-policy is designed to win:
    //   - 100k cache entries (~10 MB) live for the full process lifetime.
    //   - Transient allocations churn at ~5 MB/cycle, fully die each cycle.
    //
    // Default GC: cache promotes to gen2; every gen2 collection has to
    //   trace all 100k cache entries to prove they are live. Transient
    //   churn raises gen1/gen2 promotion pressure, causing periodic gen2.
    //
    // simplegc + explicit ForcePerm route on cache: cache lives in the
    //   perm arena, which the MS collector NEVER traces. MS collections
    //   only touch transient allocations.
    //
    // Win story: simplegc should have lower pause time per collection
    //   AND lower wall-clock at high cycle counts.
    // ====================================================================

    private const int CacheEntries        = 100_000;
    private const int CacheCycles         = 1_000;
    private const int CacheTransientPerCycle = 20_000;
    private const int CacheReadsPerCycle  = 2_000;

    private static int RunCacheWorkload(GcMode mode, bool usePolicy, bool useSimplegc)
    {
        Console.WriteLine($"# cache workload: cache={CacheEntries}, cycles={CacheCycles}, transient/cycle={CacheTransientPerCycle}, reads/cycle={CacheReadsPerCycle}");

        // ---- Cache fill ----
        //
        // For simplegc: bracket the fill in ForcePerm so cache lives in
        // perm arena (never traced by MS GC). After fill, route default
        // alloc traffic to MarkSweep so transient allocations are subject
        // to GC.
        var cache = new CacheItem[CacheEntries];
        if (useSimplegc)
        {
            int saved = SimpleGCInterop.GetThreadRoute();
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            try
            {
                for (int i = 0; i < CacheEntries; i++)
                {
                    cache[i] = new CacheItem(i);
                }
            }
            finally
            {
                SimpleGCInterop.SetThreadRoute(saved);
            }
            SimpleGCInterop.SetDefaultRoute((int)Route.ForcePerm);
        }
        else
        {
            for (int i = 0; i < CacheEntries; i++)
            {
                cache[i] = new CacheItem(i);
            }
        }

        // ---- Cross-GC metric setup ----
        var process = Process.GetCurrentProcess();
        long crossWallStartTicks = Stopwatch.GetTimestamp();
        long crossAllocStart     = SafeTotalAllocatedBytes();
        int  crossGen0Start      = GC.CollectionCount(0);
        int  crossGen1Start      = GC.CollectionCount(1);
        int  crossGen2Start      = GC.CollectionCount(2);
        TimeSpan crossPauseStart = SafeTotalPauseDuration();
        long crossPeakWss        = 0;
        long crossPeakManaged    = 0;
        long crossSimplegcReclaimUsSum = 0;

        double tickToUs = 1_000_000.0 / Stopwatch.Frequency;

        Console.WriteLine("cycle,duration_us,reclaim_us,managed_kb,wss_kb");

        var rng = new Random(42);
        long sink = 0;
        for (int cycle = 0; cycle < CacheCycles; cycle++)
        {
            long t0 = Stopwatch.GetTimestamp();

            // Simplegc: bracket the cycle in a request scope. Transient
            // allocations land in the request arena (bump alloc); the
            // RequestEnd at end of cycle rewinds the bump pointer in O(1).
            // No mark-sweep collect runs for this workload.
            if (useSimplegc) SimpleGCInterop.RequestBegin();

            // Transient churn: short-lived allocations, all die at end of cycle.
            for (int i = 0; i < CacheTransientPerCycle; i++)
            {
                var ti = new TransientLite(i, cycle);
                sink ^= ti.A;
            }

            // Cache reads: simulates app touching cache entries. Forces the
            // cache to remain reachable from a strong root chain so default
            // GC has to mark it on every gen2.
            for (int j = 0; j < CacheReadsPerCycle; j++)
            {
                int idx = rng.Next(CacheEntries);
                sink ^= cache[idx].A0;
            }

            long reclaimStart = 0, reclaimEnd = 0;
            if (useSimplegc)
            {
                reclaimStart = Stopwatch.GetTimestamp();
                SimpleGCInterop.RequestEnd();
                reclaimEnd = Stopwatch.GetTimestamp();
                long reclaimUs = (long)((reclaimEnd - reclaimStart) * tickToUs);
                crossSimplegcReclaimUsSum += reclaimUs;
            }

            long t1 = Stopwatch.GetTimestamp();
            long durationUs   = (long)((t1 - t0) * tickToUs);
            long reclaimUsRow = reclaimStart == 0 ? 0 : (long)((reclaimEnd - reclaimStart) * tickToUs);

            // Sample WSS / managed every 50 cycles.
            if (cycle % 50 == 0 || cycle == CacheCycles - 1)
            {
                process.Refresh();
                long curWss     = process.WorkingSet64;
                long curManaged = GC.GetTotalMemory(forceFullCollection: false);
                if (curWss     > crossPeakWss)     crossPeakWss     = curWss;
                if (curManaged > crossPeakManaged) crossPeakManaged = curManaged;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,5},{1,12},{2,11},{3,10},{4,10}",
                    cycle, durationUs, reclaimUsRow,
                    curManaged / 1024, curWss / 1024));
            }
        }

        // ---- End-of-run cross-GC summary ----
        long crossWallEndTicks = Stopwatch.GetTimestamp();
        long wallTotalMs = (long)((crossWallEndTicks - crossWallStartTicks) * 1000.0
                                  / Stopwatch.Frequency);
        long allocTotalBytes = SafeTotalAllocatedBytes() - crossAllocStart;
        int gen0Total = GC.CollectionCount(0) - crossGen0Start;
        int gen1Total = GC.CollectionCount(1) - crossGen1Start;
        int gen2Total = GC.CollectionCount(2) - crossGen2Start;
        TimeSpan crossPauseEnd = SafeTotalPauseDuration();
        TimeSpan defaultPauseTotal = crossPauseEnd - crossPauseStart;

        process.Refresh();
        crossPeakWss     = Math.Max(crossPeakWss,     process.WorkingSet64);
        crossPeakManaged = Math.Max(crossPeakManaged, GC.GetTotalMemory(false));

        string xgcMode = mode switch
        {
            GcMode.SimplegcPolicy   => "simplegc-policy",
            GcMode.SimplegcNoPolicy => "simplegc-nopolicy",
            _                       => "default",
        };
        long pauseTotalMs = useSimplegc
            ? crossSimplegcReclaimUsSum / 1000
            : (long)defaultPauseTotal.TotalMilliseconds;
        string pauseSource = useSimplegc
            ? "simplegc-request-end-sum"
            : "GC.GetTotalPauseDuration";

        Console.WriteLine();
        Console.WriteLine("# === Cross-GC summary (M1f-c cache workload) ===");
        Console.WriteLine($"XGC mode                 = {xgcMode}");
        Console.WriteLine($"XGC workload             = cache");
        Console.WriteLine($"XGC cache_entries        = {CacheEntries}");
        Console.WriteLine($"XGC cycles               = {CacheCycles}");
        Console.WriteLine($"XGC transient_per_cycle  = {CacheTransientPerCycle}");
        Console.WriteLine($"XGC wall_clock_ms        = {wallTotalMs}");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC total_alloc_mb       = {0:F3}", allocTotalBytes / 1024.0 / 1024.0));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC peak_wss_mb          = {0:F3}", crossPeakWss / 1024.0 / 1024.0));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "XGC peak_managed_mb      = {0:F3}", crossPeakManaged / 1024.0 / 1024.0));
        Console.WriteLine($"XGC gen0_count           = {gen0Total}");
        Console.WriteLine($"XGC gen1_count           = {gen1Total}");
        Console.WriteLine($"XGC gen2_count           = {gen2Total}");
        Console.WriteLine($"XGC pause_total_ms       = {pauseTotalMs}    # source: {pauseSource}");
        Console.WriteLine($"XGC sink                 = {sink}");

        GC.KeepAlive(cache);
        return 0;
    }
}

// Compact reference type for the scoped workload. Three longs + header
// = ~32 bytes per object. 5000 requests x 500 allocs x 32 B = ~80 MB
// of allocation traffic, all of which dies at request boundary.
internal sealed class RequestItem
{
    public long A, B, C;
    public RequestItem(long a, long b)
    {
        A = a;
        B = b;
        C = a ^ b;
    }
}

// Cache entry for the cache workload. ~96 bytes payload + 24 byte header
// = ~120 bytes per object. 100k entries = ~12 MB of long-lived data.
internal sealed class CacheItem
{
    public long A0, A1, A2, A3, A4, A5, A6, A7;
    public long B0, B1;
    public CacheItem(int seed)
    {
        A0 = seed;
        A1 = seed * 31L;
    }
}

// Lighter-weight transient than TransientItem (used to keep churn high
// without dominating wall clock with allocator overhead).
internal sealed class TransientLite
{
    public long A;
    public long B;
    public TransientLite(int i, int cycle)
    {
        A = i;
        B = cycle;
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
