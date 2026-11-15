using System.Numerics;
using ImGuiNET;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Runtime;

/// <summary>
/// On-screen look control — the Android counterpart of the original mouse look.
///
/// <para>
/// The original read the desktop mouse through <c>UnityEngine.Input.GetAxisRaw("Mouse X"/"Mouse Y")</c>.
/// On Android there is no mouse, and (for a first-person camera) the whole screen is the mouse
/// surface: the user's finger drags anywhere and the camera turns. That is delivered as an ImGui
/// overlay rectangle covering the display while first-person is active, so the drag is captured
/// regardless of whether the StArray menu is open.
/// </para>
/// <para>
/// Deltas are reported in pixels and converted by the camera using its own sensitivity, so the
/// desktop sensitivity/tuning numbers keep their meaning.
/// </para>
/// </summary>
public static class TouchLook
{
    /// <summary>Set by the plugin: true while the first-person camera owns the view.</summary>
    public static bool Active;

    public static Vector2 Delta { get; private set; }
    public static bool IsDragging { get; private set; }

    // Separate, non-overlapping hover zones for the two on-screen toggle buttons.
    private const float ButtonZoneHeight = 220f;
    private static bool _wasDown;
    private static Vector2 _lastPos;

    /// <summary>Emits the invisible full-screen drag surface. Call early in the frame.</summary>
    public static void DrawDragSurface(int screenWidth, int screenHeight)
    {
        Delta = Vector2.Zero;
        IsDragging = false;

        if (!Active || screenWidth <= 0 || screenHeight <= 0) return;

        try
        {
            ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(screenWidth, screenHeight), ImGuiCond.Always);

            // No background, no border, no focus: invisible to the user, but it still receives input.
            var flags = ImGuiWindowFlags.NoDecoration
                        | ImGuiWindowFlags.NoBackground
                        | ImGuiWindowFlags.NoSavedSettings
                        | ImGuiWindowFlags.NoMove
                        | ImGuiWindowFlags.NoBringToFrontOnFocus;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.Begin("##fpc_look_surface", flags);
            ImGui.InvisibleButton("##fpc_look_area", new Vector2(screenWidth, screenHeight),
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);

            IsDragging = ImGui.IsItemActive();

            var pos = ImGui.GetIO().MousePos;
            if (IsDragging)
            {
                if (_wasDown) Delta = pos - _lastPos;
                _lastPos = pos;
            }
            else
            {
                _lastPos = pos;
            }
            _wasDown = IsDragging;
        }
        catch
        {
            Delta = Vector2.Zero;
            IsDragging = false;
        }
    }

    /// <summary>
    /// Draws one on-screen toggle button at the current cursor. Returns true on the frame it is
    /// pressed. The view-radar HUD places it, so its position follows the safe area.
    /// </summary>
    public static bool DrawToggleButton(float width, float height)
    {
        try
        {
            return ImGui.Button("FP CAM", new Vector2(width, height));
        }
        catch
        {
            return false;
        }
    }

    public static void Reset()
    {
        Delta = Vector2.Zero;
        IsDragging = false;
        _wasDown = false;
        _lastPos = Vector2.Zero;
    }
}

/// <summary>
/// Look input for platforms that do expose the legacy mouse (Windows desktop builds of this mod,
/// and the Android emulator's mouse pass-through). The axis-name strings are created once in the
/// IL2CPP domain and reused — allocating a runtime string per frame would leak into the game heap.
/// </summary>
public static class PointerLook
{
    private static nint _getAxisRaw;
    private static nint _axisXString;
    private static nint _axisYString;
    private static bool _resolved;
    private static bool _unavailable;

    public static void Read(out float x, out float y)
    {
        x = y = 0f;

        if (!_resolved)
        {
            _resolved = true;
            _getAxisRaw = GameApi.InputGetAxisRaw;

            if (_getAxisRaw == 0)
            {
                _unavailable = true;
                Logger.Info("PointerLook", "Legacy Input axes unavailable; touch look only.");
                return;
            }

            try
            {
                var domain = RuntimeManager.GetDomain();
                _axisXString = domain?.NewString("Mouse X") ?? 0;
                _axisYString = domain?.NewString("Mouse Y") ?? 0;
            }
            catch { }
        }

        if (_unavailable || _getAxisRaw == 0 || _axisXString == 0) return;

        try
        {
            x = Il2Cpp.InvokeStaticFloat(_getAxisRaw, new[] { _axisXString });
            y = Il2Cpp.InvokeStaticFloat(_getAxisRaw, new[] { _axisYString });
        }
        catch
        {
            _unavailable = true;
        }
    }
}
