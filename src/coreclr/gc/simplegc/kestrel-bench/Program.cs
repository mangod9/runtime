// SimpleGC Kestrel bench - real Kestrel HTTP server with per-request arena
//
// This is a self-contained test:
//   * Process starts a Kestrel server on http://127.0.0.1:5005
//   * A middleware brackets each request with simplegc_request_begin / end
//   * After the server is ready, an in-process load generator opens a
//     small number of HttpClients and fires N HTTP GET requests at the
//     server, measuring per-request latency, p50/p99/p99.9/max, and
//     overall throughput
//
// Caveats vs webapi-bench (synchronous loop):
//   * Kestrel processes requests across threadpool threads. A request
//     may begin on one thread, suspend on an async I/O, and resume on
//     a different thread. Our simplegc t_activeArena is thread-local,
//     so cross-thread continuations break the bracket model.
//   * We mitigate by using AsyncLocal<> on the managed side to flow a
//     "request-active" flag, and by re-entering the native bracket on
//     EVERY HttpContext-bound delegate (best effort).
//   * Pinned I/O buffers (SocketAsyncEventArgs) outlive a single request
//     bracket - they live in perm anyway because they are pinned (POH)
//     and we route POH allocations to perm.
//   * ASP.NET Core has a vast lazy-init cache surface. We run a 1k
//     warmup loop with no arena bracket so most lazy state lands in perm.

using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleGCKestrelBench;

internal static class SimpleGC
{
    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong simplegc_request_begin();

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong simplegc_request_end();

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void simplegc_get_arena_stats(
        out ulong permUsed, out ulong permCommitted,
        out ulong reqUsed,  out ulong reqCommitted);

    [DllImport("simplegc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void simplegc_get_telemetry(
        out ulong consults, out ulong approved, out ulong gcs,
        out ulong totalAlloc, out ulong requested, out ulong objects);

    public static bool IsLoaded { get; }
    public static bool ArenaConfigured { get; }

    // The arena bracket is "armed" only AFTER warmup completes. While disarmed
    // every allocation lands in perm. This way ASP.NET's massive lazy-init
    // (routes, JsonSerializerOptions, CWTs, header parsers, Pipe pools, ...)
    // commits to perm during warmup. After arming, only request-local
    // allocations land in the request arena and rewind cleanly.
    private static volatile bool s_armed;
    public static bool Armed => s_armed;
    public static void Arm()    => s_armed = true;
    public static void Disarm() => s_armed = false;

    static SimpleGC()
    {
        string gcName = Environment.GetEnvironmentVariable("DOTNET_GCName") ?? "";
        IsLoaded = gcName.Equals("simplegc.dll", StringComparison.OrdinalIgnoreCase);
        ArenaConfigured = IsLoaded && Environment.GetEnvironmentVariable("SIMPLEGC_USE_ARENA") == "1";
    }

    public static void RequestBegin() { if (ArenaConfigured && s_armed) simplegc_request_begin(); }
    public static ulong RequestEnd()  => (ArenaConfigured && s_armed) ? simplegc_request_end() : 0UL;

    public static (ulong pu, ulong pc, ulong ru, ulong rc) ArenaStats()
    {
        if (!IsLoaded) return (0,0,0,0);
        simplegc_get_arena_stats(out var pu, out var pc, out var ru, out var rc);
        return (pu, pc, ru, rc);
    }

    public static (ulong consults, ulong approved, ulong gcs,
                   ulong totalAlloc, ulong requested, ulong objects) Telemetry()
    {
        if (!IsLoaded) return (0,0,0,0,0,0);
        simplegc_get_telemetry(out var c, out var a, out var g, out var t, out var r, out var o);
        return (c, a, g, t, r, o);
    }
}

// ---------------------------------------------------------------------------
// Domain model + handler
// ---------------------------------------------------------------------------

internal sealed class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Tags { get; set; } = Array.Empty<string>();
}

internal sealed class ItemsResponse
{
    public int RequestId { get; set; }
    public Item[] Items { get; set; } = Array.Empty<Item>();
    public int Hash { get; set; }
}

[JsonSerializable(typeof(ItemsResponse))]
[JsonSerializable(typeof(Item[]))]
[JsonSerializable(typeof(Item))]
internal partial class AppJsonContext : JsonSerializerContext { }

internal static class Handler
{
    // Same allocation profile as webapi-bench: ~30 KB / ~50 small objects per
    // request. The whole thing is locals dropped at end of method.
    public static ItemsResponse Build(int requestId)
    {
        byte[] body = new byte[8192];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(requestId + i);

        var items = new Item[32];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new Item
            {
                Id = requestId * 100 + i,
                Name = "item-" + i,
                Description = "description-of-item-" + i + "-in-request-" + requestId,
                Tags = new[] { "tag-a", "tag-b", "tag-c", "tag-d" }
            };
        }

        int sum = 0;
        for (int i = 0; i < body.Length; i++) sum += body[i];
        for (int i = 0; i < items.Length; i++) sum += items[i].Id;

        return new ItemsResponse { RequestId = requestId, Items = items, Hash = sum };
    }
}

// ---------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------

internal static class Server
{
    public const string Url = "http://127.0.0.1:5005";

    public static IHost Build()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())   // no log allocations on the hot path
            .ConfigureWebHostDefaults(web =>
            {
                web.UseKestrel(o =>
                {
                    o.ListenLocalhost(5005);
                    o.AddServerHeader = false;
                });
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        // Bracket this request. begin/end happen on whichever
                        // thread the middleware is invoked on. With async, the
                        // continuation can resume on a different thread - in
                        // that case the native bracket is incomplete (begin
                        // ran on thread A, end runs on thread B which has no
                        // active arena). For sync handlers we are fine.
                        SimpleGC.RequestBegin();
                        try { await next(); }
                        finally { SimpleGC.RequestEnd(); }
                    });

                    app.Run(async ctx =>
                    {
                        // Parse a request id out of the path: /items/N -> N
                        int requestId = 0;
                        var path = ctx.Request.Path.Value;
                        if (!string.IsNullOrEmpty(path))
                        {
                            int slash = path.LastIndexOf('/');
                            if (slash >= 0 && slash + 1 < path.Length)
                                int.TryParse(path.AsSpan(slash + 1), out requestId);
                        }

                        ItemsResponse response = Handler.Build(requestId);

                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsJsonAsync(response,
                            AppJsonContext.Default.ItemsResponse);
                    });
                });
            });
        return builder.Build();
    }
}

// ---------------------------------------------------------------------------
// In-process load driver
// ---------------------------------------------------------------------------

internal static class Driver
{
    public static async Task<int> RunAsync(int totalRequests, int concurrency)
    {
        // One HttpClient per worker; pre-create to avoid per-iter handler init
        var clients = new HttpClient[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            clients[i] = new HttpClient { BaseAddress = new Uri(Server.Url) };
        }

        long[] elapsedTicks = new long[totalRequests];
        long failures = 0;

        // Warm-up: a small batch of requests to prime ASP.NET pipeline (route
        // table, JSON formatter, ResponseHeaders pool, JsonSerializerContext
        // CWT entries, etc.). Discard timings. The arena bracket is DISARMED
        // during warmup so all lazy-init storage lands in perm.
        //
        // NOTE: warmup is sequential on a single client. Server-side, this
        // means Kestrel will mostly use a single threadpool thread to serve
        // these warmups. After arming, if Kestrel dispatches a request to a
        // FRESH threadpool thread (one that wasn't used during warmup), that
        // thread's first-time per-thread init (NumberFormatInfo.CurrentInfo
        // and friends - they are [ThreadStatic]) lands in the request arena
        // and is rewound at request_end -> AV on the next access. The
        // bench therefore only runs cleanly at concurrency = 1. Solving this
        // for c > 1 needs either suspend-EE-style coordinated rewind or
        // per-thread arena slices.
        Console.WriteLine($"driver: warmup {2000} reqs (arena disarmed)...");
        for (int i = 0; i < 2000; i++)
        {
            using var resp = await clients[0].GetAsync("/items/" + i);
            if (!resp.IsSuccessStatusCode) failures++;
        }
        Console.WriteLine($"driver: warmup done (failures={failures}).");
        failures = 0;

        if (SimpleGC.ArenaConfigured)
        {
            // Capture an arena-stats baseline at the moment we arm so we can
            // attribute below-the-line memory growth to actual request work.
            var s0 = SimpleGC.ArenaStats();
            Console.WriteLine($"driver: arming arena bracket. " +
                $"perm={s0.pu / 1024.0 / 1024.0:F1} MB committed={s0.pc / 1024.0 / 1024.0:F1} MB.");
            SimpleGC.Arm();
        }

        var totalSw = Stopwatch.StartNew();

        // Round-robin slots across workers.
        var tasks = new Task[concurrency];
        int next = 0;
        for (int w = 0; w < concurrency; w++)
        {
            int worker = w;
            tasks[w] = Task.Run(async () =>
            {
                var client = clients[worker];
                var sw = new Stopwatch();
                while (true)
                {
                    int idx = Interlocked.Increment(ref next) - 1;
                    if (idx >= totalRequests) return;
                    sw.Restart();
                    try
                    {
                        using var resp = await client.GetAsync("/items/" + idx);
                        sw.Stop();
                        elapsedTicks[idx] = sw.ElapsedTicks;
                        if (!resp.IsSuccessStatusCode) Interlocked.Increment(ref failures);
                    }
                    catch
                    {
                        sw.Stop();
                        elapsedTicks[idx] = sw.ElapsedTicks;
                        Interlocked.Increment(ref failures);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        totalSw.Stop();

        Array.Sort(elapsedTicks);
        long p50  = elapsedTicks[(int)(totalRequests * 0.50)];
        long p90  = elapsedTicks[(int)(totalRequests * 0.90)];
        long p99  = elapsedTicks[(int)(totalRequests * 0.99)];
        long p999 = elapsedTicks[(int)(totalRequests * 0.999)];
        long pmax = elapsedTicks[totalRequests - 1];

        double tickFreq = 1_000_000_000.0 / Stopwatch.Frequency;
        string Fmt(long t) => $"{t * tickFreq / 1000.0,8:F2} us";

        Console.WriteLine();
        Console.WriteLine("=== HTTP per-request latency ===");
        Console.WriteLine($"  p50      : {Fmt(p50)}");
        Console.WriteLine($"  p90      : {Fmt(p90)}");
        Console.WriteLine($"  p99      : {Fmt(p99)}");
        Console.WriteLine($"  p99.9    : {Fmt(p999)}");
        Console.WriteLine($"  max      : {Fmt(pmax)}");
        Console.WriteLine();
        Console.WriteLine("=== Throughput ===");
        Console.WriteLine($"  requests : {totalRequests:N0}");
        Console.WriteLine($"  failures : {failures:N0}");
        Console.WriteLine($"  total    : {totalSw.Elapsed.TotalMilliseconds,10:F1} ms");
        Console.WriteLine($"  rps      : {totalRequests / totalSw.Elapsed.TotalSeconds,10:F0}");

        Console.WriteLine();
        Console.WriteLine("=== GC / Memory ===");
        Console.WriteLine($"  alloc    : {GC.GetTotalAllocatedBytes(precise: false) / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine($"  gen0     : {GC.CollectionCount(0)}");
        Console.WriteLine($"  gen1     : {GC.CollectionCount(1)}");
        Console.WriteLine($"  gen2     : {GC.CollectionCount(2)}");
        var p = Process.GetCurrentProcess();
        p.Refresh();
        Console.WriteLine($"  workSet  : {p.WorkingSet64 / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine($"  peakWS   : {p.PeakWorkingSet64 / 1024.0 / 1024.0,10:F1} MB");

        if (SimpleGC.IsLoaded)
        {
            var t = SimpleGC.Telemetry();
            var a = SimpleGC.ArenaStats();
            Console.WriteLine();
            Console.WriteLine("=== simplegc telemetry ===");
            Console.WriteLine($"  gcCount  : {t.gcs}");
            Console.WriteLine($"  totalAlloc: {t.totalAlloc / 1024.0 / 1024.0,10:F1} MB");
            Console.WriteLine($"  requested : {t.requested / 1024.0 / 1024.0,10:F1} MB");
            Console.WriteLine($"  perm used : {a.pu / 1024.0 / 1024.0,10:F1} MB / committed {a.pc / 1024.0 / 1024.0,8:F1} MB");
            Console.WriteLine($"  req  used : {a.ru / 1024.0 / 1024.0,10:F1} MB / committed {a.rc / 1024.0 / 1024.0,8:F1} MB");
        }

        return failures == 0 ? 0 : 2;
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int totalRequests = 50_000;
        int concurrency = 8;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--n") int.TryParse(args[i + 1], out totalRequests);
            if (args[i] == "--c") int.TryParse(args[i + 1], out concurrency);
        }

        // Pin the threadpool size so no new threadpool worker can arrive AFTER
        // we arm the arena. Each new thread does first-time per-thread inits
        // (NumberFormatInfo, CurrentCulture, etc.) which would land in the
        // request arena and AV the next request that touches them. Reserve
        // enough for the server (concurrency req-handlers) + driver workers +
        // headroom for Kestrel internal I/O work.
        int pool = Math.Max(32, concurrency * 4);
        ThreadPool.SetMinThreads(pool, pool);
        ThreadPool.SetMaxThreads(pool, pool);

        Console.WriteLine("=== SimpleGC kestrel-bench ===");
        Console.WriteLine($"  simplegc loaded : {SimpleGC.IsLoaded}");
        Console.WriteLine($"  arena configured: {SimpleGC.ArenaConfigured}");
        Console.WriteLine($"  total requests  : {totalRequests:N0}");
        Console.WriteLine($"  concurrency     : {concurrency}");
        Console.WriteLine($"  url             : {Server.Url}");

        using var host = Server.Build();
        await host.StartAsync();
        Console.WriteLine("server started.");

        try
        {
            return await Driver.RunAsync(totalRequests, concurrency);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
