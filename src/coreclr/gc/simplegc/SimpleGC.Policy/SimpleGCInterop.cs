using System.Runtime.InteropServices;

namespace SimpleGC.Policy;

/// <summary>
/// Public P/Invoke surface for the simplegc runtime exports used by the
/// policy host and by workloads that want to express routing decisions.
/// </summary>
/// <remarks>
/// The library is named <c>simplegc</c> on disk (e.g. <c>simplegc.dll</c> on
/// Windows). Loading happens lazily on first call. The runtime must already
/// be hosted by simplegc (i.e. <c>DOTNET_GCName=simplegc.dll</c>) for these
/// calls to do anything meaningful.
/// </remarks>
public static class SimpleGCInterop
{
    private const string Lib = "simplegc";

    /// <summary>Turn on the per-MethodTable allocation/survival tracking
    /// table. The table is OFF by default; the policy host enables it once
    /// the runtime has finished bring-up.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_enable_mt_tracking")]
    public static extern void EnableMtTracking(int enable);

    /// <summary>Register a routing-policy callback. Called post-RestartEE
    /// at the end of every <c>simplegc_force_collect</c>. Pass
    /// <c>IntPtr.Zero</c> to clear. Returns <c>1</c> on success.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_register_routing_policy")]
    public static extern uint RegisterRoutingPolicy(uint abiVersion, IntPtr cb);

    /// <summary>Snapshot the current routing-table rows into <paramref name="buffer"/>.
    /// Returns the total row count; if it exceeds <paramref name="capacity"/>,
    /// only <paramref name="capacity"/> rows are written.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_routing_snapshot")]
    public static extern unsafe uint GetRoutingSnapshot(RoutingEntry* buffer, uint capacity);

    /// <summary>Set the route for a specific MethodTable. Returns <c>1</c>
    /// on success or <c>0</c> if the token is unknown.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_set_route")]
    public static extern uint SetRoute(ulong mtToken, byte route);

    /// <summary>Read the current route for a specific MethodTable. Returns
    /// <see cref="Route.Default"/> if the token is unknown.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_route")]
    public static extern byte GetRoute(ulong mtToken);

    /// <summary>Per-thread opt-in to chunk-boundary auto-routing — when ON,
    /// a refilled allocation chunk consults the dominant MT and routes the
    /// next chunk accordingly. Off by default.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_enable_auto_routing")]
    public static extern void EnableAutoRouting(int enable);

    /// <summary>Process-wide opt-in to chunk-boundary auto-routing. When ON,
    /// every thread's chunk refill consults the per-MT routing table and may
    /// flip the thread's <c>t_forceRoute</c> for the next chunk. Required for
    /// <see cref="SetRoute"/> writes to actually steer future allocations.
    /// Also implicitly enables MT tracking. Off by default.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_enable_auto_route_default")]
    public static extern void EnableAutoRouteDefault(int enable);

    /// <summary>Force a mark-sweep collection. Returns the number of bytes
    /// reclaimed, or a negative error code.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_collect_marksweep")]
    public static extern int CollectMarkSweep();

    /// <summary>Legacy single-bit per-thread route (back-compat for the
    /// Phase 1-4 demos). Prefer <see cref="SetThreadRoute"/>.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_route_to_marksweep")]
    public static extern void RouteToMarkSweep(int enable);

    /// <summary>Set the global default route (M1c). Pass <see cref="Route.Default"/>
    /// to clear.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_set_default_route")]
    public static extern void SetDefaultRoute(int route);

    /// <summary>Read the current global default route.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_default_route")]
    public static extern int GetDefaultRoute();

    /// <summary>Set a per-thread forced route (M1c). Highest-priority
    /// override (after the always-perm flags). Pass <see cref="Route.Default"/>
    /// to clear.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_set_thread_route")]
    public static extern void SetThreadRoute(int route);

    /// <summary>Read the per-thread forced route.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_thread_route")]
    public static extern int GetThreadRoute();

    /// <summary>Snapshot of mark-sweep region stats. Mirror of the native
    /// <c>SimpleGCMarkSweepStats</c> struct (80 bytes).</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_marksweep_stats")]
    public static extern unsafe void GetMarkSweepStats(MarkSweepStats* outBuf);

    /// <summary>Snapshot of the no-refs perm sub-arena (M1g): bytes
    /// bump-allocated and bytes committed. Used by demos to report how much
    /// of the long-lived heap landed in the optimized arena that the
    /// conservative cross-region scan is allowed to skip.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_norefsperm_stats")]
    public static extern unsafe void GetNoRefsPermStats(out ulong bytesUsed, out ulong bytesCommitted);

    /// <summary>Begin a per-request scope. All allocations until
    /// <see cref="RequestEnd"/> are routed to the request arena and are
    /// reclaimed in O(1) on scope end. Returns the snapshot bump position
    /// (informational).</summary>
    [DllImport(Lib, EntryPoint = "simplegc_request_begin")]
    public static extern ulong RequestBegin();

    /// <summary>End a per-request scope and rewind the request arena in
    /// O(1). Returns bytes freed by the rewind.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_request_end")]
    public static extern ulong RequestEnd();

    /// <summary>M1r.1: signal the dispatcher thread to invoke the
    /// registered routing-policy callback as soon as it can. Coalesces
    /// (multiple signals between callback invocations are collapsed to
    /// one). Used by <c>force_collect</c> internally; can also be
    /// called by managed code to request an immediate re-evaluation.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_signal_callback")]
    public static extern void SignalCallback();

    /// <summary>M1r.1: set the dispatcher's periodic wake interval in
    /// milliseconds. Pass <c>0</c> to disable periodic invocation
    /// (callback fires only on signals). Default is 250 ms.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_set_callback_period_ms")]
    public static extern void SetCallbackPeriodMs(uint periodMs);

    /// <summary>M1r.1: number of times the dispatcher has invoked the
    /// registered routing-policy callback since process start.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_callback_invocations")]
    public static extern ulong GetCallbackInvocations();

    /// <summary>M1r.2: aggregated substrate-wide memory pressure snapshot.
    /// Returns the ABI version on success (compare against
    /// <see cref="MemoryPressure.SupportedAbiVersion"/>) or 0 on error.
    /// The buffer must be the exact size of <see cref="MemoryPressure"/>.
    /// Lets the policy callback decide budget-aware actions (e.g. demote
    /// promoted MTs and request decommit when perm is tight).</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_memory_pressure")]
    public static extern unsafe uint GetMemoryPressure(MemoryPressure* outBuf);

    /// <summary>M1r.2: snapshot of the just-completed mark-sweep cycle.
    /// Returns the ABI version on success (compare against
    /// <see cref="LastCollect.SupportedAbiVersion"/>) or 0 on error.
    /// <see cref="LastCollect.CollectId"/> is 0 if no collect has run
    /// yet, otherwise it monotonically counts completed collects.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_get_last_collect")]
    public static extern unsafe uint GetLastCollect(LastCollect* outBuf);

    /// <summary>M1r.2: ask the substrate to return committed-but-unused
    /// mark-sweep pages back to the OS. Pass <c>0</c> for "as much as
    /// possible above headroom"; pass a non-zero hint to cap the amount.
    /// Returns the number of bytes actually decommitted (page-aligned;
    /// 0 if there is nothing to decommit or the syscall failed). The
    /// substrate retains a small headroom of committed memory above the
    /// MS bump pointer so the next chunk-take doesn't immediately
    /// re-commit.</summary>
    [DllImport(Lib, EntryPoint = "simplegc_request_decommit_marksweep")]
    public static extern ulong RequestDecommitMarkSweep(ulong hintBytes);
}

/// <summary>Mirror of the native <c>SimpleGCMarkSweepStats</c> ABI struct
/// (80 bytes, append-only on the native side).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MarkSweepStats
{
    /// <summary>Total bytes allocated into the mark-sweep region since startup.</summary>
    public ulong BytesAllocated;
    /// <summary>Bytes currently sitting on the free list.</summary>
    public ulong BytesFreelist;
    /// <summary>Bytes that survived the last mark-sweep walk.</summary>
    public ulong BytesLiveAfterCollect;
    /// <summary>Total bytes reclaimed across all collections.</summary>
    public ulong BytesCollectedTotal;
    /// <summary>Number of mark-sweep collections that have run.</summary>
    public ulong NCollections;
    /// <summary>Number of objects allocated into mark-sweep since startup.</summary>
    public ulong NObjectsAllocated;
    /// <summary>Number of objects reclaimed across all collections.</summary>
    public ulong NObjectsSwept;
    /// <summary>Total bytes committed for the mark-sweep region.</summary>
    public ulong BytesCommitted;
    /// <summary>Distance from region start to the bump pointer (high water mark).</summary>
    public ulong BytesBumped;
    /// <summary>Total bytes reserved (virtual address range) for the region.</summary>
    public ulong BytesReserved;
}

/// <summary>M1r.2: mirror of the native <c>SimpleGCMemoryPressure</c> ABI
/// struct (96 bytes). Aggregates used / committed counters across every
/// substrate region so the routing-policy callback can take budget-aware
/// decisions (e.g. stop promoting once perm is over a fraction of cap;
/// demote + decommit when working set is close to a Job-Object limit).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MemoryPressure
{
    /// <summary>The ABI version this binary expects (used by the C# side
    /// to validate against the value the native exporter writes).</summary>
    public const uint SupportedAbiVersion = 1;

    /// <summary>ABI version the substrate filled in. Compare against
    /// <see cref="SupportedAbiVersion"/>.</summary>
    public uint AbiVersion;
    /// <summary>Reserved (always 0).</summary>
    public uint Reserved;
    /// <summary>Bytes used in the bump-allocated perm arena.</summary>
    public ulong PermUsed;
    /// <summary>Bytes committed by the perm arena.</summary>
    public ulong PermCommitted;
    /// <summary>Bytes used in the per-request arena.</summary>
    public ulong RequestUsed;
    /// <summary>Bytes committed by the per-request arena.</summary>
    public ulong RequestCommitted;
    /// <summary>Bytes ever bumped in the mark-sweep region (high-water).</summary>
    public ulong MsUsed;
    /// <summary>Bytes committed by the mark-sweep region.</summary>
    public ulong MsCommitted;
    /// <summary>Bytes currently sitting on the MS free list.</summary>
    public ulong MsFreelist;
    /// <summary>Live bytes in MS as of the last completed sweep.</summary>
    public ulong MsLiveAfter;
    /// <summary>Total reserved (virtual address space) for the MS region.</summary>
    public ulong MsReserved;
    /// <summary>Bytes used in the no-refs perm sub-arena (M1g).</summary>
    public ulong NoRefsPermUsed;
    /// <summary>Bytes committed by the no-refs perm sub-arena.</summary>
    public ulong NoRefsPermCommitted;
}

/// <summary>M1r.2: mirror of the native <c>SimpleGCLastCollect</c> ABI
/// struct (64 bytes). Per-collect snapshot of timing + bytes-freed for
/// the most recently completed mark-sweep cycle. Used by the routing
/// policy to score MS efficacy ("sweep freed less than 20% of bytes
/// scanned three cycles in a row → MS is full of long-lived stuff").</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct LastCollect
{
    /// <summary>The ABI version this binary expects.</summary>
    public const uint SupportedAbiVersion = 1;

    /// <summary>ABI version the substrate filled in.</summary>
    public uint AbiVersion;
    /// <summary>Reserved (always 0).</summary>
    public uint Reserved;
    /// <summary>Monotonic count of completed collects (0 = none yet).</summary>
    public ulong CollectId;
    /// <summary>STW pause time (microseconds) for this collect.</summary>
    public ulong TotalUs;
    /// <summary>Walk-arenas phase time (microseconds).</summary>
    public ulong WalkUs;
    /// <summary>Sweep phase time (microseconds).</summary>
    public ulong SweepUs;
    /// <summary>Bytes freed by sweep (this collect only).</summary>
    public ulong BytesFreed;
    /// <summary>Live bytes in MS after this sweep.</summary>
    public ulong BytesLiveAfter;
    /// <summary>Total bytes scanned (perm + request + ms) this collect.</summary>
    public ulong BytesScanned;
}
