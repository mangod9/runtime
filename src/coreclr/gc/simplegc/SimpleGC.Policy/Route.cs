namespace SimpleGC.Policy;

/// <summary>
/// Routing decision values exposed by the simplegc runtime. Mirror of the
/// native kRoute* constants. Each value selects a distinct allocation
/// region in simplegc:
///
/// <list type="bullet">
///   <item><description><see cref="Default"/> — no override; the runtime applies its
///     normal priority order (request bracket if active, else perm).</description></item>
///   <item><description><see cref="ForcePerm"/> — bump-allocate in the never-collected
///     "perm" region. Best for objects that live until process exit.</description></item>
///   <item><description><see cref="ForceReq"/> — bump-allocate in the request arena
///     and rewind on <c>simplegc_request_end</c>. Best for short-lived
///     scoped objects.</description></item>
///   <item><description><see cref="MarkSweep"/> — free-list-backed mark-sweep region.
///     Best for objects with a mix of long lifetimes and reclaimable churn.</description></item>
/// </list>
/// </summary>
public enum Route : byte
{
    Default   = 0,
    ForcePerm = 1,
    ForceReq  = 2,
    MarkSweep = 3,
}
