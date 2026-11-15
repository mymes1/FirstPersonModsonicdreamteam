using System;
using System.Collections.Generic;
using FirstPersonCamera.Port.Camera;
using FirstPersonCamera.Port.Diagnostics;
using FirstPersonCamera.Port.Hooks;
using FirstPersonCamera.Port.Runtime;
using ImGuiNET;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Inspector;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;

// Points the loader straight at the plugin type. Without it StArray has to call GetTypes() on the
// whole assembly, and any single unresolvable type aborts that scan.
[assembly: ModEntryPoint(typeof(FirstPersonCamera.Port.FirstPersonCameraPlugin))]

namespace FirstPersonCamera.Port;

/// <summary>
/// StArray.ModManager entry point for the first-person camera.
///
/// This is the direct replacement for the original mod's <c>MelonMod</c> subclass:
/// <c>MelonInfo</c> → <see cref="IModPlugin"/> metadata properties,
/// <c>OnInitializeMelon</c> → <see cref="OnLoad"/>,
/// <c>OnDeinitializeMelon</c> → <see cref="OnUnload"/>,
/// <c>OnUpdate</c>/<c>OnLateUpdate</c> → <see cref="FirstPersonCameraBehaviour"/>,
/// <c>OnSceneWasLoaded</c> → <see cref="FirstPersonCameraBehaviour"/>'s scene watch,
/// <c>MelonPreferences</c> → <see cref="IModSettings"/> + <see cref="ModInspector"/>.
/// </summary>
public sealed class FirstPersonCameraPlugin : IModPlugin, IModSettings
{
    public string Id => "com.mymes.firstpersoncamera";
    public string Name => "FirstPersonCamera";
    public string Version => "1.0.0-android";
    public string Author => "mymes";
    public string Description => "First-person camera for Sonic Dream Team (StArray port).";

    public IReadOnlyList<string> Dependencies => Array.Empty<string>();

    private FirstPersonCameraController _controller;
    private FirstPersonCameraBehaviour _behaviour;

    [ModSettingHeader("Controls")]
    [ModSettingLabel("Toggle Hotkey")]
    [ModSettingTooltip("Toggles the first-person view. The on-screen FP CAM button always works.")]
    public Hotkey ToggleHotkey = new(ImGuiKey.F5);

    [ModSettingLabel("Gamepad L3 / R3")]
    [ModSettingTooltip("Legacy gamepad stick clicks (JoystickButton8 / 9) if the game reports them.")]
    public bool GamepadToggle = true;

    [ModSettingHeader("Camera")]
    [ModSettingLabel("Enabled")]
    public bool StartEnabled = false;

    [ModSettingLabel("Show HUD")]
    public bool ShowHud = true;

    /// <summary>Exposed so the settings panel can draw the same fields the mod uses.</summary>
    public FirstPersonCameraController Camera => _controller;

    // ── Lifecycle ──

    public void OnLoad()
    {
        Logger.Info(Name, $"Loading {Name} {Version} (StArray.ModManager on Android/IL2CPP)");

        GameApi.Initialise();

        _controller = new FirstPersonCameraController();

        // "Enabled" is persisted in the camera's own settings block (same key the desktop mod used),
        // so it is the single source of truth; StartEnabled only seeds the very first session.
        _controller.Enabled = StartEnabled || _controller.Enabled;
        StartEnabled = _controller.Enabled;

        _behaviour = new FirstPersonCameraBehaviour(this, _controller);
        BehaviourManager.Add(_behaviour);

        // The original Harmony prefix on CinemachineBrain.ManualUpdate.
        if (!CinemachineHook.TryInstall(_controller))
            Logger.Warn(Name, "Cinemachine hook not installed — the view switcher (manual collapse) stays available.");

        SelfTest.Run();

        Logger.Info(Name, "Loaded. F5 / L3 / R3 / on-screen FP CAM toggles first-person.");
    }

    public void OnUnload()
    {
        Logger.Info(Name, "Unloading — restoring camera, renderers and hooks.");

        CinemachineHook.Uninstall();

        if (_behaviour != null)
        {
            BehaviourManager.Remove(_behaviour);
            _behaviour = null;
        }

        _controller?.Shutdown();
        TouchLook.Reset();
        StArrayBridge.Clear();
    }

    // ── GUI ──

    /// <summary>
    /// Overlay layer. Draws the touch drag surface first (so the finger capture sits under every
    /// other overlay), then the minimal first-person HUD.
    /// </summary>
    public void OnBackgroundGUI(ImDrawListPtr drawList)
    {
        if (_controller == null) return;

        var io = ImGui.GetIO();
        var w = (int)io.DisplaySize.X;
        var h = (int)io.DisplaySize.Y;

        TouchLook.DrawDragSurface(w, h);

        if (!ShowHud) return;

        try
        {
            Hud.Draw(this, _controller, drawList, w, h);
        }
        catch (Exception ex)
        {
            Logger.Warn(Name, $"HUD draw failed: {ex.Message}");
        }
    }

    /// <summary>Foreground layer — the crosshair must sit above the manager's own window.</summary>
    public void OnForegroundGUI(ImDrawListPtr drawList)
    {
        if (_controller is { IsActive: true, InCutscene: false })
            Hud.DrawCrosshair(drawList);
    }

    /// <summary>Settings page (was: MelonPreferences category).</summary>
    public void OnGui()
    {
        if (_controller == null) return;

        ImGui.TextDisabled($"{Name} {Version}");
        ImGui.Separator();

        // Plugin-level settings (hotkey / HUD) first, then the camera's own tunables.
        ModInspector.Draw(this);

        ImGui.Spacing();
        ImGui.SeparatorText("Camera tuning");
        ModInspector.Draw(_controller);

        ImGui.Spacing();
        if (ImGui.Button(_controller.IsActive ? "Disable first-person" : "Enable first-person"))
            _controller.Toggle();
    }

    // ── Called by the behaviour each frame ──

    internal void PollToggle()
    {
        if (_controller == null) return;

        if (ToggleHotkey.IsPressed()) { _controller.Toggle(); return; }

        if (GamepadToggle && GameInput.GamepadTogglePressed()) _controller.Toggle();
    }

    /// <summary>Local button press from the HUD.</summary>
    internal void ToggleFromHud()
    {
        var state = _controller.Toggle();
        _controller.Enabled = state;
        StartEnabled = state;
        Logger.Info(Name, $"Toggled from HUD → {(state ? "ON" : "OFF")}");
    }
}
