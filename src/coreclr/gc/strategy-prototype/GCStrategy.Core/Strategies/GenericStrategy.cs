// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// GenericStrategy — The default "one size fits all" GC strategy.
//
// This represents what a traditional GC does: threshold-based collection,
// age-based promotion, generational collection.
// ============================================================================

namespace GCStrategy.Core.Strategies;

/// <summary>
/// A generic generational strategy. Collects when memory pressure exceeds threshold.
/// Promotes objects after they survive N collections.
/// </summary>
public sealed class GenericStrategy : IGCStrategy
{
    private const double CollectionThreshold = 0.75;
    private const int PromotionAge = 2;

    public string Name => "Generic (One-Size-Fits-All)";

    public CollectionPlan ShouldCollect(in GCTelemetry telemetry)
    {
        // Classic approach: collect when pressure is high
        if (telemetry.MemoryPressure > CollectionThreshold)
        {
            // Full gen0 collect, maybe gen1 if very high pressure
            int gen = telemetry.MemoryPressure > 0.9 ? 1 : 0;
            return CollectionPlan.Collect(gen, compact: telemetry.MemoryPressure > 0.95);
        }

        // Fallback: collect gen0 if lots of bytes allocated
        if (telemetry.BytesSinceLastCollection > 512 * 1024)
        {
            return CollectionPlan.Collect(0);
        }

        return CollectionPlan.None;
    }

    public PromotionDecision ShouldPromote(int objectAge, long objectSize, int currentGeneration)
    {
        // Classic: promote after surviving N collections
        if (objectAge >= PromotionAge && currentGeneration < 2)
            return PromotionDecision.Promote;

        return PromotionDecision.Keep;
    }

    public CollectionPlan OnAppEvent(string eventName, in GCTelemetry telemetry)
    {
        // Generic strategy ignores app events — it doesn't know about app semantics
        return CollectionPlan.None;
    }
}
