// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// ============================================================================
// IGCMemoryModel — The interface for defining HOW memory is organized.
//
// This goes beyond "when to collect" — it lets the app define:
//   - What memory spaces exist (arenas, generations, pools)
//   - How each space allocates (bump-pointer, free-list, pool)
//   - How each space is collected (bulk-free, mark-sweep, compact)
//   - Where allocations are routed based on their characteristics
//
// The native mechanics layer becomes a TOOLKIT that the memory model assembles.
// The generic generational GC is just ONE possible model — not the default.
// ============================================================================

namespace GCStrategy.Core;

// ═══════════════════════════════════════════════════════════════
// Memory Space Configuration
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// How a memory space allocates objects.
/// </summary>
public enum AllocatorKind
{
    /// <summary>Bump-pointer — fast, sequential, requires bulk-free or compaction.</summary>
    BumpPointer,

    /// <summary>Free-list — slower alloc, but can reuse individual freed slots.</summary>
    FreeList,

    /// <summary>Fixed-size pool — all objects same size, extremely fast alloc/free.</summary>
    FixedPool,

    /// <summary>Thread-local bump-pointer — no locking on fast path.</summary>
    ThreadLocalBumpPointer,
}

/// <summary>
/// How a memory space reclaims dead objects.
/// </summary>
public enum CollectorKind
{
    /// <summary>Arena reset — frees entire space at once, O(1), no tracing needed.</summary>
    BulkFree,

    /// <summary>Mark-sweep — traces live objects, frees dead ones in-place.</summary>
    MarkSweep,

    /// <summary>Mark-compact — traces live objects, moves them together, eliminates fragmentation.</summary>
    MarkCompact,

    /// <summary>Incremental mark — mark with a time budget per step.</summary>
    IncrementalMark,

    /// <summary>Concurrent mark — mark on a background thread while app runs.</summary>
    ConcurrentMark,

    /// <summary>Reference counting — freed immediately when refcount drops to zero.</summary>
    ReferenceCounting,

    /// <summary>No collection — space is manually managed (app calls Free explicitly).</summary>
    Manual,
}

/// <summary>
/// What write barrier a space uses for tracking cross-space references.
/// </summary>
public enum BarrierKind
{
    /// <summary>No barrier — objects in this space never escape (enforced by native layer).</summary>
    None,

    /// <summary>Card table — coarse-grained tracking, low overhead, good for generational.</summary>
    CardTable,

    /// <summary>Remembered set — precise per-reference tracking, higher overhead but exact.</summary>
    RememberedSet,

    /// <summary>Escape detection — no ongoing barrier, but detects when objects escape.</summary>
    EscapeDetect,
}

/// <summary>
/// What triggers collection for a space.
/// </summary>
public enum TriggerKind
{
    /// <summary>Collect when space is full (allocation fails).</summary>
    SpaceFull,

    /// <summary>Collect at app-defined lifecycle boundaries.</summary>
    AppEvent,

    /// <summary>Collect based on memory pressure threshold.</summary>
    PressureThreshold,

    /// <summary>Collect on a time interval.</summary>
    TimeBased,

    /// <summary>Never collect automatically — only when explicitly triggered.</summary>
    Manual,
}

/// <summary>
/// Configuration for a single memory space.
/// </summary>
public sealed class MemorySpaceConfig
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required AllocatorKind Allocator { get; init; }
    public required CollectorKind Collector { get; init; }
    public required BarrierKind Barrier { get; init; }
    public required TriggerKind Trigger { get; init; }

    /// <summary>Initial size of the space. 0 = auto-sized by native layer.</summary>
    public long InitialSize { get; init; }

    /// <summary>Max size the space can grow to. 0 = unbounded.</summary>
    public long MaxSize { get; init; }

    /// <summary>For FixedPool allocator: the fixed object size.</summary>
    public int PoolObjectSize { get; init; }

    /// <summary>For PressureThreshold trigger: when to trigger (0.0-1.0).</summary>
    public double PressureThreshold { get; init; } = 0.75;

    /// <summary>For AppEvent trigger: which event names trigger collection.</summary>
    public string[]? TriggerEvents { get; init; }

    /// <summary>For IncrementalMark: time budget per collection step.</summary>
    public TimeSpan? IncrementalBudget { get; init; }

    /// <summary>Can objects in this space be pinned?</summary>
    public bool AllowPinning { get; init; }

    /// <summary>Can objects in this space have finalizers?</summary>
    public bool AllowFinalizers { get; init; }

    /// <summary>
    /// What happens when an object escapes from this space (referenced from another space).
    /// Only relevant for arena/bulk-free spaces.
    /// </summary>
    public EscapePolicy EscapePolicy { get; init; } = EscapePolicy.PromoteTo(1);
}

/// <summary>
/// Policy for what happens when an object escapes its space.
/// </summary>
public readonly record struct EscapePolicy
{
    /// <summary>The space to move escaped objects to.</summary>
    public int TargetSpaceId { get; init; }

    /// <summary>Whether to fail (throw) instead of promoting. Strict mode for testing.</summary>
    public bool Fail { get; init; }

    public static EscapePolicy PromoteTo(int spaceId) => new() { TargetSpaceId = spaceId };
    public static EscapePolicy FailOnEscape => new() { Fail = true };
}

// ═══════════════════════════════════════════════════════════════
// Allocation Routing
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Information about an allocation request, used for routing decisions.
/// </summary>
public readonly record struct AllocationRequest
{
    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Whether the object will be pinned.</summary>
    public bool Pinned { get; init; }

    /// <summary>Whether the object has a finalizer.</summary>
    public bool HasFinalizer { get; init; }

    /// <summary>Type category hint (set by runtime or app).</summary>
    public TypeCategory Category { get; init; }

    /// <summary>Thread-local context (e.g., which request this allocation belongs to).</summary>
    public int ContextId { get; init; }
}

/// <summary>
/// High-level type categories for routing allocations.
/// These map from runtime type metadata or app-provided hints.
/// </summary>
public enum TypeCategory
{
    Unknown,
    /// <summary>Short-lived, likely dies within current scope.</summary>
    Ephemeral,
    /// <summary>Long-lived singleton or service.</summary>
    LongLived,
    /// <summary>Buffer or array used for I/O.</summary>
    Buffer,
    /// <summary>Part of a request/scope lifecycle.</summary>
    Scoped,
    /// <summary>Cached data with uncertain lifetime.</summary>
    Cached,
}

// ═══════════════════════════════════════════════════════════════
// Telemetry per space
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Telemetry for a single memory space.
/// </summary>
public readonly record struct SpaceTelemetry
{
    public int SpaceId { get; init; }
    public long AllocatedBytes { get; init; }
    public long Capacity { get; init; }
    public int ObjectCount { get; init; }
    public int CollectionCount { get; init; }
    public TimeSpan TimeSinceLastCollection { get; init; }
    public double FillRatio { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// The Memory Model Interface
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Defines the entire memory management architecture for an application.
/// An LLM generates an implementation of this based on app analysis.
///
/// This replaces the traditional "one GC for all" approach with an
/// app-specific memory model assembled from composable primitives.
/// </summary>
public interface IGCMemoryModel
{
    /// <summary>
    /// Model name for diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Define the memory spaces. Called once at startup.
    /// This is WHERE the app's memory topology is declared.
    /// </summary>
    MemorySpaceConfig[] DefineSpaces();

    /// <summary>
    /// Route an allocation to a space. Called on the allocation slow path.
    /// Returns the space ID to allocate in.
    /// </summary>
    int RouteAllocation(in AllocationRequest request);

    /// <summary>
    /// React to an app event. Can trigger collection of specific spaces.
    /// Returns space IDs to collect (empty = no action).
    /// </summary>
    int[] OnAppEvent(string eventName);

    /// <summary>
    /// Called periodically by the native layer. Chance to rebalance or reconfigure.
    /// </summary>
    void OnTelemetryUpdate(ReadOnlySpan<SpaceTelemetry> spaceTelemetry);
}
