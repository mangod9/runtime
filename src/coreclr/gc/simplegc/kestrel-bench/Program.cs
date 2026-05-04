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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SimpleGC.Policy;
using SgcRoute = SimpleGC.Policy.Route;
using SgcRoutingEntry = SimpleGC.Policy.RoutingEntry;

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

    public static void RequestBegin()
    {
        // SAFETY: see Server.Build() — the per-request bracket is incompatible
        // with ASP.NET async middleware. Kept as a no-op here so anything
        // calling it from this binary cannot accidentally re-introduce the
        // torn-reference race. The native primitive is still exported and
        // remains safe in single-threaded synchronous contexts (policy-demo).
    }

    public static ulong RequestEnd() => 0UL;

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

// ---------------------------------------------------------------------------
// M1j: Fortunes domain - TechEmpower-Fortunes-shaped workload.
// 100 fortune rows live in perm. Each /fortunes request:
//   - picks 12 deterministic-pseudo-random rows by id
//   - appends one fixed extra row built per-request
//   - sorts by Message ASCII
//   - renders an HTML table with HtmlEncoder-escaped fields
// Per-request allocation: ~2 KB sorted list + ~3-5 KB rendered HTML +
// the encoder StringBuilder churn. Closer to TechEmpower Fortunes than
// /search, and it stresses the same Latin1 / UTF-8 string paths that
// surfaced the M1i.2 alignment bug.
// ---------------------------------------------------------------------------

internal sealed class Fortune
{
    public int Id { get; set; }
    public string Message { get; set; } = "";
}

internal static class Fortunes
{
    public static Fortune[] All { get; private set; } = Array.Empty<Fortune>();

    private static readonly string[] s_messages =
    {
        "fortune: You will visit a foreign country.",
        "fortune: A beautiful, smart, and loving person will be coming into your life.",
        "fortune: Today is a good day to learn something new.",
        "fortune: A friend is a present you give yourself.",
        "fortune: An empty stomach is not a good political adviser.",
        "fortune: <script>alert('xss')</script> would be bad without HtmlEncoder.",
        "fortune: \"Quoted strings\" & ampersands < > need escaping.",
        "fortune: Ingen kan tvinges til lykke.",
        "fortune: 一切都会好起来的。",
        "fortune: Always look on the bright side of life.",
        "fortune: A picture is worth a thousand words.",
        "fortune: Beware the ides of March.",
        "fortune: Slow and steady wins the race.",
        "fortune: The early bird catches the worm.",
        "fortune: Good things come to those who wait.",
        "fortune: Many hands make light work.",
        "fortune: A journey of a thousand miles begins with a single step.",
        "fortune: Actions speak louder than words.",
        "fortune: Better late than never.",
        "fortune: Birds of a feather flock together.",
        "fortune: Don't count your chickens before they hatch.",
        "fortune: Don't put all your eggs in one basket.",
        "fortune: Every cloud has a silver lining.",
        "fortune: Fortune favors the bold.",
        "fortune: Honesty is the best policy.",
        "fortune: If it ain't broke, don't fix it.",
        "fortune: Knowledge is power.",
        "fortune: Look before you leap.",
        "fortune: Necessity is the mother of invention.",
        "fortune: No man is an island.",
        "fortune: One man's trash is another man's treasure.",
        "fortune: Practice makes perfect.",
        "fortune: Rome wasn't built in a day.",
        "fortune: The pen is mightier than the sword.",
        "fortune: Time is money.",
        "fortune: Two heads are better than one.",
        "fortune: When in Rome, do as the Romans do.",
        "fortune: Where there's smoke, there's fire.",
        "fortune: You can't judge a book by its cover.",
        "fortune: A bad workman blames his tools.",
        "fortune: A chain is only as strong as its weakest link.",
        "fortune: A drowning man will clutch at a straw.",
        "fortune: A fool and his money are soon parted.",
        "fortune: A leopard cannot change its spots.",
        "fortune: A miss is as good as a mile.",
        "fortune: A penny saved is a penny earned.",
        "fortune: A rolling stone gathers no moss.",
        "fortune: A stitch in time saves nine.",
        "fortune: A watched pot never boils.",
        "fortune: All good things must come to an end.",
        "fortune: All that glitters is not gold.",
        "fortune: All's fair in love and war.",
        "fortune: All's well that ends well.",
        "fortune: An apple a day keeps the doctor away.",
        "fortune: Appearances can be deceiving.",
        "fortune: Beauty is in the eye of the beholder.",
        "fortune: Beggars can't be choosers.",
        "fortune: Better safe than sorry.",
        "fortune: Blood is thicker than water.",
        "fortune: Charity begins at home.",
        "fortune: Cleanliness is next to godliness.",
        "fortune: Curiosity killed the cat.",
        "fortune: Dead men tell no tales.",
        "fortune: Discretion is the better part of valor.",
        "fortune: Don't bite the hand that feeds you.",
        "fortune: Don't cross the bridge until you come to it.",
        "fortune: Don't look a gift horse in the mouth.",
        "fortune: Easy come, easy go.",
        "fortune: Familiarity breeds contempt.",
        "fortune: Give credit where credit is due.",
        "fortune: God helps those who help themselves.",
        "fortune: Half a loaf is better than none.",
        "fortune: Haste makes waste.",
        "fortune: He who hesitates is lost.",
        "fortune: He who laughs last, laughs longest.",
        "fortune: Hindsight is twenty-twenty.",
        "fortune: Hope for the best, prepare for the worst.",
        "fortune: If at first you don't succeed, try, try again.",
        "fortune: If you can't beat them, join them.",
        "fortune: If you give a mouse a cookie, he'll want a glass of milk.",
        "fortune: It takes two to tango.",
        "fortune: It's better to give than to receive.",
        "fortune: It's no use crying over spilt milk.",
        "fortune: It's the squeaky wheel that gets the grease.",
        "fortune: Keep your friends close and your enemies closer.",
        "fortune: Laughter is the best medicine.",
        "fortune: Let bygones be bygones.",
        "fortune: Let sleeping dogs lie.",
        "fortune: Lightning never strikes twice in the same place.",
        "fortune: Love conquers all.",
        "fortune: Make hay while the sun shines.",
        "fortune: Money doesn't grow on trees.",
        "fortune: No news is good news.",
        "fortune: Nothing ventured, nothing gained.",
        "fortune: Old habits die hard.",
        "fortune: Out of sight, out of mind.",
        "fortune: People who live in glass houses shouldn't throw stones.",
        "fortune: Strike while the iron is hot.",
        "fortune: The grass is always greener on the other side.",
        "fortune: The proof of the pudding is in the eating.",
        "fortune: There's no place like home.",
        "fortune: Variety is the spice of life.",
    };

    public static void Initialize()
    {
        var rows = new Fortune[s_messages.Length];
        for (int i = 0; i < s_messages.Length; i++)
        {
            rows[i] = new Fortune { Id = i + 1, Message = s_messages[i] };
        }
        All = rows;
    }
}

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

    // M1j Fortunes handler. Picks 12 deterministic-pseudo-random rows by
    // requestId, appends a per-request fixed extra row, sorts by Message
    // ASCII, and renders an HTML table with HtmlEncoder-escaped fields.
    // Returns the rendered HTML body. The Content-Type/Content-Length are
    // set by the caller.
    public static string RenderFortunes(int requestId)
    {
        var all = Fortunes.All;
        if (all.Length == 0) return "<html><body>no fortunes</body></html>";

        // Pick 12 rows deterministically from requestId. We want the same
        // requestId to produce the same selection so tests are reproducible
        // and so we don't accidentally introduce LCG-state contention across
        // worker threads.
        const int picked = 12;
        var rows = new Fortune[picked + 1];
        // simple xorshift32 from requestId
        uint state = (uint)(requestId * 2654435761u);
        if (state == 0) state = 1;
        for (int i = 0; i < picked; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int idx = (int)(state % (uint)all.Length);
            rows[i] = all[idx];
        }
        // Per-request extra row whose Message changes per request id.
        rows[picked] = new Fortune
        {
            Id = -1,
            Message = "fortune: Additional fortune added at request time #" + requestId,
        };

        // Sort by Message ASCII (Ordinal). This is the TechEmpower spec —
        // the per-request sort is significant allocation pressure for large
        // catalogs but small here.
        Array.Sort(rows, static (a, b) => string.CompareOrdinal(a.Message, b.Message));

        // Render. StringBuilder + HtmlEncoder.Default.Encode is what the
        // canonical Fortunes implementations use. Per-request the SB will
        // grow ~2-3x, so prime it generously to avoid mid-render resizes.
        var enc = HtmlEncoder.Default;
        var sb = new StringBuilder(2048);
        sb.Append("<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>");
        for (int i = 0; i < rows.Length; i++)
        {
            sb.Append("<tr><td>");
            sb.Append(rows[i].Id);
            sb.Append("</td><td>");
            sb.Append(enc.Encode(rows[i].Message));
            sb.Append("</td></tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
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
        Fortunes.Initialize();

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
                    // NOTE: a per-request RequestBegin/RequestEnd bracket was
                    // tried here but is fundamentally incompatible with ASP.NET
                    // async middleware. Two compounding correctness bugs:
                    //   1. simplegc_request_begin sets a per-thread "active
                    //      arena" flag. If `await next()` resumes on a
                    //      different thread, RequestEnd runs there as a no-op
                    //      (its t_activeArena is null), and the originating
                    //      thread's flag stays armed forever -- it leaks all
                    //      future allocations into the request arena.
                    //   2. Even when end DOES fire on the begin thread, it
                    //      rewinds the GLOBAL g_request.bump pointer while
                    //      other in-flight requests still have live objects
                    //      above the checkpoint. New allocations overwrite
                    //      them, producing torn references that AV in the
                    //      Pipelines path (e.g. ChkCastClassSpecial on a
                    //      ReadOnlySequence<byte>._startObject pointing into
                    //      reused memory).
                    // For multi-threaded async servers, the request-arena
                    // primitive needs either per-request arenas or quiescent-
                    // point rewind, neither of which exists today. M1j drives
                    // the win via the policy + NoRefsPerm sub-arena instead.

                    app.Run(async ctx =>
                    {
                        var path = ctx.Request.Path.Value ?? "";

                        if (path.StartsWith("/fortunes", StringComparison.Ordinal))
                        {
                            // Per-request idx via Date-stamp header is too
                            // expensive; instead use the URL tail if present
                            // (/fortunes/123 -> 123), else 0. For pure
                            // /fortunes the workload becomes deterministic
                            // per warmup but still allocates per-request.
                            int reqId = 0;
                            int slash = path.IndexOf('/', 1);
                            if (slash >= 0 && slash + 1 < path.Length)
                                int.TryParse(path.AsSpan(slash + 1), out reqId);

                            string html = Handler.RenderFortunes(reqId);
                            ctx.Response.ContentType = "text/html; charset=utf-8";
                            await ctx.Response.WriteAsync(html, Encoding.UTF8);
                            return;
                        }

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
        // GC trace listener: subscribes to runtime ETW GC events to capture
        // per-pause (gen, duration). Reset after warmup so init/JIT/lazy-load
        // collects don't pollute the steady-state stats.
        var gcTrace = new GcTraceListener();

        // One HttpClient per worker; pre-create to avoid per-iter handler init
        var clients = new HttpClient[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            clients[i] = new HttpClient { BaseAddress = new Uri(Server.Url) };
        }

        long[] elapsedTicks = new long[totalRequests];
        long failures = 0;

        // Build a request URL for iteration idx. /items/{idx} or /search?q=...&minPrice=... or /fortunes/{idx}
        string Url(int idx)
        {
            if (endpoint == "items") return "/items/" + idx;
            if (endpoint == "fortunes") return "/fortunes/" + idx;
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

        // Reset the GC pause record so warmup pauses don't bias the steady-
        // state breakdown.
        gcTrace.Reset();

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
        // Total time the runtime was paused for GC (since process start). For
        // standalone GCs this returns Zero because the runtime doesn't track
        // pauses there — simplegc reports its own pause time via LOG1
        // "phase_us total=...". Default GC reports a real number here.
        Console.WriteLine($"  pauseDur : {GC.GetTotalPauseDuration().TotalMilliseconds,10:F1} ms");
        var p = Process.GetCurrentProcess();
        p.Refresh();
        Console.WriteLine($"  workSet  : {p.WorkingSet64 / 1024.0 / 1024.0,10:F1} MB");
        Console.WriteLine($"  peakWS   : {p.PeakWorkingSet64 / 1024.0 / 1024.0,10:F1} MB");

        // GC pause breakdown by generation. For default WKS/Server this is
        // gold (gen0 vs gen1 vs gen2 distribution). For simplegc all pauses
        // bucket as gen=unk because simplegc doesn't fire GCStart_V2.
        Console.WriteLine();
        var pauseRecords = gcTrace.Snapshot();
        GcTraceListener.PrintSummary(pauseRecords, totalSw.Elapsed.TotalMilliseconds);

        // Optional CSV dump for offline analysis.
        string? csvPath = Environment.GetEnvironmentVariable("KESTREL_BENCH_GC_TRACE_CSV");
        if (!string.IsNullOrEmpty(csvPath))
        {
            try
            {
                GcTraceListener.WriteCsv(pauseRecords, csvPath);
                Console.WriteLine($"  gc trace csv written to {csvPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (failed to write gc trace csv: {ex.Message})");
            }
        }

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

// ---------------------------------------------------------------------------
// M1j: Memory cap via Windows Job Object. Setting JOB_OBJECT_LIMIT_PROCESS_MEMORY
// makes the kernel terminate the process if its committed memory exceeds the
// cap — equivalent to running inside a cgroup memory.max on Linux. We assign
// the current process to a fresh job before any heavy allocation happens so
// that Kestrel + warmup are also counted.
// ---------------------------------------------------------------------------

internal static class MemCap
{
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    public static bool TryApply(int memMb, out string error)
    {
        error = "";
        if (!OperatingSystem.IsWindows())
        {
            error = "Job Objects only supported on Windows.";
            return false;
        }
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            error = $"CreateJobObjectW failed (LastError={Marshal.GetLastWin32Error()})";
            return false;
        }

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
        info.ProcessMemoryLimit = (UIntPtr)((ulong)memMb * 1024UL * 1024UL);

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buf, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buf, (uint)size))
            {
                error = $"SetInformationJobObject failed (LastError={Marshal.GetLastWin32Error()})";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            error = $"AssignProcessToJobObject failed (LastError={Marshal.GetLastWin32Error()})";
            return false;
        }

        // Intentionally leak the job handle for the lifetime of the process.
        Console.WriteLine($"MemCap: applied {memMb} MB process memory limit (Job Object).");
        return true;
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int totalRequests = 50_000;
        int concurrency = 8;
        string endpoint = "search"; // "items", "search", or "fortunes"
        int memMb = 0; // 0 = no Job Object cap
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--n") int.TryParse(args[i + 1], out totalRequests);
            if (args[i] == "--c") int.TryParse(args[i + 1], out concurrency);
            if (args[i] == "--ep") endpoint = args[i + 1];
            if (args[i] == "--mem-mb") int.TryParse(args[i + 1], out memMb);
        }

        // Apply Job Object memory cap BEFORE starting Kestrel, so Kestrel
        // and warmup allocations all count against the cap. If the cap is
        // breached the OS terminates the process — exactly what production
        // containers do at the cgroup memory limit.
        if (memMb > 0)
        {
            if (!MemCap.TryApply(memMb, out string err))
            {
                Console.Error.WriteLine($"WARNING: --mem-mb={memMb} could not be applied: {err}");
            }
        }

        Console.WriteLine("=== SimpleGC kestrel-bench ===");
        Console.WriteLine($"  simplegc loaded : {SimpleGC.IsLoaded}");
        Console.WriteLine($"  arena configured: {SimpleGC.ArenaConfigured}");
        Console.WriteLine($"  endpoint        : /{endpoint}");
        Console.WriteLine($"  total requests  : {totalRequests:N0}");
        Console.WriteLine($"  concurrency     : {concurrency}");
        Console.WriteLine($"  mem-mb cap      : {(memMb > 0 ? memMb.ToString() + " MB" : "(unlimited)")}");
        Console.WriteLine($"  url             : {Server.Url}");

        // M1n.2 diagnostic mode: enable per-MT tracking but DO NOT register
        // the post-collect routing-policy callback. The callback re-enters
        // managed code via a [UnmanagedCallersOnly] thunk on the same
        // thread that triggered the collect, which (empirically) corrupts
        // the runtime's reflection / JIT caches mid-traffic and produces
        // "Invalid Program: attempted to call a UnmanagedCallersOnly
        // method from managed code" on subsequent JIT'd code paths
        // (HttpClient ConcurrentStack.Push, DI factory, etc.). Until that
        // re-entrancy is solved, we only OBSERVE per-MT survival data and
        // dump a snapshot at end-of-run, which lets us answer the
        // diagnostic question "what would BasicPolicy promote?" without
        // actually firing the policy callback.
        //
        // Enable with env SIMPLEGC_USE_POLICY=1.
        bool observePolicy = false;
        if (SimpleGC.IsLoaded)
        {
            string? policyEnv = Environment.GetEnvironmentVariable("SIMPLEGC_USE_POLICY");
            if (!string.IsNullOrEmpty(policyEnv) && policyEnv != "0")
            {
                observePolicy = true;
                SimpleGCInterop.EnableMtTracking(1);
                Console.WriteLine($"  policy host     : OBSERVE (per-MT tracking ON, callback OFF; will dump snapshot at end)");
            }
            else
            {
                Console.WriteLine("  policy host     : OFF (set SIMPLEGC_USE_POLICY=1 to enable observe mode)");
            }
        }

        using var host = Server.Build();
        await host.StartAsync();
        Console.WriteLine($"server started. catalog has {Catalog.Products.Length:N0} products.");

        try
        {
            int rv = await Driver.RunAsync(endpoint, totalRequests, concurrency);

            if (observePolicy)
            {
                DumpPolicySnapshot();
            }

            return rv;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static unsafe void DumpPolicySnapshot()
    {
        const int Capacity = 4096;
        var buffer = new SgcRoutingEntry[Capacity];
        uint emitted;
        fixed (SgcRoutingEntry* p = buffer)
        {
            emitted = SimpleGCInterop.GetRoutingSnapshot(p, Capacity);
        }
        int valid = (int)Math.Min(emitted, (uint)Capacity);
        Console.WriteLine();
        Console.WriteLine("=== per-MT survival snapshot (top 30 by survived_bytes) ===");
        Console.WriteLine($"  total tracked MTs    : {emitted}{(emitted > Capacity ? " (truncated)" : "")}");
        if (valid == 0)
        {
            Console.WriteLine("  (no rows — no mark-sweep collection has observed any survivors)");
            return;
        }

        // Sort by survived_bytes descending.
        Array.Sort(buffer, 0, valid, Comparer<SgcRoutingEntry>.Create(
            (a, b) => b.SurvivedBytes.CompareTo(a.SurvivedBytes)));

        // BasicPolicy.AgeThreshold = 2, MinSurvivedBytes = 256
        const uint BasicAgeThreshold = 2;
        const ulong BasicMinSurvived = 256;

        Console.WriteLine(
            "  rank  age  alloc_count   alloc_KB  survived_count survived_KB  size  type");
        int wouldPromote = 0;
        for (int i = 0; i < Math.Min(valid, 30); i++)
        {
            var e = buffer[i];
            string typeName = TryGetMtName(e.MtToken);
            bool meetsBasic = e.AgeCollections >= BasicAgeThreshold && e.SurvivedBytes >= BasicMinSurvived;
            if (meetsBasic) wouldPromote++;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,4}  {1,3}  {2,11}  {3,9:F1}  {4,14}  {5,10:F1}  {6,4}  {7}{8}",
                i + 1,
                e.AgeCollections,
                e.AllocCount,
                e.AllocBytes / 1024.0,
                e.SurvivedCount,
                e.SurvivedBytes / 1024.0,
                e.MinSize == e.MaxSize ? e.MinSize.ToString() : $"{e.MinSize}-{e.MaxSize}",
                typeName,
                meetsBasic ? "  <- BasicPolicy would promote" : ""));
        }

        // Also count BasicPolicy-promote candidates across the whole table.
        int totalCandidates = 0;
        ulong totalCandidateBytes = 0;
        for (int i = 0; i < valid; i++)
        {
            var e = buffer[i];
            if (e.AgeCollections >= BasicAgeThreshold && e.SurvivedBytes >= BasicMinSurvived)
            {
                totalCandidates++;
                totalCandidateBytes += e.SurvivedBytes;
            }
        }
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  BasicPolicy candidates (age>=2 && survivedBytes>=256): {0} types, {1:F1} KB total",
            totalCandidates, totalCandidateBytes / 1024.0));
    }

    private static string TryGetMtName(ulong mtToken)
    {
        try
        {
            var handle = RuntimeTypeHandle.FromIntPtr((IntPtr)(long)mtToken);
            var type = Type.GetTypeFromHandle(handle);
            return type?.FullName ?? "(unknown)";
        }
        catch
        {
            return "(invalid handle)";
        }
    }
}
