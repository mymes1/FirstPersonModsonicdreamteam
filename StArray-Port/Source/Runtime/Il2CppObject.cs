using System;
using System.Runtime.InteropServices;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// Global objects the mod holds on to across frames (the local player, the camera, renderers …).
///
/// StArray hands out raw <c>il2cpp</c> object pointers. A raw pointer is <b>not</b> a GC root:
/// if the game drops its last managed reference the object can be collected while we still hold
/// the address, and the next frame writes into freed memory. MelonLoader's Il2CppInterop kept
/// objects alive for us; here that has to be done explicitly.
///
/// <c>il2cpp_gchandle_new(obj, pinned: false)</c> registers a strong root and we resolve the
/// current address with <c>il2cpp_gchandle_get_target</c>. Once Unity destroys the object the
/// handle target reads back as 0, which is a much more reliable liveness test than probing memory.
/// </summary>
public sealed class ObjectHandle : IDisposable
{
    private nint _handle;
    private bool _disposed;

    public bool IsDisposed => _disposed;

    public ObjectHandle(nint ptr)
    {
        if (ptr != 0)
            _handle = Il2Cpp.GCHandleNew(ptr);
    }

    /// <summary>
    /// Current address of the rooted object, or 0 once it has been destroyed / collected.
    /// </summary>
    public nint Target
    {
        get
        {
            if (_disposed || _handle == 0) return 0;
            return Il2Cpp.GCHandleGetTarget(_handle);
        }
    }

    public bool IsAlive => Target != 0;

    /// <summary>Drops the GC root. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != 0)
        {
            Il2Cpp.GCHandleFree(_handle);
            _handle = 0;
        }
    }

    ~ObjectHandle() => Dispose();
}

/// <summary>
/// Thin wrapper around a single IL2CPP object pointer, replacing the members MelonLoader's
/// Il2CppInterop wrappers exposed (<c>WasCollected</c>, <c>name</c>, <c>transform</c>, …).
/// </summary>
public class Il2CppObject
{
    /// <summary>Raw IL2CPP object pointer (0 when invalid).</summary>
    public nint Ptr { get; internal set; }

    public Il2CppObject(nint ptr) => Ptr = ptr;

    public bool IsValid => Ptr != 0;

    /// <summary>Replacement for Il2CppInterop's <c>WasCollected</c>: pointer is stale or freed.</summary>
    public bool WasCollected => Ptr == 0 || Il2Cpp.ObjectGetClass(Ptr) == 0;

    /// <summary>Unity <c>Object.name</c>.</summary>
    public string Name
    {
        get
        {
            if (Ptr == 0) return string.Empty;
            try
            {
                var strPtr = Il2Cpp.InvokeInstance(Ptr, "get_name", 0, null);
                return strPtr == 0 ? string.Empty : new RuntimeString(strPtr).ToString();
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Unity <c>Component.transform</c> / <c>GameObject.transform</c>.</summary>
    public TransformRef Transform
    {
        get
        {
            if (Ptr == 0) return TransformRef.None;
            try { return new TransformRef(Il2Cpp.InvokeInstance(Ptr, "get_transform", 0, null)); }
            catch { return TransformRef.None; }
        }
    }

    /// <summary>Unity <c>Component.gameObject</c>.</summary>
    public nint GameObject
    {
        get
        {
            if (Ptr == 0) return 0;
            try { return Il2Cpp.InvokeInstance(Ptr, "get_gameObject", 0, null); }
            catch { return 0; }
        }
    }

    /// <summary>Unity <c>Behaviour.enabled</c>.</summary>
    public bool Enabled
    {
        get
        {
            if (Ptr == 0) return false;
            try { return Il2Cpp.InvokeInstanceUnbox<bool>(Ptr, "get_enabled", 0, null); }
            catch { return false; }
        }
        set
        {
            if (Ptr == 0) return;
            try
            {
                var arg = value ? 1 : 0;
                Il2Cpp.InvokeInstance(Ptr, "set_enabled", 1, new[] { Il2Cpp.Addr(ref arg) });
            }
            catch { }
        }
    }

    /// <summary>Reads a <c>UnityEngine.Bounds</c> valued property (Renderer / Collider).</summary>
    public bool TryGetBounds(out Bounds bounds)
    {
        bounds = default;
        if (Ptr == 0) return false;
        try
        {
            bounds = Il2Cpp.InvokeInstanceUnbox<Bounds>(Ptr, "get_bounds", 0, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override string ToString() => $"{GetType().Name}(0x{Ptr:X})";
}

/// <summary>Cached wrapper for a <c>UnityEngine.Transform</c>.</summary>
public readonly struct TransformRef
{
    public readonly nint Ptr;

    public TransformRef(nint ptr) => Ptr = ptr;

    public static readonly TransformRef None = new(0);

    public bool IsValid => Ptr != 0;

    public Vec3 Position
    {
        get
        {
            if (Ptr == 0) return Vec3.Zero;
            try { return Il2Cpp.InvokeInstanceUnbox<Vec3>(Ptr, "get_position", 0, null); }
            catch { return Vec3.Zero; }
        }
    }

    public Vec3 Forward
    {
        get
        {
            if (Ptr == 0) return Vec3.Forward;
            try { return Il2Cpp.InvokeInstanceUnbox<Vec3>(Ptr, "get_forward", 0, null); }
            catch { return Vec3.Forward; }
        }
    }

    public string Name
    {
        get
        {
            if (Ptr == 0) return string.Empty;
            try
            {
                var strPtr = Il2Cpp.InvokeInstance(Ptr, "get_name", 0, null);
                return strPtr == 0 ? string.Empty : new RuntimeString(strPtr).ToString();
            }
            catch { return string.Empty; }
        }
    }

    /// <summary>Unity <c>Transform.SetPositionAndRotation(Vector3, Quaternion)</c>.</summary>
    public void SetPositionAndRotation(Vec3 position, Quat rotation)
    {
        if (Ptr == 0) return;
        try
        {
            var method = Il2Cpp.FindMethodInHierarchy(
                Il2Cpp.ObjectGetClass(Ptr),
                "SetPositionAndRotation",
                m => Il2CppNative.GetParamCount(m) == 2,
                new[] { "UnityEngine.Vector3", "UnityEngine.Quaternion" });

            if (method == 0) return;

            // il2cpp_runtime_invoke takes pointers to by-value arguments.
            var pos = position;
            var rot = rotation;
            Il2Cpp.InvokeInstance(Ptr, method, new[] { Il2Cpp.Addr(ref pos), Il2Cpp.Addr(ref rot) });
        }
        catch { }
    }
}
