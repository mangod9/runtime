// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// SimpleGC policy-host: M1a observability tool.
//
// Allocates a known mix of objects, then queries simplegc's per-MT counters
// via P/Invoke and prints the top-N. Verifies that the post-hoc attribution
// pipeline (slow-path Alloc -> chunk walk -> per-MT counters) produces
// realistic numbers.
//
// Run:
//   $env:DOTNET_GCName = "simplegc.dll"
//   dotnet policy-host.dll
//
// The simplegc.dll must be next to policy-host.dll (copied from the
// CoreCLR build output).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SimpleGCPolicyHost;

// ---- P/Invoke surface ------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
internal struct SimpleGCMtStat
{
    public ulong  MtToken;     // MethodTable* as opaque uint64
    public ulong  Count;
    public ulong  Bytes;
    public uint   MinSize;
    public uint   MaxSize;
}

internal static class SimpleGCInterop
{
    private const string Lib = "simplegc";

    [DllImport(Lib, EntryPoint = "simplegc_get_mt_stats")]
    public static extern uint GetMtStats([Out] SimpleGCMtStat[] buffer, uint capacity);

    [DllImport(Lib, EntryPoint = "simplegc_get_mt_summary")]
    public static extern void GetMtSummary(
        out ulong attributedObjects,
        out ulong attributedBytes,
        out uint  distinctMts,
        out ulong overflowObjects);

    [DllImport(Lib, EntryPoint = "simplegc_reset_mt_stats")]
    public static extern void ResetMtStats();

    [DllImport(Lib, EntryPoint = "simplegc_enable_mt_tracking")]
    public static extern void EnableMtTracking(int enable);

    [DllImport(Lib, EntryPoint = "simplegc_is_mt_tracking_enabled")]
    public static extern int IsMtTrackingEnabled();

    [DllImport(Lib, EntryPoint = "simplegc_get_arena_stats")]
    public static extern void GetArenaStats(
        out ulong permBytesUsed,
        out ulong permBytesCommitted,
        out ulong requestBytesUsed,
        out ulong requestBytesCommitted);

    public static bool IsLoaded
    {
        get
        {
            try
            {
                GetMtSummary(out _, out _, out _, out _);
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}

// ---- MT token -> Type resolver --------------------------------------------
//
// Builds a one-shot map from RuntimeTypeHandle.Value (== MethodTable*) to
// Type by walking all loaded assemblies. Any MT not in the map is opaque
// (typically generic instantiations or runtime-internal types).

internal static class TypeResolver
{
    private static Dictionary<IntPtr, Type>? s_map;

    public static void Build()
    {
        var map = new Dictionary<IntPtr, Type>(capacity: 4096);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
            foreach (var t in types)
            {
                if (t is null) continue;
                IntPtr h = t.TypeHandle.Value;
                if (h != IntPtr.Zero) map[h] = t;
            }
        }
        s_map = map;
    }

    public static string Resolve(ulong mtToken)
    {
        IntPtr handle = unchecked((IntPtr)(long)mtToken);
        if (s_map is { } m && m.TryGetValue(handle, out var t))
        {
            return t.FullName ?? t.Name;
        }
        // Many MTs aren't reachable via reflection (closed generics created by JIT,
        // string instantiations, etc.). Print the token so we can at least correlate.
        return $"<MT 0x{mtToken:x}>";
    }
}

// ---- Synthetic allocation workload ----------------------------------------

internal sealed class SmallNode
{
    public int A, B, C, D;
}

internal sealed class FixedShape32
{
    public long X, Y, Z, W;  // 32 bytes payload
}

internal sealed class CacheEntry
{
    public string Key = "";
    public byte[] Value = Array.Empty<byte>();
}

internal static class Workload
{
    // Returns aggregate data so the live references hold the allocations and
    // the runtime doesn't optimize them away.
    public static (long sumNodes, long sumFixed, long totalCacheBytes) Run(
        int nodeCount, int fixedCount, int cacheEntries, int cacheValueSize)
    {
        long sumN = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            var n = new SmallNode { A = i, B = i + 1, C = i + 2, D = i + 3 };
            sumN += n.A;
        }

        long sumF = 0;
        for (int i = 0; i < fixedCount; i++)
        {
            var f = new FixedShape32 { X = i, Y = i, Z = i, W = i };
            sumF += f.X;
        }

        var cache = new List<CacheEntry>(cacheEntries);
        for (int i = 0; i < cacheEntries; i++)
        {
            cache.Add(new CacheEntry
            {
                Key = "k_" + i,
                Value = new byte[cacheValueSize]
            });
        }
        long total = 0;
        foreach (var e in cache) total += e.Value.Length;

        // Arrays of various element types.
        var ints   = new int[1024];
        var longs  = new long[512];
        var bytes  = new byte[4096];
        var strs   = new string[256];
        for (int i = 0; i < strs.Length; i++) strs[i] = "x_" + i;

        GC.KeepAlive(cache);
        GC.KeepAlive(ints);
        GC.KeepAlive(longs);
        GC.KeepAlive(bytes);
        GC.KeepAlive(strs);

        return (sumN, sumF, total);
    }
}

// ---- Reporter --------------------------------------------------------------

internal static class Reporter
{
    public static void DumpTopN(int topN)
    {
        var buf = new SimpleGCMtStat[8192];
        uint count = SimpleGCInterop.GetMtStats(buf, (uint)buf.Length);
        var entries = new List<SimpleGCMtStat>((int)count);
        for (uint i = 0; i < count; i++) entries.Add(buf[i]);
        entries.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        SimpleGCInterop.GetMtSummary(out ulong objs, out ulong bytes, out uint distinct, out ulong overflow);
        Console.WriteLine();
        Console.WriteLine($"=== MT stats: {distinct:N0} distinct types, "
            + $"{objs:N0} objects, {bytes:N0} bytes attributed, {overflow:N0} overflow ===");
        Console.WriteLine($"{"#",3} {"count",12} {"bytes",14} {"avg",6} {"min..max",13}  type");

        int n = Math.Min(topN, entries.Count);
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            ulong avg = e.Count > 0 ? e.Bytes / e.Count : 0;
            string range = (e.MinSize == e.MaxSize) ? $"{e.MinSize}" : $"{e.MinSize}..{e.MaxSize}";
            Console.WriteLine($"{i + 1,3} {e.Count,12:N0} {e.Bytes,14:N0} {avg,6} {range,13}  {TypeResolver.Resolve(e.MtToken)}");
        }
    }
}

// ---- Entry point -----------------------------------------------------------

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("=== SimpleGC policy-host ===");
        Console.WriteLine($"  GC name (env)   : {Environment.GetEnvironmentVariable("DOTNET_GCName") ?? "(unset)"}");
        Console.WriteLine($"  simplegc loaded : {SimpleGCInterop.IsLoaded}");

        if (!SimpleGCInterop.IsLoaded)
        {
            Console.Error.WriteLine("ERROR: simplegc.dll not loaded. Set DOTNET_GCName=simplegc.dll and copy simplegc.dll next to this binary.");
            return 1;
        }

        // Build the type-name resolver early so it gets attributed against
        // (we want to see what reflection costs, too).
        TypeResolver.Build();

        // Now that startup (incl. NativeRuntimeEventSource cctor + reflection)
        // is past, arm per-MT tracking. The post-hoc walk dereferences MTs
        // we read out of the heap, so we must not run it during very early
        // CLR initialization.
        SimpleGCInterop.EnableMtTracking(1);
        Console.WriteLine($"  mt tracking     : {(SimpleGCInterop.IsMtTrackingEnabled() != 0 ? "enabled" : "disabled")}");

        // Reset counters so we measure only the workload below.
        SimpleGCInterop.ResetMtStats();

        int nodes = 200_000, fixedShape = 100_000, cacheEntries = 500, cacheValueSize = 256;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--nodes")  int.TryParse(args[i + 1], out nodes);
            if (args[i] == "--fixed")  int.TryParse(args[i + 1], out fixedShape);
            if (args[i] == "--cache")  int.TryParse(args[i + 1], out cacheEntries);
            if (args[i] == "--bsize")  int.TryParse(args[i + 1], out cacheValueSize);
        }

        Console.WriteLine();
        Console.WriteLine($"workload: nodes={nodes:N0} fixed={fixedShape:N0} cache={cacheEntries:N0}*{cacheValueSize}B");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (sumN, sumF, totalCache) = Workload.Run(nodes, fixedShape, cacheEntries, cacheValueSize);
        sw.Stop();

        Console.WriteLine($"workload done in {sw.ElapsedMilliseconds} ms (sumN={sumN}, sumF={sumF}, cache={totalCache}B)");

        SimpleGCInterop.GetArenaStats(out ulong permUsed, out ulong permCom, out ulong reqUsed, out ulong reqCom);
        Console.WriteLine($"perm arena: {permUsed:N0} used / {permCom:N0} committed");

        Reporter.DumpTopN(20);
        return 0;
    }
}
