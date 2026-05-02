using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimpleGC.Policy;

/// <summary>
/// Hosts a managed <see cref="IPolicy"/> against the simplegc runtime.
/// Owns the snapshot buffer, registers the native routing callback, and
/// exposes the resulting per-MT decisions back to workload code.
/// </summary>
/// <remarks>
/// Typical usage:
/// <code>
/// using var host = PolicyHost.Start(new BasicPolicy());
/// // ...workload runs; policy is invoked after each mark-sweep collection...
/// var route = host.GetRouteFor&lt;MyType&gt;();
/// </code>
///
/// <para><b>Thread safety.</b> The native callback is invoked on the GC
/// thread post-RestartEE; workload threads may concurrently call
/// <see cref="GetRouteFor{T}"/> / <see cref="GetRouteFor(ulong)"/>. The
/// internal decisions store is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.</para>
///
/// <para><b>Allocation hygiene.</b> The native runtime invokes the policy
/// callback in normal cooperative mode where allocations are legal but
/// undesirable — they would land in whatever region the active default
/// route dictates. To keep policy bookkeeping out of mark-sweep, the host
/// brackets the callback in <c>SetThreadRoute(ForcePerm)</c>.</para>
///
/// <para><b>Singleton.</b> Only one host can be active at a time — the
/// native runtime exposes a single callback slot. <see cref="Start"/>
/// throws if a host is already running.</para>
/// </remarks>
public sealed class PolicyHost : IDisposable, IPolicyContext
{
    /// <summary>Routing-policy ABI version expected by the runtime.</summary>
    private const uint RoutingPolicyAbiVersion = 1;

    /// <summary>Maximum number of MT rows pulled per snapshot. Sized to
    /// match the native table capacity (kMtTableCapacity = 4096).</summary>
    public const int SnapshotCapacity = 4096;

    private readonly IPolicy _policy;
    private readonly RoutingEntry[] _buffer = new RoutingEntry[SnapshotCapacity];
    private readonly ConcurrentDictionary<ulong, Route> _decisions = new();
    private int _snapshotCount;
    private int _decisionCount;
    private int _invocationCount;
    private bool _disposed;

    private static PolicyHost? s_singleton;
    private static readonly object s_singletonLock = new();

    private PolicyHost(IPolicy policy)
    {
        _policy = policy;
    }

    /// <summary>Start a singleton policy host. Throws if one is already
    /// running. Disposing the returned host stops it.</summary>
    public static PolicyHost Start(IPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (s_singletonLock)
        {
            if (s_singleton is not null)
            {
                throw new InvalidOperationException("A PolicyHost is already running. Dispose it first.");
            }

            // Bracket the registration calls so any allocation pulled in by
            // the marshalling / interop machinery lands in perm, not whichever
            // region the default route happens to point at.
            int saved = SimpleGCInterop.GetThreadRoute();
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            try
            {
                var host = new PolicyHost(policy);
                s_singleton = host;
                SimpleGCInterop.EnableMtTracking(1);

                unsafe
                {
                    delegate* unmanaged<void> fn = &NativeCallbackThunk;
                    uint rv = SimpleGCInterop.RegisterRoutingPolicy(RoutingPolicyAbiVersion, (IntPtr)fn);
                    if (rv != 1)
                    {
                        s_singleton = null;
                        throw new InvalidOperationException(
                            $"simplegc_register_routing_policy returned {rv}.");
                    }
                }

                return host;
            }
            finally
            {
                SimpleGCInterop.SetThreadRoute(saved);
            }
        }
    }

    public void Dispose()
    {
        lock (s_singletonLock)
        {
            if (_disposed) return;
            _disposed = true;
            int saved = SimpleGCInterop.GetThreadRoute();
            SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
            try
            {
                SimpleGCInterop.RegisterRoutingPolicy(RoutingPolicyAbiVersion, IntPtr.Zero);
                if (ReferenceEquals(s_singleton, this)) s_singleton = null;
            }
            finally
            {
                SimpleGCInterop.SetThreadRoute(saved);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void NativeCallbackThunk()
    {
        var host = s_singleton;
        host?.OnNativeCallback();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnNativeCallback()
    {
        // Bracket policy work so anything we allocate lands in perm.
        int saved = SimpleGCInterop.GetThreadRoute();
        SimpleGCInterop.SetThreadRoute((int)Route.ForcePerm);
        try
        {
            _invocationCount++;
            uint emitted;
            unsafe
            {
                fixed (RoutingEntry* p = _buffer)
                {
                    emitted = SimpleGCInterop.GetRoutingSnapshot(p, (uint)_buffer.Length);
                }
            }
            int valid = (int)Math.Min(emitted, (uint)_buffer.Length);
            _snapshotCount = valid;

            _policy.OnPostCollection(this);
        }
        catch
        {
            // Swallow. A buggy policy must never destabilize the runtime;
            // the worst-case outcome is stale routing for one tick.
        }
        finally
        {
            SimpleGCInterop.SetThreadRoute(saved);
        }
    }

    // ---- IPolicyContext ------------------------------------------------

    int IPolicyContext.SnapshotCount => _snapshotCount;
    ReadOnlySpan<RoutingEntry> IPolicyContext.Snapshot => _buffer.AsSpan(0, _snapshotCount);
    int IPolicyContext.Decisions => _decisionCount;
    int IPolicyContext.Invocations => _invocationCount;

    bool IPolicyContext.SetRoute(ulong mtToken, Route route)
    {
        if (SimpleGCInterop.SetRoute(mtToken, (byte)route) != 1)
        {
            return false;
        }
        _decisions[mtToken] = route;
        _decisionCount++;
        return true;
    }

    // ---- Workload-side query -----------------------------------------

    /// <summary>Look up the route the policy has recommended for type
    /// <typeparamref name="T"/>. Returns <see cref="Route.Default"/> if the
    /// policy has not yet decided.</summary>
    /// <remarks>
    /// <para><b>Exact runtime types only.</b> The MT token is read from
    /// <c>typeof(T).TypeHandle.Value</c>, which returns the MethodTable
    /// pointer for the loaded type. Polymorphic / open-generic / interface
    /// lookups are <em>not</em> supported.</para>
    /// </remarks>
    public Route GetRouteFor<T>() where T : class
    {
        ulong mt = (ulong)typeof(T).TypeHandle.Value.ToInt64();
        return GetRouteFor(mt);
    }

    /// <summary>Look up the route the policy has recommended for an MT
    /// token. Returns <see cref="Route.Default"/> if no decision has been
    /// recorded.</summary>
    public Route GetRouteFor(ulong mtToken)
    {
        return _decisions.TryGetValue(mtToken, out var r) ? r : Route.Default;
    }

    /// <summary>Number of policy invocations observed.</summary>
    public int Invocations => _invocationCount;

    /// <summary>Cumulative count of routing decisions the policy has made.</summary>
    public int Decisions => _decisionCount;

    /// <summary>Snapshot of all current per-MT decisions.</summary>
    public IReadOnlyDictionary<ulong, Route> CurrentDecisions => _decisions;
}
