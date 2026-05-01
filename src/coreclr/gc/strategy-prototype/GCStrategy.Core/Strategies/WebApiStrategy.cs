// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// WebApiStrategy — An LLM-generated strategy optimized for web API workloads.
//
// KEY INSIGHT: In a web API, most allocations are request-scoped. They are
// created during request processing and die when the request completes.
// This strategy exploits that pattern:
//   - Uses region-based bulk freeing at request boundaries
//   - Avoids tracing/marking request-scoped objects entirely
//   - Only does generational collection for objects that escape request scope
//
// This is what an LLM would generate after analyzing a web API codebase and
// seeing patterns like: DI scopes per request, response serialization buffers,
// middleware pipelines creating per-request state.
// ============================================================================

namespace GCStrategy.Core.Strategies;

/// <summary>
/// Strategy optimized for web API workloads with request-scoped allocation patterns.
/// </summary>
public sealed class WebApiStrategy : IGCStrategy
{
    private readonly List<int> _requestRegions = new();
    private int _activeRequests;

    public string Name => "WebApi (Request-Scoped Optimization)";

    public CollectionPlan ShouldCollect(in GCTelemetry telemetry)
    {
        // We're much less aggressive about triggering GC because we know
        // most objects will die at request boundaries via bulk-free.
        // Only collect if memory pressure is genuinely high.
        if (telemetry.MemoryPressure > 0.9)
        {
            return CollectionPlan.Collect(1, compact: true);
        }

        // If pressure is moderate but we haven't had a request boundary in a while,
        // do a lightweight gen0 sweep.
        if (telemetry.MemoryPressure > 0.8 &&
            telemetry.TimeSinceLastCollection > TimeSpan.FromSeconds(5))
        {
            return CollectionPlan.Collect(0);
        }

        return CollectionPlan.None;
    }

    public PromotionDecision ShouldPromote(int objectAge, long objectSize, int currentGeneration)
    {
        // Be LESS aggressive about promotion in a web API:
        // Most objects die at request boundaries, so promoting them wastes effort.
        // Only promote objects that have survived many cycles (likely singletons/caches).
        if (objectAge >= 5 && currentGeneration < 2)
            return PromotionDecision.Promote;

        // Large objects that survive are likely caches — promote sooner
        if (objectAge >= 3 && objectSize > 8192 && currentGeneration < 2)
            return PromotionDecision.Promote;

        return PromotionDecision.Keep;
    }

    public CollectionPlan OnAppEvent(string eventName, in GCTelemetry telemetry)
    {
        switch (eventName)
        {
            case "request.start":
                _activeRequests++;
                // Track which regions are being used for this request's allocations
                _requestRegions.Clear();
                for (int i = 0; i < telemetry.ActiveRegionCount; i++)
                    _requestRegions.Add(i);
                return CollectionPlan.None;

            case "request.end":
                _activeRequests--;
                // KEY OPTIMIZATION: Bulk-free the request regions without tracing!
                // This is O(1) per region instead of O(n) per object.
                if (_requestRegions.Count > 0)
                {
                    var regionsToFree = _requestRegions.ToArray();
                    _requestRegions.Clear();
                    return CollectionPlan.FreeRegions(regionsToFree);
                }
                return CollectionPlan.None;

            case "low_traffic":
                // During low traffic, do a full compacting collection to reduce footprint
                return CollectionPlan.Collect(2, compact: true);

            default:
                return CollectionPlan.None;
        }
    }
}
