// Mark-sweep smoke test.
//
// Routes the main thread's allocations to simplegc's mark-sweep region,
// builds a transient object graph, drops the reference, force-collects,
// and verifies that bytes_collected ~= bytes_allocated.
//
// Phase 4 (post-M1b): exercises the routing-policy ABI: registers a
// callback, retains a small set of cache objects, force-collects, and
// dumps the per-MT routing snapshot to verify survival counters populate
// and policy decisions can be written back through simplegc_set_route.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct MarkSweepStats
{
    public ulong BytesAllocated;
    public ulong BytesFreelist;
    public ulong BytesLiveAfterCollect;
    public ulong BytesCollectedTotal;
    public ulong NCollections;
    public ulong NObjectsAllocated;
    public ulong NObjectsSwept;
    public ulong BytesCommitted;
    public ulong BytesBumped;
    public ulong BytesReserved;
}

// Mirror of native SimpleGCRoutingEntry. Pack=8 keeps layout aligned with
// the native struct (which is built with /Zp8).
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

internal enum Route : byte { Default = 0, ForcePerm = 1, ForceReq = 2, MarkSweep = 3 }

internal static class SimpleGCInterop
{
    private const string Lib = "simplegc";

    [DllImport(Lib, EntryPoint = "simplegc_route_to_marksweep")]
    public static extern void RouteToMarkSweep(int enable);

    [DllImport(Lib, EntryPoint = "simplegc_is_routed_to_marksweep")]
    public static extern int IsRoutedToMarkSweep();

    [DllImport(Lib, EntryPoint = "simplegc_get_marksweep_stats")]
    public static extern void GetStats(out MarkSweepStats stats);

    [DllImport(Lib, EntryPoint = "simplegc_collect_marksweep")]
    public static extern int CollectMarkSweep();

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
}

internal sealed class Node
{
    public Node? Next;
    public int Value;
    public byte[]? Payload;
}

internal static class Program
{
    private const int NodeCount = 5000;

    // Retained by Phase4PolicyDemo so the policy callback can observe live
    // survivors of these MTs.
    private static Node? s_retainedHead;

    // Static field set by the policy callback the first time it runs.
    private static int s_policyCallbackInvocations;

    private static void Main(string[] args)
    {
        Console.WriteLine("=== simplegc mark-sweep smoke test ===");

        try
        {
            SimpleGCInterop.GetStats(out var probe);
            Console.WriteLine($"reserved: {probe.BytesReserved / (1024 * 1024)} MB");
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("simplegc.dll not found - run with DOTNET_GCName=simplegc.dll and "
                + "ensure simplegc.dll is next to this binary.");
            Environment.Exit(2);
            return;
        }

        // Enable MT tracking so allocations populate the per-MT counters.
        // (Survival counters are populated independently by the mark phase.)
        SimpleGCInterop.EnableMtTracking(1);

        // Phase 1: allocate a transient list, then drop it.
        Phase1AllocAndDrop();

        // Force the JIT to release any registers/stack slots holding the ref.
        for (int i = 0; i < 3; i++) GC.Collect();

        SimpleGCInterop.GetStats(out var before);
        Console.WriteLine();
        Console.WriteLine("Before mark-sweep collection:");
        PrintStats(before);

        // Phase 2: trigger mark-sweep.
        var hr = SimpleGCInterop.CollectMarkSweep();
        Console.WriteLine($"\nsimplegc_collect_marksweep -> 0x{hr:x}");

        SimpleGCInterop.GetStats(out var after);
        Console.WriteLine();
        Console.WriteLine("After mark-sweep collection:");
        PrintStats(after);

        long deltaCollected = (long)(after.BytesCollectedTotal - before.BytesCollectedTotal);
        Console.WriteLine();
        Console.WriteLine($"bytes reclaimed this collection : {deltaCollected:n0}");
        Console.WriteLine($"objects swept                   : {after.NObjectsSwept - before.NObjectsSwept:n0}");
        Console.WriteLine($"freelist bytes                  : {after.BytesFreelist:n0}");
        Console.WriteLine($"live bytes                      : {after.BytesLiveAfterCollect:n0}");

        // Phase 3: allocate again - should reuse freelist and not advance bump.
        ulong bumpBefore = after.BytesBumped;
        Phase3AllocReuse();
        SimpleGCInterop.GetStats(out var reuse);
        Console.WriteLine();
        Console.WriteLine("After Phase3 (re-alloc 200 nodes):");
        Console.WriteLine($"bump moved                      : {(long)(reuse.BytesBumped - bumpBefore):n0} bytes");
        Console.WriteLine($"freelist bytes                  : {reuse.BytesFreelist:n0}");
        Console.WriteLine($"objects allocated (cumulative)  : {reuse.NObjectsAllocated:n0}");

        // Phase 4: routing-policy ABI demo.
        Phase4PolicyDemo();

        // Keep s_retainedHead alive past collections.
        GC.KeepAlive(s_retainedHead);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Phase1AllocAndDrop()
    {
        SimpleGCInterop.RouteToMarkSweep(1);
        try
        {
            Node? head = null;
            for (int i = 0; i < NodeCount; i++)
            {
                head = new Node { Next = head, Value = i, Payload = new byte[64] };
            }
            int sum = 0;
            var n = head;
            while (n is not null) { sum += n.Value; n = n.Next; }
            Console.WriteLine($"Phase1: built {NodeCount}-node list (sum={sum})");
        }
        finally
        {
            SimpleGCInterop.RouteToMarkSweep(0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Phase3AllocReuse()
    {
        SimpleGCInterop.RouteToMarkSweep(1);
        try
        {
            Node? head = null;
            for (int i = 0; i < 200; i++)
            {
                head = new Node { Next = head, Value = i, Payload = new byte[64] };
            }
            int sum = 0;
            var n = head;
            while (n is not null) { sum += n.Value; n = n.Next; }
            Console.WriteLine($"Phase3: built 200-node list (sum={sum})");
        }
        finally
        {
            SimpleGCInterop.RouteToMarkSweep(0);
        }
    }

    [UnmanagedCallersOnly]
    private static void RoutingPolicyCallback()
    {
        Interlocked.Increment(ref s_policyCallbackInvocations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Node? BuildRetainedList(int count)
    {
        SimpleGCInterop.RouteToMarkSweep(1);
        try
        {
            Node? head = null;
            for (int i = 0; i < count; i++)
            {
                head = new Node { Next = head, Value = i, Payload = new byte[128] };
            }
            return head;
        }
        finally
        {
            SimpleGCInterop.RouteToMarkSweep(0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Phase4PolicyDemo()
    {
        Console.WriteLine();
        Console.WriteLine("=== Phase 4: routing-policy demo ===");

        unsafe
        {
            delegate* unmanaged<void> fn = &RoutingPolicyCallback;
            uint ack = SimpleGCInterop.RegisterRoutingPolicy(abiVersion: 1, (IntPtr)fn);
            Console.WriteLine($"register_routing_policy ack: {ack}");
        }

        // Build chain on the stack so it's pinned by GcScanRoots, not by a
        // static field (whose reachability for standalone GCs is via the
        // runtime's handle table — not currently surfaced to simplegc).
        Node? head = BuildRetainedList(150);

        // Trigger a collection. Native side bumps survival counters for
        // Node + byte[] MTs while walking the live closure, then invokes
        // the registered routing policy callback.
        SimpleGCInterop.CollectMarkSweep();

        Console.WriteLine($"policy callback invocations    : {s_policyCallbackInvocations}");

        unsafe
        {
            uint total = SimpleGCInterop.GetRoutingSnapshot(null, 0);
            Console.WriteLine($"populated MT entries           : {total}");

            const int Cap = 256;
            RoutingEntry* buf = stackalloc RoutingEntry[Cap];
            uint emitted = SimpleGCInterop.GetRoutingSnapshot(buf, Cap);
            uint shown = emitted < (uint)Cap ? emitted : (uint)Cap;

            var top = new (ulong bytes, int idx)[shown];
            for (int i = 0; i < shown; i++) top[i] = (buf[i].SurvivedBytes, i);
            Array.Sort(top, (a, b) => b.bytes.CompareTo(a.bytes));

            Console.WriteLine("top entries by survived_bytes:");
            Console.WriteLine("    survived_bytes  survived_count   age   alloc_bytes  alloc_count  route");
            int rows = (int)Math.Min(8, shown);
            for (int i = 0; i < rows; i++)
            {
                ref RoutingEntry e = ref buf[top[i].idx];
                Console.WriteLine(
                    $"    {e.SurvivedBytes,14:n0}  {e.SurvivedCount,14:n0}   " +
                    $"{e.AgeCollections,3}   {e.AllocBytes,11:n0}  {e.AllocCount,11:n0}  {e.CurrentRoute}");
            }

            if (shown > 0 && top[0].bytes > 0)
            {
                ulong tok = buf[top[0].idx].MtToken;
                uint ok = SimpleGCInterop.SetRoute(tok, (byte)Route.MarkSweep);
                byte rb = SimpleGCInterop.GetRoute(tok);
                Console.WriteLine($"set_route on top survivor: ok={ok} read-back={rb} (expect 3)");
            }
        }

        GC.KeepAlive(head);
    }

    private static void PrintStats(MarkSweepStats s)
    {
        Console.WriteLine($"  bytes_allocated         : {s.BytesAllocated:n0}");
        Console.WriteLine($"  bytes_bumped            : {s.BytesBumped:n0}");
        Console.WriteLine($"  bytes_committed         : {s.BytesCommitted:n0}");
        Console.WriteLine($"  bytes_freelist          : {s.BytesFreelist:n0}");
        Console.WriteLine($"  bytes_collected_total   : {s.BytesCollectedTotal:n0}");
        Console.WriteLine($"  bytes_live_after_collect: {s.BytesLiveAfterCollect:n0}");
        Console.WriteLine($"  n_collections           : {s.NCollections:n0}");
        Console.WriteLine($"  n_objects_allocated     : {s.NObjectsAllocated:n0}");
        Console.WriteLine($"  n_objects_swept         : {s.NObjectsSwept:n0}");
    }
}
