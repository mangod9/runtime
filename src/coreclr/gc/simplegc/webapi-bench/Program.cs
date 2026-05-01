// SimpleGC Phase 4: webapi-style benchmark
//
// Simulates a single-threaded webapi server processing N short-lived requests.
// Each request allocates ~5 KB across ~13 small objects (typical of a simple
// JSON request handler) and discards every reference at end of request.
//
// Two run modes (selected by env var SIMPLEGC_USE_ARENA=1):
//   * default  - rely on the GC (default coreclr GC, or simplegc with no arena)
//   * arena    - bracket each request with simplegc_request_begin/end so the
//                request arena is rewound in O(1) at end of request
//
// Metrics:
//   * Per-request elapsed (Stopwatch ticks) - p50, p99, p99.9, max
//   * Total runtime
//   * GC.CollectionCount(0/1/2)
//   * GC.GetTotalAllocatedBytes(true)
//   * Process.PeakWorkingSet64
//   * Strategy/arena telemetry from simplegc (when present)

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleGCWebApiBench;

internal static unsafe class SimpleGC
{
    // Phase 3 strategy register / telemetry
    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int simplegc_register_strategy(uint abiVersion, uint structSize, IntPtr shouldCollect);

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void simplegc_get_telemetry(
        out ulong consultCount, out ulong approvedCount, out ulong gcCount,
        out ulong totalAllocatedBytes, out ulong requestedBytes, out ulong objectCount);

    // Phase 4 arena
    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong simplegc_request_begin();

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong simplegc_request_end();

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void simplegc_get_arena_stats(
        out ulong permUsed, out ulong permCommitted,
        out ulong requestUsed, out ulong requestCommitted);

    public static bool IsLoaded { get; private set; }

    static SimpleGC()
    {
        // We're running under simplegc only when DOTNET_GCName names it.
        // The DLL is also present in the shared-fx folder when running under
        // default GC so that DllImport doesn't fail to load it - but we must
        // not call into it because GC_Initialize never ran and the arena
        // globals are uninitialized.
        string gcName = Environment.GetEnvironmentVariable("DOTNET_GCName") ?? "";
        IsLoaded = gcName.Equals("simplegc.dll", StringComparison.OrdinalIgnoreCase)
                || gcName.Equals("simplegc", StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RequestBegin()
    {
        if (IsLoaded) simplegc_request_begin();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong RequestEnd()
    {
        return IsLoaded ? simplegc_request_end() : 0;
    }

    public static (ulong permUsed, ulong permCommitted, ulong reqUsed, ulong reqCommitted) ArenaStats()
    {
        if (!IsLoaded) return (0, 0, 0, 0);
        simplegc_get_arena_stats(out var pu, out var pc, out var ru, out var rc);
        return (pu, pc, ru, rc);
    }

    public static (ulong consults, ulong approved, ulong gcs, ulong totalAlloc, ulong requested, ulong objects) Telemetry()
    {
        if (!IsLoaded) return (0, 0, 0, 0, 0, 0);
        simplegc_get_telemetry(out var c, out var a, out var g, out var t, out var r, out var o);
        return (c, a, g, t, r, o);
    }
}

// Simulates a small webapi request: parse, materialize a "model", render a "response".
// Total per-request: ~5 KB and ~13 small objects, all discarded before return.
internal static class FakeWebApi
{
    // Each request reuses no state; everything is freshly allocated.
    // Per-request budget: ~30 KB across ~50 small objects (typical of a real
    // JSON webapi that parses a request, materializes a model, and serializes
    // a response).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int HandleRequest(int requestId)
    {
        // Read an 8 KB body
        byte[] body = new byte[8192];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(requestId + i);

        // Header dictionary (small object array)
        string[] headerKeys = new[] { "Host", "User-Agent", "Accept", "X-Request-Id", "Content-Type", "Authorization", "Cookie", "Referer" };
        string[] headerVals = new string[headerKeys.Length];
        for (int i = 0; i < headerKeys.Length; i++)
        {
            headerVals[i] = "value-" + requestId + "-" + i;
        }

        // Materialize 32 "model" objects (more realistic for a list/feed endpoint)
        var items = new RequestItem[32];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new RequestItem
            {
                Id = requestId * 100 + i,
                Name = "item-" + i,
                Description = "description-of-item-" + i + "-in-request-" + requestId,
                Tags = new[] { "tag-a", "tag-b", "tag-c", "tag-d" }
            };
        }

        // Build an 8 KB response string
        var sb = new StringBuilder(8192);
        sb.Append("{\"id\":").Append(requestId).Append(",\"items\":[");
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(items[i].Id)
              .Append(",\"name\":\"").Append(items[i].Name)
              .Append("\",\"description\":\"").Append(items[i].Description)
              .Append("\"}");
        }
        sb.Append("],\"hash\":");

        // Compute a checksum so the JIT can't elide the work
        int sum = 0;
        for (int i = 0; i < body.Length; i++) sum += body[i];
        for (int i = 0; i < items.Length; i++) sum += items[i].Id;
        sb.Append(sum).Append('}');

        string response = sb.ToString();
        return response.Length + sum;
    }

    private sealed class RequestItem
    {
        public int Id;
        public string Name = "";
        public string Description = "";
        public string[] Tags = Array.Empty<string>();
    }
}

internal static class Program
{
    private const int kWarmupRequests = 1_000;
    private const int kMeasuredRequests = 100_000;

    private static int Main(string[] args)
    {
        bool useArena =
            Environment.GetEnvironmentVariable("SIMPLEGC_USE_ARENA") == "1";

        string mode = useArena
            ? (SimpleGC.IsLoaded ? "simplegc + per-request arena" : "ARENA REQUESTED but simplegc not loaded -> falling back")
            : (SimpleGC.IsLoaded ? "simplegc, NO arena (Phase 2/3 mode)" : "default coreclr GC");

        Console.WriteLine("=== SimpleGC webapi-bench ===");
        Console.WriteLine($"mode            : {mode}");
        Console.WriteLine($"simplegc loaded : {SimpleGC.IsLoaded}");
        Console.WriteLine($"warmup req      : {kWarmupRequests:N0}");
        Console.WriteLine($"measured req    : {kMeasuredRequests:N0}");
        Console.WriteLine();

        // Warm-up - get JIT, tier1, alloc paths warm, AND pre-init runtime/library
        // lazy state. We deliberately DON'T use the arena during warmup: every
        // ArrayPool bucket, type initializer, JIT cache, exception resource,
        // ResourceManager etc. needs to land in perm so that when the measured
        // loop starts using the arena, no cross-request runtime cache lives in
        // request memory.
        Console.WriteLine("warmup starting (no arena)...");

        // Pre-touch exception infrastructure: AV resources, type-load resources,
        // index/format error resources, ResourceManager(typeof(SR)) for a few
        // assemblies, etc. Inside HandleRequest we never throw, but the runtime
        // may throw for many other reasons (e.g., null-checks in interop, lazy
        // type init). Forcing exception construction once primes those caches
        // in perm.
        try { throw new InvalidOperationException("warmup"); } catch { }
        try { throw new ArgumentNullException("warmup"); } catch { }
        try { throw new ArgumentOutOfRangeException("warmup"); } catch { }
        try { throw new IndexOutOfRangeException("warmup"); } catch { }
        try { throw new FormatException("warmup"); } catch { }
        try { throw new OverflowException("warmup"); } catch { }
        try { throw new NullReferenceException("warmup"); } catch { }
        try { throw new AccessViolationException("warmup"); } catch { }
        try { object? o = null; o!.ToString(); } catch { }
        try { int.Parse("not-a-number"); } catch { }
        try { int[] xs = new int[4]; _ = xs[10]; } catch { }
        // Pre-touch number/string formatting and culture caches.
        for (int i = 0; i < 16; i++)
        {
            _ = i.ToString();
            _ = i.ToString("D6");
            _ = i.ToString("X");
            _ = string.Format("[{0}]", i);
            _ = $"v-{i}";
        }
        for (int i = 0; i < kWarmupRequests; i++)
        {
            int r = FakeWebApi.HandleRequest(i);
            if (r < 0) Console.WriteLine("?");
            if (i == 0) Console.WriteLine("  warmup iter 0 ok");
            if (i == 9) Console.WriteLine("  warmup iter 9 ok");
            if (i == kWarmupRequests - 1) Console.WriteLine($"  warmup iter {i} ok");
        }
        Console.WriteLine("warmup done.");

        // Reset GC counters baseline
        // Note: precise=true triggers a SuspendEE/RestartEE round trip that
        // our standalone GC doesn't fully implement. Use precise=false (a
        // cheap thread-local-counter sum) to stay portable.
        long allocBaseline = GC.GetTotalAllocatedBytes(precise: false);
        int gen0Baseline = GC.CollectionCount(0);
        int gen1Baseline = GC.CollectionCount(1);
        int gen2Baseline = GC.CollectionCount(2);

        long[] elapsedTicks = new long[kMeasuredRequests];
        var sw = new Stopwatch();
        var totalSw = Stopwatch.StartNew();

        for (int i = 0; i < kMeasuredRequests; i++)
        {
            if (useArena) SimpleGC.RequestBegin();
            sw.Restart();
            int r = FakeWebApi.HandleRequest(i);
            sw.Stop();
            elapsedTicks[i] = sw.ElapsedTicks;
            if (useArena) SimpleGC.RequestEnd();
            if (r < 0) Console.WriteLine("?");
        }

        totalSw.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes(precise: false);
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        // Refresh process info to get fresh working-set
        Process p = Process.GetCurrentProcess();
        p.Refresh();

        // Percentiles
        Array.Sort(elapsedTicks);
        long p50 = elapsedTicks[(int)(kMeasuredRequests * 0.50)];
        long p90 = elapsedTicks[(int)(kMeasuredRequests * 0.90)];
        long p99 = elapsedTicks[(int)(kMeasuredRequests * 0.99)];
        long p999 = elapsedTicks[(int)(kMeasuredRequests * 0.999)];
        long pmax = elapsedTicks[kMeasuredRequests - 1];

        double tickFreq = 1_000_000_000.0 / Stopwatch.Frequency; // ns per tick
        string Fmt(long t) => $"{t * tickFreq / 1000.0,8:F2} us";

        Console.WriteLine("=== Per-request latency (measured window) ===");
        Console.WriteLine($"  p50       : {Fmt(p50)}");
        Console.WriteLine($"  p90       : {Fmt(p90)}");
        Console.WriteLine($"  p99       : {Fmt(p99)}");
        Console.WriteLine($"  p99.9     : {Fmt(p999)}");
        Console.WriteLine($"  max       : {Fmt(pmax)}");
        Console.WriteLine();
        Console.WriteLine("=== Throughput / GC ===");
        Console.WriteLine($"  total     : {totalSw.Elapsed.TotalMilliseconds,10:F1} ms");
        Console.WriteLine($"  rps       : {kMeasuredRequests / totalSw.Elapsed.TotalSeconds,10:F0}");
        Console.WriteLine($"  alloc     : {(allocAfter - allocBaseline) / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine($"  gen0      : {gen0After - gen0Baseline}");
        Console.WriteLine($"  gen1      : {gen1After - gen1Baseline}");
        Console.WriteLine($"  gen2      : {gen2After - gen2Baseline}");
        Console.WriteLine();
        Console.WriteLine("=== Process ===");
        Console.WriteLine($"  workingSet (final): {p.WorkingSet64 / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine($"  workingSet (peak) : {p.PeakWorkingSet64 / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine();

        if (SimpleGC.IsLoaded)
        {
            var t = SimpleGC.Telemetry();
            Console.WriteLine("=== simplegc telemetry ===");
            Console.WriteLine($"  consults  : {t.consults}");
            Console.WriteLine($"  approved  : {t.approved}");
            Console.WriteLine($"  gcCount   : {t.gcs}     (= request_end count when arena mode)");
            Console.WriteLine($"  totalAlloc: {t.totalAlloc / 1024.0 / 1024.0,10:F1} MB");
            Console.WriteLine($"  requested : {t.requested / 1024.0 / 1024.0,10:F1} MB");
            Console.WriteLine($"  objects   : {t.objects}");

            var a = SimpleGC.ArenaStats();
            Console.WriteLine($"  perm used : {a.permUsed / 1024.0 / 1024.0,10:F1} MB / committed {a.permCommitted / 1024.0 / 1024.0,8:F1} MB");
            Console.WriteLine($"  req  used : {a.reqUsed / 1024.0 / 1024.0,10:F1} MB / committed {a.reqCommitted / 1024.0 / 1024.0,8:F1} MB");
        }

        Console.WriteLine();
        Console.WriteLine("done.");
        return 0;
    }
}
