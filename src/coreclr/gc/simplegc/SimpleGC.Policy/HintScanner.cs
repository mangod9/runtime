using System.Reflection;

namespace SimpleGC.Policy;

/// <summary>
/// Walks loaded assemblies for types decorated with
/// <see cref="SimpleGCHintAttribute"/> and applies the corresponding
/// routing decision to simplegc via <see cref="SimpleGCInterop.SetRoute"/>.
/// Subscribes to <see cref="AppDomain.AssemblyLoad"/> so hints in
/// dynamically-loaded assemblies are picked up too.
/// </summary>
/// <remarks>
/// <para>
/// This is the "policy as code" complement to <see cref="AdaptivePolicy"/>:
/// the adaptive policy <em>discovers</em> hot types by observing
/// allocation counters; the hint scanner <em>declares</em> them through
/// type-level annotations. The two compose — a type with a hint is
/// applied at startup; types without hints are still subject to the
/// adaptive policy's count-based promotion.
/// </para>
/// <para>
/// Pay-for-play: the scanner does nothing unless <see cref="Start"/> is
/// called. Once started, the cost is O(types) walks during assembly
/// loads; steady-state cost is zero.
/// </para>
/// </remarks>
public static class HintScanner
{
    private static readonly object s_lock = new();
    private static readonly HashSet<Assembly> s_seenAssemblies = new();
    private static int s_started;
    private static int s_appliedTotal;
    private static int s_skippedOpenGeneric;

    /// <summary>Total number of <see cref="SimpleGCInterop.SetRoute"/>
    /// invocations that returned success since <see cref="Start"/>.</summary>
    public static int AppliedTotal => Volatile.Read(ref s_appliedTotal);

    /// <summary>Number of types skipped because they were open generic
    /// definitions or contained unbound generic parameters. Closed
    /// instantiations are routed independently; annotate those if needed.</summary>
    public static int SkippedOpenGeneric => Volatile.Read(ref s_skippedOpenGeneric);

    /// <summary>
    /// Enable hint-driven routing. Subscribes to <see cref="AppDomain.AssemblyLoad"/>
    /// FIRST (so no load is missed during the initial sweep), then scans
    /// every currently-loaded assembly. Idempotent: calling more than once
    /// is a no-op after the first.
    /// </summary>
    /// <returns>The number of hints successfully applied during the
    /// initial scan.</returns>
    public static int Start()
    {
        if (Interlocked.CompareExchange(ref s_started, 1, 0) != 0)
        {
            return AppliedTotal;
        }

        // Hints can't take effect unless per-MT routing is consulted on
        // chunk refill, and the table needs MT entries to write into.
        SimpleGCInterop.EnableMtTracking(1);
        SimpleGCInterop.EnableAutoRouteDefault(1);

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        int initial = 0;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            initial += ScanAssembly(asm);
        }

        return initial;
    }

    /// <summary>
    /// Scan a specific assembly for <see cref="SimpleGCHintAttribute"/>-decorated
    /// types and apply their hints. Safe to call directly even before
    /// <see cref="Start"/> (does not enable auto-routing on its own).
    /// Idempotent per assembly.
    /// </summary>
    /// <returns>The number of hints applied from this assembly.</returns>
    public static int ScanAssembly(Assembly assembly)
    {
        if (assembly is null)
        {
            return 0;
        }

        lock (s_lock)
        {
            if (!s_seenAssemblies.Add(assembly))
            {
                return 0;
            }
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch
        {
            return 0;
        }

        int applied = 0;
        int skippedOpenGeneric = 0;
        foreach (Type? t in types)
        {
            if (t is null)
            {
                continue;
            }

            SimpleGCHintAttribute? attr;
            try
            {
                attr = t.GetCustomAttribute<SimpleGCHintAttribute>(inherit: false);
            }
            catch
            {
                continue;
            }

            if (attr is null)
            {
                continue;
            }

            if (t.IsGenericTypeDefinition || t.ContainsGenericParameters)
            {
                skippedOpenGeneric++;
                continue;
            }

            byte route = MapHintToRoute(attr);
            if (route == (byte)Route.Default)
            {
                continue;
            }

            ulong mtToken;
            try
            {
                mtToken = (ulong)t.TypeHandle.Value.ToInt64();
            }
            catch
            {
                continue;
            }

            if (SimpleGCInterop.SetRoute(mtToken, route) != 0)
            {
                applied++;
            }
        }

        if (applied != 0)
        {
            Interlocked.Add(ref s_appliedTotal, applied);
        }
        if (skippedOpenGeneric != 0)
        {
            Interlocked.Add(ref s_skippedOpenGeneric, skippedOpenGeneric);
        }
        return applied;
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        ScanAssembly(args.LoadedAssembly);
    }

    private static byte MapHintToRoute(SimpleGCHintAttribute attr) => attr.Lifetime switch
    {
        Lifetime.Permanent => attr.NoReferences ? (byte)Route.NoRefsPerm : (byte)Route.ForcePerm,
        Lifetime.Transient => (byte)Route.MarkSweep,
        _ => (byte)Route.Default,
    };
}
