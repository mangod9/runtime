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
