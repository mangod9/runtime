// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// Demo: Compare Generic vs WebApi vs GameLoop GC strategies
//
// This simulates three workload patterns and shows how app-specific strategies
// outperform the generic one by exploiting workload knowledge.
// ============================================================================

using GCStrategy.Core;
using GCStrategy.Core.Strategies;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  LLM-Generated GC Strategy Prototype — Option C Demo        ║");
Console.WriteLine("║  Architecture: Managed Strategy + Native Mechanics           ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// SCENARIO 1: Web API Workload
// Pattern: Many short-lived request-scoped objects
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  SCENARIO: Web API (1000 requests, each allocating 50 objects)");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();

RunWebApiWorkload(new GenericStrategy());
RunWebApiWorkload(new WebApiStrategy());
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// SCENARIO 2: Game Loop Workload
// Pattern: Per-frame allocations with strict timing
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  SCENARIO: Game Loop (500 frames, 20 objects per frame)");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();

RunGameLoopWorkload(new GenericStrategy());
RunGameLoopWorkload(new GameLoopStrategy());
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// Summary
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  KEY TAKEAWAY");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();
Console.WriteLine("  The Generic strategy treats all workloads the same — it can't");
Console.WriteLine("  exploit app-specific knowledge (request boundaries, frame timing).");
Console.WriteLine();
Console.WriteLine("  An LLM analyzing the app's code would recognize these patterns");
Console.WriteLine("  and generate a strategy that:");
Console.WriteLine("    • Bulk-frees at natural lifecycle boundaries (no mark phase!)");
Console.WriteLine("    • Respects timing constraints (never pauses during a frame)");
Console.WriteLine("    • Tunes promotion for the specific lifetime distribution");
Console.WriteLine();
Console.WriteLine("  The mechanics layer (C++ in production) stays FIXED and VERIFIED.");
Console.WriteLine("  Only the strategy changes — and it's safe because it only returns");
Console.WriteLine("  decisions, never touches raw memory directly.");

// ═══════════════════════════════════════════════════════════════
// Workload Implementations
// ═══════════════════════════════════════════════════════════════

static void RunWebApiWorkload(IGCStrategy strategy)
{
    var gc = new GCMechanics(strategy);

    // Simulate a singleton service (long-lived root)
    var singleton = gc.Allocate(1024, typeId: 1, isRoot: true);

    // Simulate 1000 HTTP requests
    for (int req = 0; req < 1000; req++)
    {
        gc.SignalAppEvent("request.start");

        // Each request allocates ~50 objects (controllers, DTOs, buffers, etc.)
        var requestObjects = new List<SimObject>();
        for (int i = 0; i < 50; i++)
        {
            var obj = gc.Allocate(size: 128 + (i * 16), typeId: 100 + i);
            requestObjects.Add(obj);

            // Some objects reference each other (simulates object graph)
            if (i > 0)
                requestObjects[i - 1].References.Add(obj);
        }

        // Request completes — all request objects become garbage
        foreach (var obj in requestObjects)
            gc.RemoveRoot(obj);

        gc.SignalAppEvent("request.end");
    }

    PrintResults(gc);
}

static void RunGameLoopWorkload(IGCStrategy strategy)
{
    var gc = new GCMechanics(strategy);

    // Persistent game state (long-lived)
    var gameState = gc.Allocate(4096, typeId: 1, isRoot: true);

    // Simulate 500 frames
    for (int frame = 0; frame < 500; frame++)
    {
        gc.SignalAppEvent("frame.start");

        // Each frame allocates temporary objects (particles, UI elements, etc.)
        for (int i = 0; i < 20; i++)
        {
            var obj = gc.Allocate(size: 64 + (i * 8), typeId: 200 + i);
            // These are frame-scoped, not rooted — they become garbage
        }

        gc.SignalAppEvent("frame.end");

        // Simulate a loading screen every 100 frames
        if (frame > 0 && frame % 100 == 0)
        {
            gc.SignalAppEvent("loading_screen.start");
        }
    }

    PrintResults(gc);
}

static void PrintResults(GCMechanics gc)
{
    var stats = gc.Stats;
    Console.WriteLine($"  Strategy: {gc.StrategyName}");
    Console.WriteLine($"    Collections:    {stats.Count}");
    Console.WriteLine($"    Total freed:    {stats.Sum(s => s.BytesFreed):N0} bytes");
    Console.WriteLine($"    Objects freed:  {stats.Sum(s => s.ObjectsFreed):N0}");
    Console.WriteLine($"    Promoted:       {stats.Sum(s => s.ObjectsPromoted):N0}");
    Console.WriteLine($"    Total pause:    {TimeSpan.FromTicks(stats.Sum(s => s.PauseDuration.Ticks)).TotalMilliseconds:F3}ms");
    Console.WriteLine($"    Heap now:       {gc.TotalAllocated:N0} bytes in {gc.TotalRegions} regions");
    Console.WriteLine();
}
