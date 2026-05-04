// ---------------------------------------------------------------------------
// GcTraceListener — in-process EventListener that subscribes to the
// "Microsoft-Windows-DotNETRuntime" provider's GC keyword (0x1) and records
// every STW pause along with the generation that caused it. Used to decompose
// the per-collect cost of the default GC (WKS / Server) and compare against
// simplegc-policy.
//
// Why this exists:
//   The summary printed by Program.cs only shows total pauseDur and per-gen
//   collection counts. To understand WHY default-WKS is fast we need the
//   pause-time distribution split by generation. WKS' magic is "many tiny
//   gen0 collects" — without the breakdown we can't see that.
//
// What it captures:
//   For each GC, pairs the GCStart_V2 event (which carries Depth=generation)
//   with the surrounding GCSuspendEEBegin_V1 / GCRestartEEEnd_V1 events
//   (which mark the actual STW boundary). PauseMs = restart_ts - suspend_ts.
//   Records (gen, reason, pause_ms, wall_ticks) per pause.
//
// Behavior with simplegc:
//   simplegc fires SuspendEE/RestartEE through the EE bridge so suspend pause
//   events still arrive. simplegc does NOT fire GCStart_V2 (it's a standalone
//   GC, the runtime hooks the FireEtwGCStart path differently). Records from
//   simplegc therefore have generation = -1 (UnknownGen). All pause time still
//   gets attributed to a single bucket so we still get a fair count + total.
//
// Cost:
//   EventListener is ~free for low-volume events. We process at most a few
//   hundred GC events per benchmark run.
// ---------------------------------------------------------------------------

using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace SimpleGCKestrelBench;

internal sealed class GcTraceListener : EventListener
{
    private const string ProviderName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GCKeyword = (EventKeywords)0x1;

    public readonly record struct PauseRecord(
        int Generation,    // -1 if unknown (simplegc)
        int Reason,
        double PauseMs,
        long WallTicks);

    private readonly List<PauseRecord> _records = new();
    private readonly Lock _lock = new();
    private long _suspendStartTicks;
    private int _pendingGen = -1;
    private int _pendingReason = -1;
    private int _debugCount;

    private EventSource? _runtimeEventSource;

    public GcTraceListener()
    {
        // Walk already-created event sources (typical case: runtime is created
        // before this listener). For sources created later, OnEventSourceCreated
        // fires for us.
        foreach (EventSource es in EventSource.GetSources())
        {
            if (es.Name == ProviderName)
            {
                _runtimeEventSource = es;
                EnableEvents(es, EventLevel.Informational, GCKeyword);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _records.Clear();
        }
    }

    public List<PauseRecord> Snapshot()
    {
        lock (_lock)
        {
            return new List<PauseRecord>(_records);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == ProviderName)
        {
            _runtimeEventSource = eventSource;
            EnableEvents(eventSource, EventLevel.Informational, GCKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Debug: print first N events seen so we can confirm event names.
        if (Environment.GetEnvironmentVariable("KESTREL_BENCH_GC_TRACE_DEBUG") == "1")
        {
            int idx = Interlocked.Increment(ref _debugCount);
            if (idx <= 50)
            {
                string payloadDesc = "";
                if (eventData.Payload is { Count: > 0 } p)
                {
                    payloadDesc = " [" + string.Join(",", p.Take(6).Select(x => x?.ToString() ?? "null")) + "]";
                }
                Console.WriteLine($"  [gc-evt {idx,3}] {eventData.EventName} id={eventData.EventId}{payloadDesc}");
            }
        }

        // GCStart_V2: Count, Depth, Reason, Type, ClrInstanceID, ...
        // Depth is the generation actually being collected (0/1/2).
        if (eventData.EventName is "GCStart_V2" or "GCStart_V1" or "GCStart")
        {
            if (eventData.Payload is { Count: >= 3 })
            {
                _pendingGen = ConvertInt(eventData.Payload[1]);
                _pendingReason = ConvertInt(eventData.Payload[2]);
                if (Environment.GetEnvironmentVariable("KESTREL_BENCH_GC_TRACE_DEBUG") == "1")
                {
                    Console.WriteLine($"  [gc-trace] GCStart fired: gen={_pendingGen} reason={_pendingReason}");
                }
            }
            return;
        }

        if (eventData.EventName is "GCSuspendEEBegin_V1" or "GCSuspendEEBegin")
        {
            _suspendStartTicks = Stopwatch.GetTimestamp();
            return;
        }

        if (eventData.EventName is "GCRestartEEEnd_V1" or "GCRestartEEEnd")
        {
            if (_suspendStartTicks > 0)
            {
                long now = Stopwatch.GetTimestamp();
                double pauseMs = (now - _suspendStartTicks) * 1000.0 / Stopwatch.Frequency;
                int gen = _pendingGen;
                int reason = _pendingReason;
                if (Environment.GetEnvironmentVariable("KESTREL_BENCH_GC_TRACE_DEBUG") == "1")
                {
                    Console.WriteLine($"  [gc-trace] RestartEE: pauseMs={pauseMs:F3} gen={gen}");
                }
                lock (_lock)
                {
                    _records.Add(new PauseRecord(gen, reason, pauseMs, _suspendStartTicks));
                }
            }
            _suspendStartTicks = 0;
            _pendingGen = -1;
            _pendingReason = -1;
        }
    }

    private static int ConvertInt(object? o) => o switch
    {
        null => -1,
        int i => i,
        uint u => (int)u,
        long l => (int)l,
        ulong ul => (int)ul,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        _ => -1,
    };

    public static void PrintSummary(IReadOnlyList<PauseRecord> records, double totalRunMs)
    {
        if (records.Count == 0)
        {
            Console.WriteLine("=== GC pause breakdown ===");
            Console.WriteLine("  (no GC pauses recorded)");
            return;
        }

        Console.WriteLine("=== GC pause breakdown (post-warmup) ===");
        Console.WriteLine($"  total pauses    : {records.Count,8}");
        double totalPauseMs = records.Sum(r => r.PauseMs);
        Console.WriteLine($"  total pause ms  : {totalPauseMs,8:F2}");
        if (totalRunMs > 0)
        {
            Console.WriteLine($"  % time in GC    : {100.0 * totalPauseMs / totalRunMs,8:F2}");
        }
        Console.WriteLine();

        var byGen = records.GroupBy(r => r.Generation).OrderBy(g => g.Key).ToList();
        Console.WriteLine("  gen   count    sum ms    avg ms    p50 ms    p99 ms    max ms");
        Console.WriteLine("  ---  ------  --------  --------  --------  --------  --------");
        foreach (var grp in byGen)
        {
            var sorted = grp.Select(r => r.PauseMs).OrderBy(x => x).ToArray();
            int n = sorted.Length;
            double sum = sorted.Sum();
            double avg = sum / n;
            double p50 = sorted[(int)(n * 0.50)];
            double p99 = sorted[Math.Min(n - 1, (int)(n * 0.99))];
            double max = sorted[n - 1];
            string genLabel = grp.Key switch
            {
                -1 => "unk",
                _ => grp.Key.ToString(),
            };
            Console.WriteLine($"  {genLabel,3}  {n,6}  {sum,8:F2}  {avg,8:F3}  {p50,8:F3}  {p99,8:F3}  {max,8:F3}");
        }

        // All-up.
        var allSorted = records.Select(r => r.PauseMs).OrderBy(x => x).ToArray();
        {
            int n = allSorted.Length;
            double sum = allSorted.Sum();
            double avg = sum / n;
            double p50 = allSorted[(int)(n * 0.50)];
            double p99 = allSorted[Math.Min(n - 1, (int)(n * 0.99))];
            double max = allSorted[n - 1];
            Console.WriteLine($"  all  {n,6}  {sum,8:F2}  {avg,8:F3}  {p50,8:F3}  {p99,8:F3}  {max,8:F3}");
        }
    }

    public static void WriteCsv(IReadOnlyList<PauseRecord> records, string path)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("idx,gen,reason,pause_ms,wall_ticks");
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            sw.WriteLine($"{i},{r.Generation},{r.Reason},{r.PauseMs:F4},{r.WallTicks}");
        }
    }
}
