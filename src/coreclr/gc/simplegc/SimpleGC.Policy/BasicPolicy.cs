namespace SimpleGC.Policy;

/// <summary>
/// A simple, hand-coded policy that promotes long-lived MTs to the
/// mark-sweep region. Designed as the M1d default — easy to reason about
/// and easy to compare against an LLM-generated alternative.
/// </summary>
/// <remarks>
/// <para><b>Heuristic.</b> A type's <em>future</em> allocations are routed
/// to mark-sweep when:</para>
/// <list type="bullet">
///   <item><description><see cref="RoutingEntry.AgeCollections"/> ≥ <see cref="AgeThreshold"/>:
///     the MT has been observed surviving across that many collections.</description></item>
///   <item><description><see cref="RoutingEntry.SurvivedCount"/> &gt; 0: it actually
///     has live instances now.</description></item>
///   <item><description><see cref="RoutingEntry.SurvivedBytes"/> ≥ <see cref="MinSurvivedBytes"/>:
///     enough survives to be worth tracking.</description></item>
/// </list>
/// <para>Once a route is set, the decision is sticky: later snapshots leave
/// it alone. There is no demotion — instances allocated under the old route
/// stay where they are until they die.</para>
/// </remarks>
public sealed class BasicPolicy : IPolicy
{
    /// <summary>Minimum surviving collections before a type is promoted.</summary>
    public uint AgeThreshold { get; init; } = 2;

    /// <summary>Minimum surviving bytes before a type is promoted.</summary>
    public ulong MinSurvivedBytes { get; init; } = 256;

    /// <summary>Route promoted MTs land in. Defaults to <see cref="Route.MarkSweep"/>.</summary>
    public Route PromoteTo { get; init; } = Route.MarkSweep;

    public void OnPostCollection(IPolicyContext ctx)
    {
        var snap = ctx.Snapshot;
        for (int i = 0; i < snap.Length; i++)
        {
            ref readonly RoutingEntry e = ref snap[i];

            if (e.CurrentRoute != (byte)Route.Default)
            {
                continue; // already routed; leave alone
            }

            if (e.AgeCollections >= AgeThreshold &&
                e.SurvivedCount > 0 &&
                e.SurvivedBytes >= MinSurvivedBytes)
            {
                ctx.SetRoute(e.MtToken, PromoteTo);
            }
        }
    }
}
