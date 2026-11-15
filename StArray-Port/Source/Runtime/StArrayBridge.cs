using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StArray.ModManager.Manager;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// Bounded, cached reflection into <c>StArray.ModManager</c>.
///
/// <para>
/// The manager loads each mod into its own collectible <c>AssemblyLoadContext</c>, so the mod
/// compiles against the same <c>StArray.ModManager.dll</c> instance the host is running — reference
/// identity is preserved and no duplicate-type problems arise.
/// </para>
/// <para>
/// A few helpers are declared <c>internal</c> rather than public:
/// </para>
/// <list type="bullet">
/// <item><c>Il2Cpp.Il2CppFunctions.il2cpp_object_unbox</c> — needed to read struct-valued IL2CPP
/// results (Vector3, Bounds) that <c>InvokeUnbox</c> would otherwise leave boxed.</item>
/// <item><c>Il2Cpp.Il2CppAssembly.GetClass</c> — the typed <c>Il2CppClass</c> returning overload
/// (the public <see cref="StArray.ModManager.RuntimeAbstractions.IRuntimeAssembly"/> surface
/// returns the interface, which is enough for everything except class-pointer arithmetic).</item>
/// <item><c>RuntimeAbstractions.RuntimeArray.GetIl2CppDataPtr</c> — element data of an Il2Cpp
/// array, for arrays returned through the untyped API.</item>
/// </list>
/// <para>
/// Every lookup is cached and failure is non-fatal: if an internal member is renamed by a future
/// manager release, the affected helper returns <c>null</c> and the caller degrades instead of
/// throwing during <c>OnLoad</c>.
/// </para>
/// </summary>
internal static class StArrayBridge
{
    private const BindingFlags StaticNonPublic =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<string, Delegate> Cache = new();
    private static readonly object CacheLock = new();

    private static Assembly _managerAssembly;

    internal static Assembly ManagerAssembly =>
        _managerAssembly ??= typeof(Logger).Assembly;

    /// <summary>Resolves a type from the manager assembly (null if it was renamed/removed).</summary>
    internal static Type FindType(string fullName)
    {
        try { return ManagerAssembly.GetType(fullName, throwOnError: false); }
        catch { return null; }
    }

    /// <summary>Locates a static method, tolerating overloads by parameter count.</summary>
    internal static MethodInfo FindMethod(Type owner, string name, int parameterCount = -1,
        bool isPublic = false)
    {
        if (owner == null) return null;
        try
        {
            var flags = isPublic ? StaticNonPublic : BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            return owner
                .GetMethods(flags)
                .FirstOrDefault(m => m.Name == name &&
                                     (parameterCount < 0 || m.GetParameters().Length == parameterCount));
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds (and caches) a strongly typed delegate for a static method.
    /// Typed delegates keep the per-frame cost to a single indirect call — no
    /// <c>MethodInfo.Invoke</c> boxing on the hot path.
    /// </summary>
    internal static T Delegate<T>(string cacheKey, Type owner, string name, int parameterCount = -1,
        bool isPublic = false) where T : Delegate
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(cacheKey, out var hit)) return (T)hit;
        }

        T built = null;
        try
        {
            var method = FindMethod(owner, name, parameterCount, isPublic);
            if (method != null) built = (T)System.Delegate.CreateDelegate(typeof(T), method);
            else Logger.Warn("StArrayBridge", $"'{owner?.FullName}.{name}' not found — feature degraded.");
        }
        catch (Exception ex)
        {
            Logger.Warn("StArrayBridge", $"Binding '{owner?.FullName}.{name}' failed: {ex.Message}");
        }

        lock (CacheLock) Cache[cacheKey] = built;
        return built;
    }

    internal static void Clear() { lock (CacheLock) Cache.Clear(); }
}
