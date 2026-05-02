// Adaptive routing policy demo for simplegc.
//
// Demonstrates the M1c / "p5-adaptive" loop:
//
//   1. A managed `AdaptivePolicy` registers a callback with simplegc via
//      `simplegc_register_routing_policy`.
//   2. After every mark-sweep collection, simplegc invokes the callback
//      from a normal cooperative-mode point (post-RestartEE), so the policy
//      can read the per-MT snapshot, allocate, log, and call back into
//      simplegc_set_route to flip routing decisions.
//   3. New allocations on the same thread auto-route to whichever region
//      the policy chose (`simplegc_enable_auto_routing(1)` opts the thread
//      in; routing decisions take effect at chunk-refill boundaries).
//
// Heuristic in this demo (deliberately simple — the whole point is that the
// policy is plain C# that an app author or LLM can replace):
//
//   * If a type's `age_collections >= 2` and `survived_count > 0` (i.e. it
//     reliably survives across cycles) → mark it `MarkSweep`. New
//     allocations of that type will land in the mark-sweep region; the
//     freelist reclaims them when they finally die.
//   * Otherwise leave it on the default route.
//
// Workload runs for several cycles. Each cycle:
//   * Allocates a "scratch" buffer set that is dropped before the next
//     collection (transient).
//   * Adds a few entries to a long-lived cache that is retained.
//   * Triggers a collection.
//
// After several cycles the cache types should be re-routed to MarkSweep
// and we print the final routing decisions.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal enum Route : byte { Default = 0, ForcePerm = 1, ForceReq = 2, MarkSweep = 3 }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct RoutingEntry
{
    public ulong MtToken;
    public ulong AllocCount;
    public ulong AllocBytes;
    public ulong SurvivedCount;
    public ulong SurvivedBytes;
    public uint  AgeCollections;
    public uint  MinSize;
    public uint  MaxSize;
    public byte  CurrentRoute;
    public byte  Pad0;
    public ushort Pad1;
}

internal static class SimpleGCInterop
{
    private const string Lib = "simplegc";

    [DllImport(Lib, EntryPoint = "simplegc_enable_mt_tracking")]
    public static extern void EnableMtTracking(int enable);

    [DllImport(Lib, EntryPoint = "simplegc_register_routing_policy")]
    public static extern uint RegisterRoutingPolicy(uint abiVersion, IntPtr cb);

    [DllImport(Lib, EntryPoint = "simplegc_get_routing_snapshot")]
    public static extern unsafe uint GetRoutingSnapshot(RoutingEntry* buffer, uint capacity);

    [DllImport(Lib, EntryPoint = "simplegc_set_route")]
    public static extern uint SetRoute(ulong mtToken, byte route);

    [DllImport(Lib, EntryPoint = "simplegc_get_route")]
    public static extern byte GetRoute(ulong mtToken);

    [DllImport(Lib, EntryPoint = "simplegc_enable_auto_routing")]
    public static extern void EnableAutoRouting(int enable);

    [DllImport(Lib, EntryPoint = "simplegc_collect_marksweep")]
    public static extern int CollectMarkSweep();

    [DllImport(Lib, EntryPoint = "simplegc_route_to_marksweep")]
    public static extern void RouteToMarkSweep(int enable);
}

// Pre-allocated state used by the policy callback. Allocating during the
// callback itself is safe (it runs post-RestartEE) but we still pre-allocate
// to keep the callback bounded and predictable.
internal static class AdaptivePolicy
{
    // Constants tunable per-app; in a real LLM-authored policy these would
    // be derived from observed allocation patterns.
    public const uint AgeThreshold      = 2;     // cycles surviving before promotion
    public const int  SnapshotCapacity  = 512;
    public const ulong MinSurvivedBytesForPromote = 256;

    private static readonly RoutingEntry[] s_buffer = new RoutingEntry[SnapshotCapacity];
    private static readonly object         s_lock   = new();

    public static int Decisions { get; private set; }
    public static int Invocations { get; private set; }

    [UnmanagedCallersOnly]
    public static void OnPostCollection()
    {
        // Single-threaded by construction (callback runs on the GC thread
        // post-RestartEE), but defensive lock costs nothing.
        lock (s_lock)
        {
            Invocations++;

            uint emitted;
            unsafe
            {
                fixed (RoutingEntry* p = s_buffer)
                {
                    emitted = SimpleGCInterop.GetRoutingSnapshot(p, (uint)s_buffer.Length);
                }
            }
            uint shown = emitted < (uint)s_buffer.Length ? emitted : (uint)s_buffer.Length;

            for (uint i = 0; i < shown; i++)
            {
                ref RoutingEntry e = ref s_buffer[i];

                if (e.CurrentRoute != (byte)Route.Default)
                    continue; // already routed; leave alone

                if (e.AgeCollections >= AgeThreshold &&
                    e.SurvivedCount > 0 &&
                    e.SurvivedBytes >= MinSurvivedBytesForPromote)
                {
                    if (SimpleGCInterop.SetRoute(e.MtToken, (byte)Route.MarkSweep) == 1)
                    {
                        Decisions++;
                    }
                }
            }
        }
    }
}

internal sealed class CacheEntry
{
    public string? Key;
    public byte[]? Payload;
    public CacheEntry? Next;
}

internal sealed class ScratchBuffer
{
    public byte[]? Data;
    public int Hash;
}

internal static class Program
{
    private const int Cycles            = 6;
    private const int CacheGrowthPerCycle = 60;
    private const int ScratchPerCycle   = 1_500;

    private static int Main()
    {
        Console.WriteLine("=== simplegc adaptive-policy demo ===");
        Console.WriteLine($"cycles={Cycles}, cacheGrowthPerCycle={CacheGrowthPerCycle}, scratchPerCycle={ScratchPerCycle}");

        SimpleGCInterop.EnableMtTracking(1);

        // Auto-routing intentionally OFF for this demo: the workload
        // explicitly brackets allocations with RouteToMarkSweep so the
        // policy's set_route calls are the only mutations of routing state.
        SimpleGCInterop.EnableAutoRouting(0);

        // Per-frame route bracket inside AllocScratch / GrowCache rather
        // than a thread-wide route. This keeps the calling thread off the
        // mark-sweep route at the moment CollectMarkSweep / the policy
        // callback runs — important because the routing-policy callback
        // is invoked on the calling thread (post-RestartEE) and the
        // reverse-P/Invoke transition can allocate runtime bookkeeping
        // objects that would otherwise re-enter the mark-sweep allocator.

        // Register the policy callback.
        unsafe
        {
            delegate* unmanaged<void> fn = &AdaptivePolicy.OnPostCollection;
            uint ack = SimpleGCInterop.RegisterRoutingPolicy(abiVersion: 1, (IntPtr)fn);
            if (ack != 1)
            {
                Console.Error.WriteLine($"register_routing_policy failed: ack={ack}");
                return 2;
            }
            Console.WriteLine("policy registered.");
        }

        // The cache lives on the stack so the runtime's root scanner finds
        // it during STW. (Statics are reachable through the runtime's handle
        // table, which our standalone GC does not currently surface.)
        CacheEntry? cacheHead = null;
        for (int cycle = 1; cycle <= Cycles; cycle++)
        {
            int scratchHash = AllocScratch(ScratchPerCycle);
            cacheHead = GrowCache(cacheHead, CacheGrowthPerCycle, cycle);
            int hr = SimpleGCInterop.CollectMarkSweep();
            Console.WriteLine($"cycle {cycle}: scratchHash={scratchHash} cache={Count(cacheHead)} collect=0x{hr:x} " +
                              $"policy.invocations={AdaptivePolicy.Invocations} policy.decisions={AdaptivePolicy.Decisions}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Final routing snapshot (sorted by survived_bytes desc) ===");
        DumpSnapshot();

        GC.KeepAlive(cacheHead);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocScratch(int n)
    {
        // Scratch lives only inside this stack frame; dropped before the
        // collect that follows. Route bracket scoped to this frame so the
        // calling thread is OFF mark-sweep when the next CollectMarkSweep
        // runs (the routing-policy callback is invoked on this thread,
        // and reverse-P/Invoke transitions are simpler when the thread is
        // not actively routed at policy-invocation time).
        SimpleGCInterop.RouteToMarkSweep(1);
        try
        {
            int hash = 0;
            for (int i = 0; i < n; i++)
            {
                var sb = new ScratchBuffer
                {
                    Data = new byte[64 + (i & 63)],
                    Hash = unchecked(i * (int)0x9E3779B1)
                };
                hash ^= sb.Hash;
            }
            return hash;
        }
        finally
        {
            SimpleGCInterop.RouteToMarkSweep(0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CacheEntry GrowCache(CacheEntry? head, int n, int cycle)
    {
        SimpleGCInterop.RouteToMarkSweep(1);
        try
        {
            for (int i = 0; i < n; i++)
            {
                head = new CacheEntry
                {
                    Key     = $"c{cycle}.{i}",
                    Payload = new byte[256],
                    Next    = head
                };
            }
            return head!;
        }
        finally
        {
            SimpleGCInterop.RouteToMarkSweep(0);
        }
    }

    private static int Count(CacheEntry? head)
    {
        int c = 0;
        while (head is not null) { c++; head = head.Next; }
        return c;
    }

    private static void DumpSnapshot()
    {
        unsafe
        {
            const int Cap = 512;
            RoutingEntry* buf = stackalloc RoutingEntry[Cap];
            uint emitted = SimpleGCInterop.GetRoutingSnapshot(buf, Cap);
            uint shown = emitted < (uint)Cap ? emitted : (uint)Cap;

            var idx = new (ulong key, int i)[shown];
            for (int i = 0; i < shown; i++) idx[i] = (buf[i].SurvivedBytes, i);
            Array.Sort(idx, (a, b) => b.key.CompareTo(a.key));

            Console.WriteLine("    survived_bytes  survived_count   age   alloc_bytes  alloc_count  route");
            int rows = (int)Math.Min(12, shown);
            for (int i = 0; i < rows; i++)
            {
                ref RoutingEntry e = ref buf[idx[i].i];
                string r = e.CurrentRoute switch
                {
                    0 => "Default",
                    1 => "Perm",
                    2 => "Req",
                    3 => "MarkSweep",
                    _ => $"?{e.CurrentRoute}",
                };
                Console.WriteLine(
                    $"    {e.SurvivedBytes,14:n0}  {e.SurvivedCount,14:n0}   " +
                    $"{e.AgeCollections,3}   {e.AllocBytes,11:n0}  {e.AllocCount,11:n0}  {r}");
            }

            int routedCount = 0;
            ulong routedBytes = 0;
            for (uint i = 0; i < shown; i++)
            {
                if (buf[i].CurrentRoute == (byte)Route.MarkSweep)
                {
                    routedCount++;
                    routedBytes += buf[i].SurvivedBytes;
                }
            }
            Console.WriteLine();
            Console.WriteLine($"types routed to MarkSweep      : {routedCount}");
            Console.WriteLine($"survived_bytes in routed types : {routedBytes:n0}");
        }
    }
}
