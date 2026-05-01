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

// Catalog domain - representative of an e-commerce product catalog API.
// Catalog data is built ONCE at startup and lives in perm. Each request
// allocates: query strings (parsed from URL), an intermediate filter list,
// a sorted result array, projected ProductSummary[], JSON serialization
// buffers. Estimate ~30-60 KB per request depending on match count.

internal sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double Price { get; set; }
    public int Stock { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
}

internal sealed class ProductSummary
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public int MatchScore { get; set; }
}

internal sealed class SearchResponse
{
    public string Query { get; set; } = "";
    public double MinPrice { get; set; }
    public int TotalMatches { get; set; }
    public ProductSummary[] Results { get; set; } = Array.Empty<ProductSummary>();
}

[JsonSerializable(typeof(ItemsResponse))]
[JsonSerializable(typeof(Item[]))]
[JsonSerializable(typeof(Item))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(ProductSummary[]))]
[JsonSerializable(typeof(ProductSummary))]
internal partial class AppJsonContext : JsonSerializerContext { }

internal static class Catalog
{
    // 10k products generated deterministically. Long-lived: built once at
    // Build() time, stays referenced by a static field for the life of the
    // process. In simplegc this lives in perm.
    public static Product[] Products { get; private set; } = Array.Empty<Product>();

    private static readonly string[] s_adjectives =
        { "premium", "deluxe", "classic", "essential", "ultra", "compact", "rugged", "sleek", "smart", "vintage" };
    private static readonly string[] s_nouns =
        { "widget", "gadget", "sprocket", "gizmo", "device", "tool", "kit", "stand", "mount", "case",
          "cable", "adapter", "charger", "speaker", "lamp", "monitor", "keyboard", "mouse", "headset", "camera" };
    private static readonly string[] s_tagPool =
        { "new", "sale", "popular", "outdoor", "indoor", "wireless", "rechargeable", "ergonomic", "portable", "ecofriendly" };

    public static void Initialize(int count)
    {
        var rng = new Random(42); // deterministic
        var products = new Product[count];
        for (int i = 0; i < count; i++)
        {
            string adj = s_adjectives[rng.Next(s_adjectives.Length)];
            string noun = s_nouns[rng.Next(s_nouns.Length)];
            int tagCount = 1 + rng.Next(3);
            var tags = new string[tagCount];
            for (int t = 0; t < tagCount; t++) tags[t] = s_tagPool[rng.Next(s_tagPool.Length)];
            products[i] = new Product
            {
                Id = i,
                Sku = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"SKU-{i:D6}"),
                Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{adj} {noun} {i}"),
                Description = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"Our {adj} {noun} model #{i} is engineered for performance and reliability. Includes a 2-year warranty."),
                Price = Math.Round(5.0 + rng.NextDouble() * 495.0, 2),
                Stock = rng.Next(1000),
                Tags = tags
            };
        }
        Products = products;
    }
}

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

    // Catalog search handler. Filter products by name/description containing q
    // (case-insensitive ASCII), and price >= minPrice. Sort matching products
    // by (score desc, price desc). Project the top 50 to ProductSummary. This
    // shape is typical of an e-commerce listing API.
    public static SearchResponse Search(string? q, double minPrice)
    {
        q ??= "";
        var products = Catalog.Products;

        // Lowercase the query once. Ascii-only fast path.
        string ql = q.Length == 0 ? "" : q.ToLowerInvariant();

        // First pass: count matches and remember which indices matched. A
        // single byte[catalog] keeps the marker compact (10 KB) regardless of
        // catalog size, vs a Match[catalog] at 160 KB. We then size the
        // Match[] to the exact match count for the sort.
        var marks = new byte[products.Length];
        int matchCount = 0;
        for (int i = 0; i < products.Length; i++)
        {
            var p = products[i];
            if (p.Price < minPrice) continue;
            int score;
            if (ql.Length == 0)
            {
                score = 1;
            }
            else
            {
                score = 0;
                if (ContainsIgnoreCase(p.Name, ql)) score += 3;
                if (ContainsIgnoreCase(p.Description, ql)) score += 1;
                if (score == 0) continue;
            }
            marks[i] = (byte)score;
            matchCount++;
        }

        // Second pass: pack matches into a sorted-size Match[].
        var matches = new Match[matchCount];
        int mi = 0;
        for (int i = 0; i < products.Length && mi < matchCount; i++)
        {
            byte s = marks[i];
            if (s == 0) continue;
            matches[mi++] = new Match { Id = i, Score = s, Price = products[i].Price };
        }

        // Sort by (score desc, price desc).
        matches.AsSpan().Sort(static (a, b) =>
        {
            int s = b.Score - a.Score;
            return s != 0 ? s : b.Price.CompareTo(a.Price);
        });

        int take = Math.Min(50, matchCount);
        var results = new ProductSummary[take];
        for (int i = 0; i < take; i++)
        {
            ref var m = ref matches[i];
            var p = products[m.Id];
            results[i] = new ProductSummary
            {
                Id = p.Id,
                Sku = p.Sku,
                Name = p.Name,
                Price = p.Price,
                MatchScore = m.Score
            };
        }

        return new SearchResponse
        {
            Query = q,
            MinPrice = minPrice,
            TotalMatches = matchCount,
            Results = results
        };
    }

    private struct Match
    {
        public int Id;
        public int Score;
        public double Price;
    }

    private static bool ContainsIgnoreCase(string haystack, string needleLower)
    {
        // Case-insensitive ASCII contains - avoid culture-aware overloads
        // which lazily init NumberFormatInfo / CompareInfo per thread (the
        // very thing we hit at concurrency > 1).
        if (needleLower.Length == 0) return true;
        if (haystack.Length < needleLower.Length) return false;
        int last = haystack.Length - needleLower.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            for (; j < needleLower.Length; j++)
            {
                char a = haystack[i + j];
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (a != needleLower[j]) break;
            }
            if (j == needleLower.Length) return true;
        }
        return false;
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
        // Initialize the catalog before Kestrel starts so the ~10k products
        // are referenced from the static field BEFORE the first request and
        // therefore land in perm under simplegc.
        Catalog.Initialize(10_000);

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
                        var path = ctx.Request.Path.Value ?? "";
                        ctx.Response.ContentType = "application/json";

                        if (path.StartsWith("/search", StringComparison.Ordinal))
                        {
                            // Parse ?q= and ?minPrice= directly from the
                            // QueryString to avoid IQueryCollection allocations
                            // that lazy-init parsers per thread.
                            string? q = ctx.Request.Query["q"].ToString();
                            double minPrice = 0;
                            string mp = ctx.Request.Query["minPrice"].ToString();
                            if (!string.IsNullOrEmpty(mp))
                                double.TryParse(mp, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out minPrice);

                            SearchResponse response = Handler.Search(q, minPrice);
                            await ctx.Response.WriteAsJsonAsync(response,
                                AppJsonContext.Default.SearchResponse);
                            return;
                        }

                        // Default /items/N path - simple smoke test handler
                        int requestId = 0;
                        if (!string.IsNullOrEmpty(path))
                        {
                            int slash = path.LastIndexOf('/');
                            if (slash >= 0 && slash + 1 < path.Length)
                                int.TryParse(path.AsSpan(slash + 1), out requestId);
                        }

                        ItemsResponse itemsResp = Handler.Build(requestId);
                        await ctx.Response.WriteAsJsonAsync(itemsResp,
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
    // Pseudo-random query terms for /search. Picked so a few terms match
    // many products (long tail) and others match few. Terms come from the
    // adjective/noun pools used to generate the catalog.
    private static readonly string[] s_queryTerms =
    {
        "widget",        // common noun -> ~10% of catalog
        "premium",       // common adj  -> ~10% of catalog
        "smart camera",  // 2-word, narrower
        "ultra mount",   // 2-word, narrower
        "vintage",       // adj only
        "headset",       // narrower
        "sprocket",      // narrower
        "wireless",      // doesn't match by name (only tags) -> 0 matches
        "",              // empty query -> all products price-filtered
        "rugged kit"     // narrow
    };

    public static async Task<int> RunAsync(string endpoint, int totalRequests, int concurrency)
    {
        // One HttpClient per worker; pre-create to avoid per-iter handler init
        var clients = new HttpClient[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            clients[i] = new HttpClient { BaseAddress = new Uri(Server.Url) };
        }

        long[] elapsedTicks = new long[totalRequests];
        long failures = 0;

        // Build a request URL for iteration idx. /items/{idx} or /search?q=...&minPrice=...
        string Url(int idx)
        {
            if (endpoint == "items") return "/items/" + idx;
            // /search: vary q and minPrice deterministically per idx so the
            // same idx always produces the same URL (for reproducibility).
            string q = s_queryTerms[idx % s_queryTerms.Length];
            int minPriceIdx = (idx / s_queryTerms.Length) % 10;
            int minPrice = minPriceIdx * 50;        // 0, 50, 100, ..., 450
            if (string.IsNullOrEmpty(q))
                return "/search?minPrice=" + minPrice;
            return "/search?q=" + Uri.EscapeDataString(q) + "&minPrice=" + minPrice;
        }

        // Warm-up: prime ASP.NET pipeline (route table, JSON formatter,
        // ResponseHeaders pool, JsonSerializerContext CWT entries, etc.).
        // Discard timings. The arena bracket is DISARMED during warmup so
        // all lazy-init storage commits to perm. Keep warmup small because
        // every byte allocated here grows perm permanently.
        const int warmup = 500;
        Console.WriteLine($"driver: warmup {warmup} reqs (arena disarmed)...");
        for (int i = 0; i < warmup; i++)
        {
            using var resp = await clients[0].GetAsync(Url(i));
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
                        using var resp = await client.GetAsync(Url(idx));
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
        string endpoint = "search"; // "items" or "search"
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--n") int.TryParse(args[i + 1], out totalRequests);
            if (args[i] == "--c") int.TryParse(args[i + 1], out concurrency);
            if (args[i] == "--ep") endpoint = args[i + 1];
        }

        Console.WriteLine("=== SimpleGC kestrel-bench ===");
        Console.WriteLine($"  simplegc loaded : {SimpleGC.IsLoaded}");
        Console.WriteLine($"  arena configured: {SimpleGC.ArenaConfigured}");
        Console.WriteLine($"  endpoint        : /{endpoint}");
        Console.WriteLine($"  total requests  : {totalRequests:N0}");
        Console.WriteLine($"  concurrency     : {concurrency}");
        Console.WriteLine($"  url             : {Server.Url}");

        using var host = Server.Build();
        await host.StartAsync();
        Console.WriteLine($"server started. catalog has {Catalog.Products.Length:N0} products.");

        try
        {
            return await Driver.RunAsync(endpoint, totalRequests, concurrency);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
