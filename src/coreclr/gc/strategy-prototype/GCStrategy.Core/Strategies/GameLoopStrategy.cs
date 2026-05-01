// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// GameLoopStrategy — An LLM-generated strategy optimized for game workloads.
//
// KEY INSIGHT: Games have a fixed frame budget (e.g., 16ms for 60fps).
// GC pauses within a frame are unacceptable. This strategy:
//   - Never collects during a frame (only at frame boundaries)
//   - Uses incremental marking with strict time budgets
//   - Bulk-frees frame-scoped allocations at frame end
//   - Only does full GC during loading screens
// ============================================================================

namespace GCStrategy.Core.Strategies;

/// <summary>
/// Strategy optimized for game loop workloads with strict frame timing.
/// </summary>
public sealed class GameLoopStrategy : IGCStrategy
{
    private bool _inFrame;
    private readonly TimeSpan _frameBudget = TimeSpan.FromMilliseconds(1); // 1ms max for GC per frame

    public string Name => "GameLoop (Frame-Budget Constrained)";

    public CollectionPlan ShouldCollect(in GCTelemetry telemetry)
    {
        // NEVER trigger collection from allocation pressure during a frame
        if (_inFrame)
            return CollectionPlan.None;

        // Between frames, do incremental work with strict budget
        if (telemetry.MemoryPressure > 0.85)
            return CollectionPlan.IncrementalMark(_frameBudget);

        return CollectionPlan.None;
    }

    public PromotionDecision ShouldPromote(int objectAge, long objectSize, int currentGeneration)
    {
        // Games: most per-frame objects die immediately.
        // Only promote objects that survive 10+ frames (likely persistent game state).
        if (objectAge >= 10 && currentGeneration < 2)
            return PromotionDecision.Promote;

        return PromotionDecision.Keep;
    }

    public CollectionPlan OnAppEvent(string eventName, in GCTelemetry telemetry)
    {
        switch (eventName)
        {
            case "frame.start":
                _inFrame = true;
                return CollectionPlan.None;

            case "frame.end":
                _inFrame = false;
                // Do incremental work between frames if needed
                if (telemetry.MemoryPressure > 0.7)
                    return CollectionPlan.IncrementalMark(_frameBudget);
                return CollectionPlan.None;

            case "loading_screen.start":
                // Loading screens are the perfect time for full compacting GC
                return CollectionPlan.Collect(2, compact: true);

            default:
                return CollectionPlan.None;
        }
    }
}
