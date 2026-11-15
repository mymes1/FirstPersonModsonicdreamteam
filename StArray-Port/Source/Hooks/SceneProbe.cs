using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Manager;

namespace FirstPersonCamera.Port.Hooks;

/// <summary>
/// Scene-change probe.
///
/// The manager has no "scene loaded" callback, so the active <c>UnityEngine.SceneManagement.Scene</c>
/// is sampled from <c>Camera.main</c>'s owning scene, falling back to the camera's
/// <c>gameObject.scene</c>. The returned value is only ever compared for equality — a stable value
/// per scene is all that is needed to detect a load.
/// </summary>
internal static class SceneProbe
{
    private static nint _getScene;

    internal static nint ActiveSceneHandle()
    {
        var camera = UnityApi.MainCamera;
        if (camera == 0) return 0;

        try
        {
            if (_getScene == 0)
            {
                _getScene = Il2Cpp.FindMethodInHierarchy(
                    Il2Cpp.ObjectGetClass(camera), "get_scene", 0);
            }

            if (_getScene == 0) return 0;

            // A Scene is a value type: the boxed result carries the handle.
            return Il2Cpp.InvokeInstanceUnbox<nint>(camera, _getScene, null);
        }
        catch
        {
            return 0;
        }
    }

    internal static void Reset() => _getScene = 0;
}

/// <summary>
/// Gamepad stick-click polling (original: <c>KeyCode.JoystickButton8</c> / <c>JoystickButton9</c>).
/// </summary>
internal static class GameInput
{
    private static nint _getKeyDown;
    private static bool _resolved;
    private static bool _unavailable;

    internal static bool GamepadTogglePressed()
    {
        if (!_resolved)
        {
            _resolved = true;
            _getKeyDown = GameApi.InputGetKeyDown;
            if (_getKeyDown == 0)
            {
                _unavailable = true;
                Logger.Info("GameInput", "Legacy key polling unavailable; gamepad toggle disabled.");
            }
        }

        if (_unavailable || _getKeyDown == 0) return false;

        try
        {
            // KeyCode.JoystickButton8 / JoystickButton9 = 358 / 359 in Unity's enum.
            if (KeyDown(358)) return true;
            if (KeyDown(359)) return true;
        }
        catch { _unavailable = true; }

        return false;
    }

    private static bool KeyDown(int keyCode)
    {
        var value = keyCode;
        return Il2Cpp.InvokeStaticBool(_getKeyDown, new[] { Il2Cpp.Addr(ref value) });
    }
}
