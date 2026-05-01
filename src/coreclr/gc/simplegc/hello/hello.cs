using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static unsafe class SimpleGCStrategy
{
    // Mirror of the native SimpleGCStats struct (see simplegc.cpp).
    // Field order and types must match exactly.
    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public ulong TotalAllocatedBytes;
        public ulong RequestedBytes;
        public ulong ObjectCount;
        public ulong GcCount;
        public ulong BytesSinceLastConsult;
    }

    private const uint AbiVersion = 1;

    [DllImport("simplegc", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint simplegc_register_strategy(
        uint abiVersion,
        uint structSize,
        delegate* unmanaged[Cdecl]<Stats*, int> shouldCollect);

    [DllImport("simplegc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void simplegc_get_telemetry(
        ulong* outConsultCount,
        ulong* outApprovedCount,
        ulong* outGcCount,
        ulong* outTotalAllocatedBytes,
        ulong* outRequestedBytes,
        ulong* outObjectCount);

    // Counters captured by the callback. Static fields (no allocation involved
    // when read/written) — must remain primitive.
    public static long Consults;
    public static long ApprovedHere;
    public static ulong LastSeenTotal;
    public static ulong LastSeenRequested;

    // Threshold above which the strategy approves a collection. Configurable at
    // startup. Demonstrates the app deciding when collection happens. Set above
    // the ~20 MB the runtime allocates during EE init so we can show "no" then
    // "yes" decisions cleanly.
    public static ulong CollectAboveBytes = 30UL * 1024 * 1024; // 30 MB

    // The managed callback. CRITICAL: must not allocate, must not throw, must
    // not call any framework method that may allocate (no Console.WriteLine,
    // no string formatting, no boxing).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int ShouldCollect(Stats* s)
    {
        // Primitive interlocked increments and field stores only.
        System.Threading.Interlocked.Increment(ref Consults);
        LastSeenTotal     = s->TotalAllocatedBytes;
        LastSeenRequested = s->RequestedBytes;

        if (s->TotalAllocatedBytes >= CollectAboveBytes)
        {
            System.Threading.Interlocked.Increment(ref ApprovedHere);
            return 1; // approve a collection
        }
        return 0;
    }

    public static void Register()
    {
        // Per rubber-duck guidance: pre-init the type and pre-JIT the callback
        // so the very first invocation from inside Alloc cannot trigger lazy
        // class init / JIT / tiered compilation work that would itself
        // allocate.
        RuntimeHelpers.RunClassConstructor(typeof(SimpleGCStrategy).TypeHandle);

        // Pre-JIT ShouldCollect.
        var mi = typeof(SimpleGCStrategy).GetMethod(
            nameof(ShouldCollect),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (mi != null)
        {
            RuntimeHelpers.PrepareMethod(mi.MethodHandle);
        }

        delegate* unmanaged[Cdecl]<Stats*, int> fn = &ShouldCollect;
        uint structSize = (uint)sizeof(Stats);
        uint rc = simplegc_register_strategy(AbiVersion, structSize, fn);
        if (rc != AbiVersion)
        {
            Console.WriteLine($"[hello] WARNING: simplegc_register_strategy returned {rc} (expected {AbiVersion})");
        }
        else
        {
            Console.WriteLine($"[hello] strategy registered (abi={rc}, struct size={structSize})");
        }
    }

    public static void PrintTelemetry(string label)
    {
        ulong consults = 0, approved = 0, gcCount = 0, totalAlloc = 0, requested = 0, objs = 0;
        simplegc_get_telemetry(&consults, &approved, &gcCount, &totalAlloc, &requested, &objs);
        Console.WriteLine($"[hello] {label}: consults={consults} approved={approved} gcCount={gcCount} " +
                          $"totalAlloc={totalAlloc} requested={requested} objs={objs}");
        Console.WriteLine($"[hello]   managed-side: Consults={Consults} ApprovedHere={ApprovedHere} " +
                          $"LastSeenTotal={LastSeenTotal} LastSeenRequested={LastSeenRequested}");
    }
}

class Program
{
    static int Main()
    {
        Console.WriteLine("[hello] Process started under SimpleGC");
        SimpleGCStrategy.Register();
        SimpleGCStrategy.PrintTelemetry("baseline");

        // Phase 1: allocate just under the 4 MB threshold — strategy should
        // be consulted but always say "no".
        Console.WriteLine("[hello] === phase 1: small allocations (under threshold) ===");
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            sb.Append("alloc-" + i + ";");
        }
        Console.WriteLine("[hello] Built string: " + sb.ToString());
        SimpleGCStrategy.PrintTelemetry("after small");

        // Phase 2: allocate a chunk of arrays totaling > 16 MB so the strategy
        // crosses the 30 MB threshold and starts approving.
        Console.WriteLine("[hello] === phase 2: bulk allocations (cross threshold) ===");
        const int Iterations = 400;
        long totalLen = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Each ~64 KB -> ~25.6 MB total.
            byte[] buf = new byte[64 * 1024];
            buf[0] = (byte)i;
            buf[buf.Length - 1] = (byte)~i;
            totalLen += buf.Length;
        }
        Console.WriteLine($"[hello] Allocated {Iterations} buffers, total bytes ~= {totalLen}");
        SimpleGCStrategy.PrintTelemetry("after bulk");

        // Phase 3: prove the app can dynamically change strategy by lowering
        // its own threshold mid-run. Subsequent consults will reuse the new
        // policy directly because the managed code reads CollectAboveBytes.
        Console.WriteLine("[hello] === phase 3: lower threshold to 0, allocate more ===");
        SimpleGCStrategy.CollectAboveBytes = 0;
        for (int i = 0; i < 50; i++)
        {
            byte[] buf = new byte[64 * 1024];
            buf[0] = (byte)i;
        }
        SimpleGCStrategy.PrintTelemetry("after phase 3");

        // Sum-of-squares smoke test (preserved from Phase 2 hello world).
        var arr = new int[16];
        for (int i = 0; i < arr.Length; i++) arr[i] = i * i;
        Console.WriteLine("[hello] Sum of squares = " + Sum(arr));

        SimpleGCStrategy.PrintTelemetry("final");
        Console.WriteLine("[hello] Done.");
        return 0;
    }

    static int Sum(int[] a)
    {
        int s = 0;
        foreach (var v in a) s += v;
        return s;
    }
}
