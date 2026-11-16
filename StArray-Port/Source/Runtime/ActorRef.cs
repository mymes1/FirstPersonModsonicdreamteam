using System;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// Typed view over the game's <c>Hardlight.Actor</c> — the base class of every playable character.
/// Replaces the interop wrapper the desktop mod used (<c>Il2CppHardlightProject.Actor</c>) and
/// exposes the same members it relied on: <c>name</c>, <c>transform</c>, renderer enumeration,
/// <c>ActorAttachPoints</c> and <c>VisualProxy</c>.
///
/// The object is held through an <see cref="ObjectHandle"/> so the GC cannot free it underneath the
/// camera while the game still uses it.
/// </summary>
public sealed class ActorRef : IDisposable
{
    private ObjectHandle _handle;

    public ActorRef(nint ptr) => _handle = new ObjectHandle(ptr);

    public nint Ptr => _handle?.Target ?? 0;

    public bool IsValid => _handle != null && !_handle.IsDisposed && Ptr != 0;

    /// <summary>Original <c>WasCollected</c>: pointer is stale or the object has been destroyed.</summary>
    public bool WasCollected => !IsValid || Il2Cpp.ObjectGetClass(Ptr) == 0;

    public string Name
    {
        get
        {
            var p = Ptr;
            if (p == 0) return string.Empty;
            try
            {
                var s = Il2Cpp.InvokeInstance(p, "get_name", 0, null);
                return s == 0 ? string.Empty : new RuntimeString(s).ToString();
            }
            catch { return string.Empty; }
        }
    }

    public TransformRef Transform
    {
        get
        {
            var p = Ptr;
            if (p == 0) return TransformRef.None;
            try { return new TransformRef(Il2Cpp.InvokeInstance(p, "get_transform", 0, null)); }
            catch { return TransformRef.None; }
        }
    }

    public Vec3 Position => Transform.Position;

    public Vec3 Forward => Transform.Forward;

    /// <summary>Every renderer under the character (inactive included, as the original did).</summary>
    public nint[] GetRenderers()
    {
        var p = Ptr;
        if (p == 0) return Array.Empty<nint>();
        return UnityApi.GetComponentsInChildren(p, GameApi.RendererClass, true);
    }

    /// <summary>Combined renderer bounds — the original's <c>GetActorHeadTop</c> input.</summary>
    public bool TryGetCombinedBounds(out Bounds bounds)
    {
        bounds = default;

        var renderers = GetRenderers();
        if (renderers.Length == 0) return false;

        bool first = true;
        foreach (var ptr in renderers)
        {
            if (ptr == 0) continue;
            if (!new Il2CppObject(ptr).TryGetBounds(out var b)) continue;

            if (first) { bounds = b; first = false; }
            else bounds = bounds.Encapsulate(b);
        }

        return !first;
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }
}
