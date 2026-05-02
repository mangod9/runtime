using System.Runtime.InteropServices;

namespace SimpleGC.Policy;

/// <summary>
/// Mirror of the native <c>SimpleGCRoutingEntry</c> ABI struct. One row per
/// <em>MethodTable</em> tracked by simplegc, exposed through
/// <c>simplegc_get_routing_snapshot</c>.
/// </summary>
/// <remarks>
/// The layout is fixed at 56 bytes and is part of the public ABI (version
/// <c>kRoutingPolicyAbiVersion</c>). Do not reorder, resize, or insert
/// fields without bumping that version on the native side.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct RoutingEntry
{
    /// <summary>The MethodTable* address (cast to ulong) for the type.</summary>
    public ulong MtToken;

    /// <summary>Total objects of this type observed across all chunks since startup.</summary>
    public ulong AllocCount;

    /// <summary>Total bytes attributed to this type since startup.</summary>
    public ulong AllocBytes;

    /// <summary>Total objects of this type that survived the most recent mark-sweep walks.</summary>
    public ulong SurvivedCount;

    /// <summary>Total bytes attributed to objects of this type that survived recent walks.</summary>
    public ulong SurvivedBytes;

    /// <summary>Number of collections during which this type was observed surviving.</summary>
    public uint  AgeCollections;

    /// <summary>Smallest object size observed for this type, in bytes.</summary>
    public uint  MinSize;

    /// <summary>Largest object size observed for this type, in bytes.</summary>
    public uint  MaxSize;

    /// <summary>Current routing decision (a <see cref="Route"/> value cast to byte).</summary>
    public byte  CurrentRoute;

    public byte   Pad0;
    public ushort Pad1;
}
