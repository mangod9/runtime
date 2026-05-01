// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// Model Demo: Traditional GC vs WebApi Model vs Game Model
//
// Same workload, different memory models — shows how eliminating generic GC
// assumptions materially changes behavior.
// ============================================================================

using GCStrategy.Core;
using GCStrategy.Core.MemoryModels;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Memory Model Demo — Same Workload, Different Models        ║");
Console.WriteLine("║  Showing: Generic GC goo is NOT needed when you know the app║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// SCENARIO: Web API Workload (2000 requests, 30 objects each)
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  WORKLOAD: Web API — 2000 requests, 30 objects each");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();

RunWebApiWorkload(new TraditionalMemoryModel());
RunWebApiWorkload(new WebApiMemoryModel());
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// SCENARIO: Game Loop Workload (1000 frames, 30 objects each)
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  WORKLOAD: Game — 1000 frames, 30 temp objects per frame");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();

RunGameWorkload(new TraditionalMemoryModel());
RunGameWorkload(new GameMemoryModel());
Console.WriteLine();

// ═══════════════════════════════════════════════════════════════
// ANALYSIS
// ═══════════════════════════════════════════════════════════════

Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine("  WHY THE APP-SPECIFIC MODELS WIN");
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine();
Console.WriteLine("  Traditional GC (what .NET does today):");
Console.WriteLine("    • ALL objects go to Gen0 → traced → promoted → traced again");
Console.WriteLine("    • Mark phase walks entire object graph every collection");
Console.WriteLine("    • No awareness of request boundaries or frame timing");
Console.WriteLine("    • Promotion wastes effort on objects about to die");
Console.WriteLine();
Console.WriteLine("  WebApi Memory Model:");
Console.WriteLine("    • Request objects → arena (NEVER traced, bulk-freed in O(1))");
Console.WriteLine("    • Only escaped/long-lived objects get traced");
Console.WriteLine("    • Result: most GC work is eliminated entirely");
Console.WriteLine();
Console.WriteLine("  Game Memory Model:");
Console.WriteLine("    • Frame objects → double-buffer arena (zero-cost free)");
Console.WriteLine("    • Persistent objects → incremental with strict time budget");
Console.WriteLine("    • Result: predictable frame timing, no surprise pauses");
Console.WriteLine();
Console.WriteLine("  The insight: the 'generic goo' (generational, promote-by-age,");
Console.WriteLine("  trace-everything) exists because the GC doesn't know the app.");
Console.WriteLine("  When you TELL it the app's memory model, most of that work");
Console.WriteLine("  becomes unnecessary.");

// ═══════════════════════════════════════════════════════════════
// Workload Implementations
// ═══════════════════════════════════════════════════════════════

static void RunWebApiWorkload(IGCMemoryModel model)
{
    var engine = new ModelDrivenMechanics(model);

    // Singleton service (long-lived)
    engine.Allocate(new AllocationRequest
    {
        Size = 2048, Category = TypeCategory.LongLived
    });

    for (int req = 0; req < 2000; req++)
    {
        // Each request: 30 scoped objects + some buffers
        for (int i = 0; i < 25; i++)
        {
            engine.Allocate(new AllocationRequest
            {
                Size = 128 + (i * 16),
                Category = TypeCategory.Scoped,
                ContextId = req,
            });
        }

        // 5 buffer allocations per request
        for (int i = 0; i < 5; i++)
        {
            engine.Allocate(new AllocationRequest
            {
                Size = 4096,
                Category = TypeCategory.Buffer,
            });
        }

        // Request ends
        engine.SignalAppEvent("request.end");
        engine.CheckTriggers();
    }

    engine.PrintSummary();
}

static void RunGameWorkload(IGCMemoryModel model)
{
    var engine = new ModelDrivenMechanics(model);

    // Persistent game state
    engine.Allocate(new AllocationRequest
    {
        Size = 16384, Category = TypeCategory.LongLived
    });

    // Some cached assets
    for (int i = 0; i < 10; i++)
    {
        engine.Allocate(new AllocationRequest
        {
            Size = 65536, Category = TypeCategory.Cached
        });
    }

    for (int frame = 0; frame < 1000; frame++)
    {
        // Per-frame temp objects (particles, UI elements, vectors)
        for (int i = 0; i < 30; i++)
        {
            engine.Allocate(new AllocationRequest
            {
                Size = 64 + (i * 4),
                Category = TypeCategory.Ephemeral,
            });
        }

        engine.SignalAppEvent("frame.end");
        engine.CheckTriggers();

        // Loading screen every 200 frames
        if (frame > 0 && frame % 200 == 0)
        {
            engine.SignalAppEvent("loading_screen");
        }
    }

    engine.PrintSummary();
}
