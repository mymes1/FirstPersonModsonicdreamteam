using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// Transcribed <c>GameAssembly</c> prototype (Android: <c>libil2cpp.so</c>).
///
/// <para>
/// This is the only IL2CPP export StArray's public surface does not already wrap, and the only
/// place in this port that touches native code directly. Everything else goes through
/// <c>Il2CppFunctions</c> / <see cref="RuntimeObject"/> / <see cref="RuntimeManager"/>.
/// </para>
/// <para>
/// <b>Why this is not <c>[NativeHook]</c>:</b> the original mod's Harmony patch targeted a
/// <i>managed</i> method (<c>CinemachineBrain.ManualUpdate</c>). Hooking that by name would mean
/// un-mangling an Il2Cpp icall stub from the build's symbol table and pinning the mod to one build
/// hash. Patching the one runtime export every managed call funnels through suppresses the same
/// behaviour while staying version independent — see <c>Reports/UnsupportedFeatures.md</c>.
/// </para>
/// <para>
/// The library name is substituted per platform at build time (Android ships
/// <c>libil2cpp.so</c>). If it cannot be resolved, one error is logged and the port falls back to
/// the managed-only call path instead of crashing the game.
/// </para>
/// </summary>
internal static unsafe class Il2CppNative
{
    private const string Library = "libil2cpp.so";

    [DllImport(Library, EntryPoint = "il2cpp_runtime_invoke",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtRuntimeInvoke(nint method, nint obj, nint* args, ref nint exception);

    [DllImport(Library, EntryPoint = "il2cpp_object_get_class",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtObjectGetClass(nint obj);

    [DllImport(Library, EntryPoint = "il2cpp_class_get_method_from_name",
        CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern nint ExtClassGetMethodFromName(nint klass, string name, int paramCount);

    [DllImport(Library, EntryPoint = "il2cpp_class_get_methods",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtClassGetMethods(nint klass, ref nint iterator);

    [DllImport(Library, EntryPoint = "il2cpp_method_get_name",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtMethodGetName(nint method);

    [DllImport(Library, EntryPoint = "il2cpp_method_get_param_count",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ExtMethodGetParamCount(nint method);

    [DllImport(Library, EntryPoint = "il2cpp_method_get_param",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtMethodGetParam(nint method, uint index);

    [DllImport(Library, EntryPoint = "il2cpp_type_get_name",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtTypeGetName(nint type);

    [DllImport(Library, EntryPoint = "il2cpp_class_get_parent",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ExtClassGetParent(nint klass);

    [DllImport(Library, EntryPoint = "il2cpp_array_length",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ExtArrayLength(nint array);

    [DllImport(Library, EntryPoint = "il2cpp_class_is_assignable_from",
        CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ExtClassIsAssignableFrom(nint klass, nint other);

    private static bool _failureReported;

    internal static nint ObjectGetClass(nint obj)
    {
        if (obj == 0) return 0;
        try { return ExtObjectGetClass(obj); }
        catch (Exception ex) { ReportUnavailable(ex); return 0; }
    }

    internal static nint RuntimeInvokeRaw(nint method, nint obj, nint* args, out nint exception)
    {
        exception = 0;
        try
        {
            return ExtRuntimeInvoke(method, obj, args, ref exception);
        }
        catch (Exception ex)
        {
            ReportUnavailable(ex);
            exception = 0;
            return 0;
        }
    }

    internal static nint GetMethod(nint klass, string name, int paramCount)
    {
        try { return ExtClassGetMethodFromName(klass, name, paramCount); }
        catch (Exception ex) { ReportUnavailable(ex); return 0; }
    }

    internal static nint GetNextMethod(nint klass, ref nint iterator)
    {
        try { return ExtClassGetMethods(klass, ref iterator); }
        catch (Exception ex) { ReportUnavailable(ex); return 0; }
    }

    internal static string GetMethodName(nint method)
    {
        try { return Marshal.PtrToStringAnsi(ExtMethodGetName(method)) ?? string.Empty; }
        catch { return string.Empty; }
    }

    internal static int GetParamCount(nint method)
    {
        try { return (int)ExtMethodGetParamCount(method); }
        catch { return -1; }
    }

    internal static string GetParamTypeName(nint method, uint index)
    {
        try
        {
            var t = ExtMethodGetParam(method, index);
            return t == 0 ? string.Empty : Marshal.PtrToStringAnsi(ExtTypeGetName(t)) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    internal static nint GetParent(nint klass)
    {
        try { return ExtClassGetParent(klass); }
        catch { return 0; }
    }

    internal static int GetArrayLength(nint array)
    {
        try { return (int)ExtArrayLength(array); }
        catch { return 0; }
    }

    internal static bool IsAssignableFrom(nint klass, nint other)
    {
        try { return ExtClassIsAssignableFrom(klass, other); }
        catch { return false; }
    }

    private static void ReportUnavailable(Exception ex)
    {
        if (_failureReported) return;
        _failureReported = true;
        Logger.Error("Il2CppNative",
            $"Native IL2CPP entry points unavailable ({ex.GetType().Name}: {ex.Message}). " +
            "Verify the imported library name matches this platform.");
    }
}

/// <summary>
/// Method-level cache and invocation helpers around the IL2CPP runtime.
///
/// IL2CPP properties are ordinary <c>get_X</c> / <c>set_X</c> methods; by-value struct arguments
/// must be passed as <i>pointers</i> to <c>il2cpp_runtime_invoke</c> while reference arguments are
/// passed as plain object pointers. Both are handled here so the camera code reads like the
/// original Unity code.
/// </summary>
internal static class Il2Cpp
{
    private static readonly Dictionary<(nint, string, int), nint> MethodCache = new();
    private static readonly Dictionary<string, nint> ClassCache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    /// <summary>Roots an object for the lifetime of the handle.</summary>
    internal static ObjectHandle Root(nint ptr) => new(ptr);

    internal static nint ObjectGetClass(nint obj) => obj == 0 ? 0 : Il2CppNative.ObjectGetClass(obj);

    // ── Class / method resolution ──

    internal static nint GetClass(string assembly, string ns, string name)
    {
        var key = $"{assembly}|{ns}|{name}";
        lock (CacheLock)
        {
            if (ClassCache.TryGetValue(key, out var hit)) return hit;
        }

        nint klass = 0;
        try
        {
            var domain = RuntimeManager.GetDomain();
            var asm = domain?.OpenAssembly(assembly);
            klass = asm?.GetClass(ns, name)?.Ptr ?? 0;
        }
        catch (Exception ex)
        {
            Logger.Warn("Il2Cpp", $"GetClass {ns}.{name} in {assembly}: {ex.Message}");
        }

        lock (CacheLock) ClassCache[key] = klass;
        return klass;
    }

    /// <summary>Drops every cached class/method pointer (scene reload, runtime restart).</summary>
    internal static void InvalidateCaches()
    {
        lock (CacheLock)
        {
            ClassCache.Clear();
            MethodCache.Clear();
        }
    }

    /// <summary>Method by name + parameter count on a class (no inheritance walk).</summary>
    internal static nint GetMethod(nint klass, string name, int paramCount)
    {
        if (klass == 0) return 0;

        var key = (klass, name, paramCount);
        lock (CacheLock)
        {
            if (MethodCache.TryGetValue(key, out var hit)) return hit;
        }

        var method = Il2CppNative.GetMethod(klass, name, paramCount);
        lock (CacheLock) MethodCache[key] = method;
        return method;
    }

    /// <summary>
    /// Method lookup walking up the base-class chain.
    /// <c>il2cpp_class_get_method_from_name</c> only inspects the class itself, while Unity's API
    /// lives on base types (<c>Component</c>, <c>Transform</c>, <c>Behaviour</c> …).
    /// </summary>
    internal static nint FindMethodInHierarchy(nint klass, string name, Func<nint, bool> predicate,
        string[] preferParamTypes = null)
    {
        if (klass == 0) return 0;

        for (var current = klass; current != 0; current = Il2CppNative.GetParent(current))
        {
            nint fallback = 0;
            nint iter = 0;
            nint m;
            while ((m = Il2CppNative.GetNextMethod(current, ref iter)) != 0)
            {
                if (Il2CppNative.GetMethodName(m) != name) continue;
                if (!predicate(m)) continue;

                if (preferParamTypes == null) return m;

                var count = Il2CppNative.GetParamCount(m);
                if (count != preferParamTypes.Length) continue;

                var matches = true;
                for (uint i = 0; i < preferParamTypes.Length; i++)
                {
                    if (Il2CppNative.GetParamTypeName(m, i) != preferParamTypes[i]) { matches = false; break; }
                }

                if (matches) return m;
                if (fallback == 0) fallback = m;
            }

            if (fallback != 0) return fallback;
        }

        return 0;
    }

    /// <summary>First overload of <paramref name="name"/> whose parameter count matches.</summary>
    internal static nint FindMethodInHierarchy(nint klass, string name, int paramCount)
        => FindMethodInHierarchy(klass, name, m => Il2CppNative.GetParamCount(m) == paramCount);

    /// <summary>Resolves a game method from a named assembly.</summary>
    internal static nint FindMethod(string assembly, string ns, string className, string method,
        int paramCount)
    {
        var klass = GetClass(assembly, ns, className);
        return FindMethodInHierarchy(klass, method, paramCount);
    }

    // ── Invocation ──

    internal static nint InvokeInstance(nint obj, string name, int paramCount, nint[] args)
    {
        if (obj == 0) return 0;
        var method = FindMethodInHierarchy(ObjectGetClass(obj), name, paramCount);
        return InvokeInstance(obj, method, args);
    }

    internal static nint InvokeInstance(nint obj, nint method, nint[] args)
    {
        if (obj == 0 || method == 0) return 0;
        var ret = InvokeCore(method, obj, args, out var exception);
        if (exception != 0) throw BuildInvocationException(method, exception);
        return ret;
    }

    internal static nint InvokeStatic(nint method, nint[] args)
    {
        if (method == 0) return 0;
        var ret = InvokeCore(method, 0, args, out var exception);
        if (exception != 0) throw BuildInvocationException(method, exception);
        return ret;
    }

    internal static double InvokeStaticDouble(nint method, nint[] args)
        => Unbox<double>(InvokeStatic(method, args));

    internal static float InvokeStaticFloat(nint method, nint[] args)
        => Unbox<float>(InvokeStatic(method, args));

    internal static bool InvokeStaticBool(nint method, nint[] args)
    {
        var ret = InvokeStatic(method, args);
        return ret != 0 && Unbox<bool>(ret);
    }

    internal static int InvokeStaticInt(nint method, nint[] args)
        => Unbox<int>(InvokeStatic(method, args));

    internal static T InvokeInstanceUnbox<T>(nint obj, string name, int paramCount, nint[] args)
        where T : unmanaged
    {
        if (obj == 0) return default;
        var method = FindMethodInHierarchy(ObjectGetClass(obj), name, paramCount);
        return InvokeInstanceUnbox<T>(obj, method, args);
    }

    internal static T InvokeInstanceUnbox<T>(nint obj, nint method, nint[] args)
        where T : unmanaged
    {
        if (obj == 0 || method == 0) return default;
        var ret = InvokeCore(method, obj, args, out var exception);
        if (exception != 0) throw BuildInvocationException(method, exception);
        return Unbox<T>(ret);
    }

    internal static T InvokeStaticUnbox<T>(nint method, nint[] args) where T : unmanaged
    {
        if (method == 0) return default;
        var ret = InvokeCore(method, 0, args, out var exception);
        if (exception != 0) throw BuildInvocationException(method, exception);
        return Unbox<T>(ret);
    }

    /// <summary>
    /// Address of a by-value argument for <c>il2cpp_runtime_invoke</c>.
    /// The caller must keep the variable alive until the call returns — pass a local, never a
    /// temporary, or the GC may move it out from under the runtime.
    /// </summary>
    internal static unsafe nint Addr<T>(ref T value) where T : unmanaged
    {
        fixed (T* p = &value) return (nint)p;
    }

    /// <summary>Boxed return value → struct value (<c>il2cpp_object_unbox</c>).</summary>
    internal static unsafe T Unbox<T>(nint boxed) where T : unmanaged
    {
        if (boxed == 0) return default;
        var raw = ObjectUnbox(boxed);
        return raw == 0 ? default : *(T*)raw;
    }

    /// <summary>
    /// <c>il2cpp_object_unbox</c>. The export lives in <c>Il2CppFunctions</c> as an internal member,
    /// so it is reached through <see cref="StArrayBridge"/> with a cached typed delegate.
    /// </summary>
    internal static nint ObjectUnbox(nint boxed)
    {
        if (boxed == 0) return 0;

        UnboxFn fn = StArrayBridge.Delegate<UnboxFn>(
            "il2cpp_object_unbox",
            StArrayBridge.FindType("StArray.ModManager.Il2Cpp.Il2CppFunctions"),
            "il2cpp_object_unbox",
            1);

        return fn?.Invoke(boxed) ?? 0;
    }

    private delegate nint UnboxFn(nint obj);

    private static unsafe nint InvokeCore(nint method, nint obj, nint[] args, out nint exception)
    {
        fixed (nint* p = args)
            return Il2CppNative.RuntimeInvokeRaw(method, obj, p, out exception);
    }

    private static Exception BuildInvocationException(nint method, nint exception)
    {
        var name = Il2CppNative.GetMethodName(method);
        return new InvalidOperationException($"IL2CPP invocation of '{name}' threw.");
    }

    // ── Arrays ──

    /// <summary>
    /// Object-reference array → element pointers.
    /// An Il2Cpp array's element data starts 2 pointer-sized words past the unboxed address
    /// (bounds + max_length), which is what <c>RuntimeArray</c> uses internally as well.
    /// </summary>
    internal static unsafe nint[] ToObjectPointers(nint array)
    {
        if (array == 0) return Array.Empty<nint>();

        var length = Il2CppNative.GetArrayLength(array);
        if (length <= 0) return Array.Empty<nint>();

        var unboxed = ObjectUnbox(array);
        if (unboxed == 0) return Array.Empty<nint>();

        // Il2Cpp array layout: object header, then bounds, then max_length, then element data.
        var data = unboxed + nint.Size * 2;
        var result = new nint[length];
        for (int i = 0; i < length; i++)
            result[i] = *(nint*)(data + i * nint.Size);
        return result;
    }

    internal static bool IsAssignableTo(nint klass, nint baseKlass)
        => klass != 0 && baseKlass != 0 && Il2CppNative.IsAssignableFrom(baseKlass, klass);
}
