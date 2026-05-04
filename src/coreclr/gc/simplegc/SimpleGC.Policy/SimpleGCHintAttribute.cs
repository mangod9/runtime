namespace SimpleGC.Policy;

/// <summary>
/// Declarative GC hint placed on a type to tell simplegc how the
/// application intends instances of that type to live. The hint expresses
/// developer (or LLM) <em>intent</em>; the runtime maps that intent to a
/// concrete <see cref="Route"/> based on which substrate regions are
/// available in the current build.
/// </summary>
/// <remarks>
/// <para>
/// This attribute is the "policy as code" entry point for simplegc:
/// rather than maintaining a separate hand-curated list of types to seed
/// (see <c>ManagedSeed</c> in kestrel-bench), an LLM generating
/// application code can express its understanding of each type's lifetime
/// directly on the type declaration. The <see cref="HintScanner"/> picks
/// the annotations up at startup and as new assemblies load.
/// </para>
/// <para>
/// Hints are advisory: simplegc trusts the contract the developer is
/// asserting. In particular, <see cref="NoReferences"/> = <c>true</c> on a
/// type that actually contains managed reference fields produces torn
/// references during cross-region scans (the <see cref="Route.NoRefsPerm"/>
/// arena is skipped by the conservative pointer walker). The substrate
/// performs an alloc-time fallback when it can prove the allocation
/// itself contains refs (via <c>GC_ALLOC_CONTAINS_REF</c>) but cannot in
/// general police hint correctness.
/// </para>
/// <para>
/// Inheritance is intentionally disabled. Lifetime is a property of the
/// concrete type's allocation pattern, not of its supertype.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class SimpleGCHintAttribute : Attribute
{
    /// <summary>Construct a hint with the given lifetime.</summary>
    public SimpleGCHintAttribute(Lifetime lifetime)
    {
        Lifetime = lifetime;
    }

    /// <summary>The semantic lifetime class for this type.</summary>
    public Lifetime Lifetime { get; }

    /// <summary>
    /// Optional orthogonal property: instances of this type contain no
    /// managed reference fields. When combined with
    /// <see cref="Lifetime.Permanent"/>, the runtime can place the type in
    /// a region the conservative GC scan skips entirely
    /// (<see cref="Route.NoRefsPerm"/>). Has no effect for
    /// <see cref="Lifetime.Transient"/> or <see cref="Lifetime.Default"/>
    /// today; future substrates may exploit it for short-lived ref-free
    /// types as well.
    /// </summary>
    public bool NoReferences { get; init; }
}

/// <summary>
/// Semantic lifetime classifications for <see cref="SimpleGCHintAttribute"/>.
/// </summary>
/// <remarks>
/// Decoupled from <see cref="Route"/> on purpose. An LLM emitting hints in
/// generated code shouldn't have to know which simplegc regions exist;
/// it states intent, and the substrate maps it. The set deliberately omits
/// "request-scoped" until the request-arena primitive is safe under
/// asynchronous middleware (see comments in <c>kestrel-bench</c>'s
/// <c>SimpleGC.RequestBegin</c>).
/// </remarks>
public enum Lifetime : byte
{
    /// <summary>No hint; the runtime applies its default (or
    /// dynamically-learned) policy.</summary>
    Default = 0,

    /// <summary>Instances live until process exit. Examples: configuration
    /// snapshots, interned values, startup-populated caches, runtime
    /// metadata. Maps to <see cref="Route.ForcePerm"/> (or
    /// <see cref="Route.NoRefsPerm"/> when
    /// <see cref="SimpleGCHintAttribute.NoReferences"/> is set).</summary>
    Permanent = 1,

    /// <summary>Instances have a short, churning lifetime — allocated at
    /// high rate and quickly become unreachable. Examples: per-request
    /// DTOs, JSON projections, sort scratch buffers. Maps to
    /// <see cref="Route.MarkSweep"/> today; will map to a nursery region
    /// in a future build without changing the hint contract.</summary>
    Transient = 2,
}
