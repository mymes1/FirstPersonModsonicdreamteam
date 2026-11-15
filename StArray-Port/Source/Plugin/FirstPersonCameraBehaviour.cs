using System;
using FirstPersonCamera.Port.Camera;
using FirstPersonCamera.Port.Hooks;
using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Behaviours;
using StArray.ModManager.Manager;

namespace FirstPersonCamera.Port;

/// <summary>
/// Per-frame pump.
///
/// Replaces MelonLoader's <c>OnUpdate</c> / <c>OnLateUpdate</c> overrides. StArray drives
/// <see cref="GameBehaviour"/> from its render loop once per Unity frame (present hook), which is
/// the same cadence: <c>Update</c> for input + look/position, <c>LateUpdate</c> for the final camera
/// placement after the game's own animation/camera code has run.
/// </summary>
public sealed class FirstPersonCameraBehaviour : GameBehaviour
{
    private readonly FirstPersonCameraController _controller;
    private readonly FirstPersonCameraPlugin _plugin;

    private nint _lastScene;
    private double _lastSceneCheck;
    private bool _started;

    internal FirstPersonCameraBehaviour(FirstPersonCameraPlugin plugin,
        FirstPersonCameraController controller)
    {
        _plugin = plugin;
        _controller = controller;
    }

    public override void OnStart()
    {
        Logger.Info("FirstPersonCamera", "Behaviour started — pumping camera from the render loop.");
    }

    public override void OnUpdate(float delta)
    {
        try
        {
            _plugin.PollToggle();
            WatchScene();

            // Prefer Unity's own frame delta so smoothing matches the game's timescale; fall back to
            // the host's delta when the binding is unavailable.
            var dt = UnityTime.DeltaTime;
            if (dt <= 0f || dt > 0.5f) dt = delta;

            _controller.Tick(dt);
        }
        catch (Exception ex)
        {
            Logger.Error("FirstPersonCamera", $"Update: {ex}");
        }
    }

    public override void OnLateUpdate(float delta)
    {
        try
        {
            var dt = UnityTime.DeltaTime;
            if (dt <= 0f || dt > 0.5f) dt = delta;

            _controller.LateTick(dt);
        }
        catch (Exception ex)
        {
            Logger.Error("FirstPersonCamera", $"LateUpdate: {ex}");
        }
    }

    public override void OnStop()
    {
        _controller.Shutdown();
    }

    /// <summary>
    /// Scene-change watchdog, replacing <c>OnSceneWasLoaded</c>.
    /// The manager exposes no scene callback, so the active scene object is polled a few times a
    /// second and a change invalidates every cached game pointer (they die with the scene).
    /// </summary>
    private void WatchScene()
    {
        var now = UnityTime.UnscaledTime;
        if (now - _lastSceneCheck < 0.25) return;
        _lastSceneCheck = now;

        var scene = SceneProbe.ActiveSceneHandle();
        if (scene == _lastScene) return;

        // The very first observation only records the baseline; resetting on startup would wipe
        // state the mod has already built if the manager loads us mid-scene.
        if (_started)
        {
            Logger.Info("FirstPersonCamera", "Scene changed — resetting camera state.");
            _controller.OnSceneLoaded();
            CinemachineHook.Reinstall(_controller);
        }

        _started = true;
        _lastScene = scene;
    }
}
