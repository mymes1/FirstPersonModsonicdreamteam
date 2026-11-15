using System;
using FirstPersonCamera.Port.Camera;
using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Hooks;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

namespace FirstPersonCamera.Port.Hooks;

/// <summary>
/// Port of the original Harmony prefix:
/// <code>
/// [HarmonyPatch(typeof(CinemachineBrain), nameof(CinemachineBrain.ManualUpdate))]
/// static bool Prefix() => !(fpActive &amp;&amp; camera != null &amp;&amp; actor != null);
/// </code>
/// Cinemachine must stop overwriting the transform we just wrote.
///
/// <para>
/// <b>How it is ported.</b> An <c>[UnmanagedHook]</c> on <c>CinemachineBrain.ManualUpdate</c> would
/// have to declare the exact native signature, which changes with the Cinemachine/Unity version and
/// — on ARM64 — again with struct return handling. Suppressing the same work through the public
/// behaviour API is both exact and build independent, because <c>CinemachineBrain.Update()</c>
/// bails out immediately when the behaviour is disabled, so <c>ManualUpdate</c> is never reached:
/// </para>
/// <list type="number">
/// <item><b>Primary (managed):</b> <c>CinemachineBrain.enabled = false</c> while the first-person
/// view is active — this is what the prefix achieved.</item>
/// <item><b>Secondary (native):</b> if the build keeps <c>ManualUpdate</c> as a dynamic symbol it is
/// also detoured with <see cref="NativeHookAttribute"/>, and the trampoline is simply not called
/// while first-person is active — literally "prefix returns false". If the symbol is absent the
/// installer says so and the managed equivalent carries the behaviour.</item>
/// </list>
///
/// The prefix predicate is preserved verbatim in <see cref="ShouldAllowOriginal"/>.
/// </summary>
internal static class CinemachineHook
{
    private static bool _nativeInstalled;
    private static FirstPersonCameraController _controller;

    internal static FirstPersonCameraController Controller => _controller;

    internal static bool TryInstall(FirstPersonCameraController controller)
    {
        _controller = controller;
        if (_nativeInstalled) return true;

        try
        {
            if (Il2CppNativeLocator.FindManualUpdateSymbol() == null)
            {
                Logger.Info(nameof(CinemachineHook),
                    "ManualUpdate is not an exported symbol on this build — using the " +
                    "equivalent 'disable CinemachineBrain' path (same behaviour).");
                return false;
            }

            _nativeInstalled = CinemachineManualUpdateHook.InstallHooks();
            Logger.Info(nameof(CinemachineHook), _nativeInstalled
                ? "Native ManualUpdate detour installed."
                : "Native ManualUpdate detour could not be installed; managed equivalent stays active.");
            return _nativeInstalled;
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(CinemachineHook), $"Native hook unavailable: {ex.Message}");
            return false;
        }
    }

    internal static void Uninstall()
    {
        if (!_nativeInstalled) return;
        try { CinemachineManualUpdateHook.UninstallHooks(); }
        catch (Exception ex) { Logger.Warn(nameof(CinemachineHook), $"Uninstall: {ex.Message}"); }
        _nativeInstalled = false;
    }

    internal static void Reinstall(FirstPersonCameraController controller)
    {
        Uninstall();
        _controller = controller;
        TryInstall(controller);
    }

    /// <summary>Prefix body — true lets the original run, false suppresses it.</summary>
    internal static bool ShouldAllowOriginal()
    {
        var controller = _controller;
        return !(controller != null && controller.IsActive && !controller.InCutscene);
    }
}

/// <summary>
/// The Harmony prefix expressed as a StArray native hook. <c>[NativeHook]</c> makes the analyzer
/// generate <c>InstallHooks</c>, <c>UninstallHooks</c> and the <c>OnManualUpdateOriginal</c>
/// trampoline.
/// </summary>
// Must match the modifier set the generator emits ("{access} unsafe partial class"),
// otherwise the two halves of the partial type do not merge (CS0260/CS0227).
internal static unsafe partial class CinemachineManualUpdateHook
{
    [NativeHook(nameof(ResolveManualUpdate), Convention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static void OnManualUpdate(nint thiz)
    {
        // Not calling the trampoline == Harmony prefix returning false.
        if (!CinemachineHook.ShouldAllowOriginal()) return;

        OnManualUpdateOriginal(thiz);
    }

    /// <summary>Address resolver understood by <c>[NativeHook(nameof(...))]</c>.</summary>
    private static nint ResolveManualUpdate() => Il2CppNativeLocator.ResolveManualUpdate();
}

/// <summary>
/// Locates <c>CinemachineBrain.ManualUpdate</c> inside <c>libil2cpp.so</c> using StArray's
/// <c>NativeFuncResolver</c> (dynamic-symbol scan, with a byte-signature fallback).
/// </summary>
internal static class Il2CppNativeLocator
{
    private const string Il2CppLibraryPath = "/system/lib64/libil2cpp.so";

    /// <summary>Il2Cpp symbol mangling for <c>ManualUpdate</c>, most specific first.</summary>
    private static readonly string[] Candidates =
    {
        "_ZN10Cinemachine16CinemachineBrain12ManualUpdateEv",
        "Cinemachine_CinemachineBrain__ManualUpdate",
        "CinemachineBrain$$ManualUpdate",
    };

    private static string _cachedSymbol;
    private static bool _searched;

    internal static string FindManualUpdateSymbol()
    {
        if (_searched) return _cachedSymbol;
        _searched = true;

        try
        {
            var resolver = new NativeFuncResolver(Il2CppLibraryPath);
            foreach (var candidate in Candidates)
            {
                // FindSymbolRva only reads the ELF symbol tables — no library load, no side effects.
                if (resolver.FindSymbolRva(candidate) > 0)
                {
                    _cachedSymbol = candidate;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(Il2CppNativeLocator), $"Symbol scan failed: {ex.Message}");
        }

        return _cachedSymbol;
    }

    internal static nint ResolveManualUpdate()
    {
        if (string.IsNullOrEmpty(_cachedSymbol)) return 0;

        try
        {
            var resolver = new NativeFuncResolver(Il2CppLibraryPath);

            // FindSymbolRva returns -1 when absent; FindRva would throw in that case.
            var rva = resolver.FindSymbolRva(_cachedSymbol);
            if (rva <= 0) return 0;

            resolver.Load();
            return resolver.GetFuncPtr(rva);
        }
        catch (Exception ex)
        {
            Logger.Warn(nameof(Il2CppNativeLocator), $"Resolve: {ex.Message}");
            return 0;
        }
    }

    internal static void Reset()
    {
        _searched = false;
        _cachedSymbol = null;
    }
}
