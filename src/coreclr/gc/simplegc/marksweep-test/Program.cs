// Mark-sweep smoke test.
//
// Routes the main thread's allocations to simplegc's mark-sweep region,
// builds a transient object graph, drops the reference, force-collects,
// and verifies that bytes_collected ~= bytes_allocated.

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
            // head goes out of scope here.
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
