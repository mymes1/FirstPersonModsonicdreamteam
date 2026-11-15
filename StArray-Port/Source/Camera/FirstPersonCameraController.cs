using System;
using System.Collections.Generic;
using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Manager;

namespace FirstPersonCamera.Port.Camera;

/// <summary>
/// First-person camera orchestration — a direct port of the original MelonLoader mod's update loop
/// (<c>OnUpdate</c> + <c>OnLateUpdate</c> → <c>TickFP</c>), target resolution, look handling,
/// head-bone search, brain suppression and character hiding.
///
/// <para>
/// What changed, and only because the platform changed:
/// </para>
/// <list type="bullet">
/// <item>Unity components are addressed through IL2CPP pointers resolved at runtime instead of
/// interop wrapper classes from <c>Il2CppCinemachine.dll</c>.</item>
/// <item>Frame timing comes from the host's render loop (<see cref="Tick"/> receives
/// <c>Time.unscaledTime</c>) instead of MelonLoader's <c>OnUpdate</c> pump; the mod still works in
/// the same two phases (look/position, then late position).</item>
/// <item>Mouse look is fed by the touch drag surface (see <see cref="TouchLook"/>) with the desktop
/// mouse axis as an additional source when present.</item>
/// </list>
///
/// <para>Settings are unchanged: every tunable from <c>MelonPreferences</c> is now a public field
/// drawn by StArray's <c>ModInspector</c> and persisted to the mod folder's <c>settings.json</c>.</para>
/// </summary>
public sealed class FirstPersonCameraController
{
    // ── Settings (was: MelonPreferences entries in the "FirstPersonCamera" category) ──

    [StArray.ModManager.Inspector.ModSettingLabel("Start Enabled")]
    [StArray.ModManager.Inspector.ModSettingTooltip("Begin every session with the first-person camera on.")]
    public bool Enabled = false;

    [StArray.ModManager.Inspector.ModSettingLabel("Look Sensitivity")]
    [StArray.ModManager.Inspector.ModSettingRange(0.1f, 10f)]
    public float Sensitivity = 2.0f;

    [StArray.ModManager.Inspector.ModSettingLabel("Invert Y")]
    public bool InvertY = false;

    [StArray.ModManager.Inspector.ModSettingLabel("Head Forward Offset")]
    [StArray.ModManager.Inspector.ModSettingRange(-1f, 1f)]
    public float HeadForwardOffset = 0f;

    [StArray.ModManager.Inspector.ModSettingLabel("Pitch Clamp")]
    [StArray.ModManager.Inspector.ModSettingRange(10f, 89f)]
    public float PitchClamp = 80f;

    [StArray.ModManager.Inspector.ModSettingLabel("Look Smoothing")]
    [StArray.ModManager.Inspector.ModSettingRange(0f, 60f)]
    public float LookSmoothing = 18f;

    [StArray.ModManager.Inspector.ModSettingLabel("Position Smoothing")]
    [StArray.ModManager.Inspector.ModSettingRange(0f, 60f)]
    public float PositionSmoothing = 30f;

    [StArray.ModManager.Inspector.ModSettingLabel("Head Height Fallback")]
    [StArray.ModManager.Inspector.ModSettingRange(0.5f, 3f)]
    public float HeadHeightFallback = 1.8f;

    [StArray.ModManager.Inspector.ModSettingLabel("Vertical Offset")]
    [StArray.ModManager.Inspector.ModSettingRange(-0.5f, 0.5f)]
    public float VerticalOffset = 0.05f;

    [StArray.ModManager.Inspector.ModSettingLabel("Hide Character")]
    [StArray.ModManager.Inspector.ModSettingTooltip("Disable the character's renderers, keeping hands/outfit visible.")]
    public bool HideCharacter = true;

    [StArray.ModManager.Inspector.ModSettingLabel("Hide Radius")]
    [StArray.ModManager.Inspector.ModSettingRange(1f, 8f)]
    public float HideRadius = 3f;

    [StArray.ModManager.Inspector.ModSettingNoSave]
    [StArray.ModManager.Inspector.ModSettingLabel("Status")]
    [StArray.ModManager.Inspector.ModSettingReadOnly]
    public string Status = "idle";

    // ── State carried over from the original ──

    private bool _fpActive;
    private bool _wasInCutscene;
    private float _yaw;
    private float _pitch;
    private bool _yawPitchInitialised;

    private nint _camManager;
    private nint _brain;
    private bool _brainWasEnabled = true;
    private bool _brainDisabledByUs;
    private ActorRef _actor;
    private nint _headBone;
    private nint _camera;
    private int _resolveCooldown;
    private int _scanCooldown;
    private int _lastTickedFrame = -1;
    private bool _posInitialised;
    private Vec3 _camPos;

    private float _targetYaw;
    private float _targetPitch;

    private readonly List<nint> _hideCandidates = new();
    private readonly Dictionary<nint, bool> _rendererStates = new();

    private static readonly string[] KeepNames =
        { "hand", "arm", "leg", "foot", "finger", "thumb", "toe", "wrist", "ankle", "knee", "elbow" };

    private static readonly string[] HeadBoneNames = { "head", "neck", "skull", "face", "helmet" };
    private static readonly string[] RejectNames = { "lod", "root", "mesh", "body_", "_body" };

    private const float MaxHeadDistance = 2.0f;

    private readonly RenderHider _hider;

    public FirstPersonCameraController() => _hider = new RenderHider(this);

    /// <summary>True while the first-person view owns the game's camera.</summary>
    public bool IsActive => _fpActive;

    /// <summary>True while a cutscene owns the camera (first-person view suspends).</summary>
    public bool InCutscene => _wasInCutscene;

    // ── Lifecycle ──

    /// <summary>Called when the mod is enabled; equivalent of the original's <c>ApplyEnabledState(true)</c>.</summary>
    public void Activate()
    {
        if (_fpActive) return;
        _fpActive = true;
        _yawPitchInitialised = false;
        Status = "active";
        Logger.Info("FirstPersonCamera", "First-person camera: ENABLED");
    }

    /// <summary>Called when the mod is disabled; equivalent of <c>ApplyEnabledState(false)</c>.</summary>
    public void Deactivate()
    {
        if (!_fpActive)
        {
            SyncEnabledFlagFromState(false);
            return;
        }

        _fpActive = false;
        _hider.RestoreAll();
        RestoreBrain();
        _camManager = 0;
        _brain = 0;
        _actor?.Dispose();
        _actor = null;
        _headBone = 0;
        _camera = 0;
        _posInitialised = false;
        _camPos = Vec3.Zero;
        _scanCooldown = 0;
        _hideCandidates.Clear();
        _wasInCutscene = false;
        TouchLook.Active = false;
        TouchLook.Reset();
        Status = "idle";
        Logger.Info("FirstPersonCamera", "First-person camera: DISABLED");
    }

    /// <summary>Toggles the view; returns the new state. Backs the hotkey and the on-screen button.</summary>
    public bool Toggle()
    {
        if (_fpActive) Deactivate();
        else Activate();
        return _fpActive;
    }

    /// <summary>Scene change: every cached game pointer is stale.</summary>
    public void OnSceneLoaded()
    {
        _camManager = 0;
        _brain = 0;
        _headBone = 0;
        _camera = 0;
        _hideCandidates.Clear();
        _hider.ForgetStates();
        _actor?.Dispose();
        _actor = null;
        _resolveCooldown = 0;
        _yawPitchInitialised = false;
        _posInitialised = false;
        _scanCooldown = 0;
        _wasInCutscene = false;

        _hider.RestoreAll();
        RestoreBrain();

        GameApi.Invalidate();
        Status = _fpActive ? "waiting for scene" : "idle";
    }

    /// <summary>Full teardown before the game exits or the mod unloads.</summary>
    public void Shutdown()
    {
        Deactivate();
        _hider.RestoreAll();
        RestoreBrain();
        _actor?.Dispose();
        _actor = null;
    }

    // ── Frame pump ──

    /// <summary>
    /// One logic tick — the original's <c>TickFP</c>. <paramref name="deltaTime"/> is the host's
    /// frame delta (seconds); it replaces <c>Time.deltaTime</c> so the smoothing constants keep
    /// their meaning.
    /// </summary>
    public void Tick(float deltaTime)
    {
        var frame = UnityTime.FrameCount;
        if (frame == _lastTickedFrame) return;
        _lastTickedFrame = frame;

        TouchLook.Active = _fpActive && !_wasInCutscene;

        try
        {
            // Late-activate when the mod is enabled but the world was not ready yet.
            if (!_fpActive && Enabled)
            {
                if (_resolveCooldown == 0 && ResolveTargets())
                    Activate();
                else
                    _resolveCooldown = 30;
            }

            if (!_fpActive) return;

            if (_resolveCooldown > 0) { _resolveCooldown--; return; }

            if (!ResolveTargets())
            {
                _resolveCooldown = 30;
                if (_brainDisabledByUs) RestoreBrain();
                return;
            }

            DisableBrainOnce();

            bool inCutscene = IsInCutscene();
            if (inCutscene != _wasInCutscene)
            {
                _wasInCutscene = inCutscene;
                Status = inCutscene ? "cutscene — suspended" : "active";
            }

            if (!_wasInCutscene)
            {
                UpdateLook(deltaTime);
                PositionCamera(deltaTime);
                if (HideCharacter) _hider.Hide(_actor, HideRadius);
                else _hider.RestoreAll();
            }
            else
            {
                _hider.RestoreAll();
                RestoreBrain();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("FirstPersonCamera", $"FP camera error: {ex}");
            _resolveCooldown = 60;
        }
    }

    /// <summary>
    /// Late pass — re-applies the position after the game's own camera systems ran, matching the
    /// original's <c>OnLateUpdate</c> behaviour.
    /// </summary>
    public void LateTick(float deltaTime)
    {
        if (!_fpActive || _wasInCutscene || _camera == 0) return;

        try
        {
            PositionCamera(deltaTime);
        }
        catch (Exception ex)
        {
            Logger.Warn("FirstPersonCamera", $"LateTick: {ex.Message}");
        }
    }

    // ── Target resolution (original: ResolveTargets / PickPlayerActor) ──

    private bool ResolveTargets()
    {
        if (_camManager == 0 || Il2Cpp.ObjectGetClass(_camManager) == 0)
        {
            if (!GameApi.Initialise()) return false;

            var found = UnityApi.FindObjectsOfType(GameApi.CameraManagerClass, true);
            _camManager = found.Length > 0 ? found[0] : 0;

            _actor?.Dispose();
            _actor = null;
            _headBone = 0;
            _camera = 0;
            _brain = 0;
            _brainDisabledByUs = false;
        }

        // ── Fallback path: no CinemachineCameraManager in this scene, drive Camera.main directly ──
        if (_camManager == 0)
        {
            try
            {
                _camera = UnityApi.MainCamera;
                if (_camera == 0) return false;

                if (_actor == null || _actor.WasCollected)
                {
                    _actor?.Dispose();
                    _actor = PickPlayerActor();
                    if (_actor == null) return false;

                    _headBone = 0;
                    _hideCandidates.Clear();
                    _hider.ForgetStates();
                    _scanCooldown = 0;
                    _posInitialised = false;
                    _yawPitchInitialised = false;
                }

                if (_brain == 0 || Il2Cpp.ObjectGetClass(_brain) == 0)
                {
                    _brain = UnityApi.GetComponent(_camera, GameApi.CinemachineBrainClass);
                    _brainDisabledByUs = false;
                }

                return true;
            }
            catch { return false; }
        }

        // ── Player actor from the camera manager (original: m_playerCharacter as Actor) ──
        nint playerActor = 0;
        try
        {
            var raw = Il2Cpp.InvokeInstance(_camManager, "get_m_playerCharacter", 0, null);
            if (raw != 0 && UnityApi.IsAssignableTo(raw, GameApi.ActorClass))
                playerActor = raw;
        }
        catch { }

        if (playerActor != 0 && (_actor == null || _actor.Ptr != playerActor))
        {
            _actor?.Dispose();
            _actor = new ActorRef(playerActor);
            _headBone = 0;
            _hideCandidates.Clear();
            _hider.ForgetStates();
            _scanCooldown = 0;
            _posInitialised = false;
            _yawPitchInitialised = false;
            Logger.Info("FirstPersonCamera", $"FP tracking new character: {_actor.Name}");
        }

        if (_actor == null || _actor.WasCollected)
        {
            _actor?.Dispose();
            _actor = playerActor != 0 ? new ActorRef(playerActor) : PickPlayerActor();

            _headBone = 0;
            _hideCandidates.Clear();
            _hider.ForgetStates();
            _scanCooldown = 0;
            _posInitialised = false;
            if (_actor == null || !_actor.IsValid) return false;
        }

        if (_camera == 0 || Il2Cpp.ObjectGetClass(_camera) == 0)
            _camera = UnityApi.MainCamera;

        if (_camera == 0 && _camManager != 0)
        {
            try { _camera = Il2Cpp.InvokeInstance(_camManager, GameApi.CameraManagerMainCamera, null); }
            catch { }
        }

        if (_camera == 0) return false;

        if (_headBone == 0 || Il2Cpp.ObjectGetClass(_headBone) == 0)
            _headBone = FindHeadBone(_actor);

        return true;
    }

    /// <summary>
    /// Original <c>PickPlayerActor</c>: prefer an instance of the game's <c>Character</c> subclass,
    /// then a known character name, then anything.
    /// </summary>
    private static ActorRef PickPlayerActor()
    {
        var actors = UnityApi.FindObjectsOfType(GameApi.ActorClass, true);
        if (actors.Length == 0) return null;

        nint fallback = 0;
        foreach (var ptr in actors)
        {
            if (ptr == 0) continue;
            if (fallback == 0) fallback = ptr;

            if (GameApi.CharacterClass != 0 && UnityApi.IsAssignableTo(ptr, GameApi.CharacterClass))
                return new ActorRef(ptr);

            var name = new Il2CppObject(ptr).Name ?? string.Empty;
            if (ContainsIgnoreCase(name, "sonic") || ContainsIgnoreCase(name, "tails") ||
                ContainsIgnoreCase(name, "knuckles") || ContainsIgnoreCase(name, "amy") ||
                ContainsIgnoreCase(name, "cream") || ContainsIgnoreCase(name, "rouge") ||
                ContainsIgnoreCase(name, "shadow"))
                return new ActorRef(ptr);
        }

        return fallback != 0 ? new ActorRef(fallback) : null;
    }

    // ── Look / position (original: UpdateLook / PositionCamera) ──

    private void UpdateLook(float deltaTime)
    {
        if (deltaTime <= 0f) deltaTime = 1f / 60f;

        float mouseX = 0f, mouseY = 0f;
        PointerLook.Read(out mouseX, out mouseY);

        // Touch drag is reported in pixels; scale to the same units as the desktop axis.
        var touch = TouchLook.Delta;
        const float TouchPixelsPerAxisUnit = 0.02f;
        mouseX += touch.X * TouchPixelsPerAxisUnit;
        mouseY -= touch.Y * TouchPixelsPerAxisUnit; // ImGui Y grows downward, Unity's mouse Y grows up

        if (!_yawPitchInitialised)
        {
            var forward = _actor?.Forward ?? Vec3.Forward;
            _yaw = MathF.Atan2(forward.X, forward.Z) * (180f / MathF.PI);
            _pitch = 0f;
            _yaw %= 360f;
            _targetYaw = _yaw;
            _targetPitch = _pitch;
            _yawPitchInitialised = true;
        }

        float sens = MathF.Max(0.01f, Sensitivity);
        float invert = InvertY ? -1f : 1f;

        _targetYaw += mouseX * sens;
        _targetPitch += mouseY * sens * invert;

        float clamp = Math.Clamp(PitchClamp, 1f, 89f);
        _targetPitch = Math.Clamp(_targetPitch, -clamp, clamp);

        float smooth = MathF.Max(0.01f, LookSmoothing);
        float t = 1f - MathF.Exp(-smooth * deltaTime);
        _yaw = LerpAngle(_yaw, _targetYaw, t);
        _pitch = _pitch + (_targetPitch - _pitch) * Math.Clamp(t, 0f, 1f);
    }

    private void PositionCamera(float deltaTime)
    {
        if (_camera == 0 || Il2Cpp.ObjectGetClass(_camera) == 0) return;

        // A paused frame reports deltaTime 0, which would make every Lerp a no-op forever.
        if (deltaTime <= 0f) deltaTime = 1f / 60f;

        var cameraTransform = new TransformRef(Il2Cpp.InvokeInstance(_camera, "get_transform", 0, null));
        if (!cameraTransform.IsValid) return;

        if (_wasInCutscene)
        {
            cameraTransform.SetPositionAndRotation(_camPos, Quat.Euler(_pitch, _yaw, 0f));
            return;
        }

        if (_actor == null || !_actor.IsValid) return;

        float offset = HeadForwardOffset;
        Vec3 target;
        Vec3 forward;

        if (_headBone != 0 && Il2Cpp.ObjectGetClass(_headBone) != 0)
        {
            var head = new TransformRef(_headBone);
            target = head.Position + head.Forward * offset;
            forward = head.Forward;
        }
        else
        {
            var top = GetActorHeadTop(_actor);
            if (top.HasValue)
            {
                target = top.Value;
                forward = _actor.Forward;
            }
            else
            {
                target = _actor.Position + Vec3.Up * HeadHeightFallback;
                forward = _actor.Forward;
            }
        }

        target += forward * offset + Vec3.Up * VerticalOffset;

        var actorPos = _actor.Position;
        var desiredPos = target;
        float dist = Vec3.Distance(desiredPos, actorPos);
        if (dist > MaxHeadDistance)
        {
            var dir = (desiredPos - actorPos).Normalized;
            desiredPos = actorPos + dir * MaxHeadDistance;
        }

        if (!_posInitialised)
        {
            _camPos = desiredPos;
            _posInitialised = true;
        }
        else
        {
            float posSmooth = MathF.Max(0.01f, PositionSmoothing);
            float pt = 1f - MathF.Exp(-posSmooth * deltaTime);
            _camPos = Vec3.Lerp(_camPos, desiredPos, pt);
        }

        cameraTransform.SetPositionAndRotation(_camPos, Quat.Euler(_pitch, _yaw, 0f));
    }

    /// <summary>Original <c>GetActorHeadTop</c>: top of the character's combined renderer bounds.</summary>
    private static Vec3? GetActorHeadTop(ActorRef actor)
    {
        if (actor == null || !actor.IsValid) return null;

        var renderers = actor.GetRenderers();
        if (renderers.Length == 0) return null;

        Bounds bounds = default;
        bool first = true;
        foreach (var ptr in renderers)
        {
            if (ptr == 0) continue;
            if (!new Il2CppObject(ptr).TryGetBounds(out var b)) continue;
            if (first) { bounds = b; first = false; }
            else bounds = bounds.Encapsulate(b);
        }

        if (first) return null;
        var max = bounds.Max;
        return new Vec3(bounds.Center.X, max.Y - 0.15f, bounds.Center.Z);
    }

    // ── Cutscene detection (original: IsInCutscene) ──

    private bool IsInCutscene()
    {
        try
        {
            if (_brain != 0 && Il2Cpp.ObjectGetClass(_brain) != 0 &&
                new Il2CppObject(_brain).Enabled)
                return true;

            if (_camera != 0 && Il2Cpp.ObjectGetClass(_camera) != 0)
            {
                var brain = UnityApi.GetComponent(_camera, GameApi.CinemachineBrainClass);
                if (brain != 0 && new Il2CppObject(brain).Enabled) return true;
            }
        }
        catch { }

        return false;
    }

    // ── Cinemachine brain suppression (original: DisableBrainOnce / RestoreBrain) ──

    private void DisableBrainOnce()
    {
        if (_brainDisabledByUs) return;

        try
        {
            if (_brain == 0 || Il2Cpp.ObjectGetClass(_brain) == 0)
            {
                if (_camManager != 0)
                {
                    try { _brain = Il2Cpp.InvokeInstance(_camManager, GameApi.CameraManagerCinemachineBrain, null); }
                    catch { }
                }

                if (_brain == 0 && _camera != 0)
                    _brain = UnityApi.GetComponent(_camera, GameApi.CinemachineBrainClass);
            }

            if (_brain != 0)
            {
                var brain = new Il2CppObject(_brain);
                _brainWasEnabled = brain.Enabled;
                if (brain.Enabled) brain.Enabled = false;
                _brainDisabledByUs = true;
            }
        }
        catch { _brainDisabledByUs = true; }
    }

    private void RestoreBrain()
    {
        try
        {
            if (_brain != 0 && Il2Cpp.ObjectGetClass(_brain) != 0)
                new Il2CppObject(_brain).Enabled = _brainWasEnabled;
        }
        catch { }

        _brain = 0;
        _brainDisabledByUs = false;
        _brainWasEnabled = true;
    }

    // ── Head bone search (original: FindHeadBone) ──

    private static nint FindHeadBone(ActorRef actor)
    {
        if (actor == null || !actor.IsValid) return 0;

        // 1) The game's own attach points.
        try
        {
            if (GameApi.ActorAttachPoints != 0)
            {
                var attachPoints = Il2Cpp.InvokeInstance(actor.Ptr, GameApi.ActorAttachPoints, null);
                if (attachPoints != 0)
                {
                    var node = TryGetAttachPoint(attachPoints, 0 /* Head */);
                    if (IsUsableBone(node)) return node;

                    node = TryGetAttachPoint(attachPoints, 1 /* Body */);
                    if (IsUsableBone(node)) return node;
                }
            }
        }
        catch { }

        // 2) Fall back to scanning the visual hierarchy by name.
        nint searchRoot = 0;
        try
        {
            if (GameApi.ActorVisualProxy != 0)
            {
                var proxy = Il2Cpp.InvokeInstance(actor.Ptr, GameApi.ActorVisualProxy, null);
                if (proxy != 0)
                {
                    var animationRoot = Il2Cpp.InvokeInstance(proxy, "get_AnimationRoot", 0, null);
                    searchRoot = animationRoot != 0
                        ? Il2Cpp.InvokeInstance(animationRoot, "get_transform", 0, null)
                        : Il2Cpp.InvokeInstance(proxy, "get_transform", 0, null);
                }
            }
        }
        catch { }

        if (searchRoot == 0) searchRoot = actor.Transform.Ptr;
        if (searchRoot == 0) return 0;

        float actorY = actor.Position.Y;
        nint best = 0;
        float bestScore = float.MinValue;

        foreach (var t in UnityApi.GetComponentsInChildren(searchRoot, GameApi.TransformClass, true))
        {
            var transform = new TransformRef(t);
            var n = transform.Name ?? string.Empty;

            bool rejected = false;
            foreach (var rn in RejectNames)
            {
                if (ContainsIgnoreCase(n, rn)) { rejected = true; break; }
            }
            if (ContainsIgnoreCase(n, "LOD0") || ContainsIgnoreCase(n, "LOD1") || ContainsIgnoreCase(n, "LOD2"))
                rejected = true;
            if (rejected) continue;

            var pos = transform.Position;
            if (MathF.Abs(pos.Y - actorY) < 0.3f) continue;

            foreach (var bn in HeadBoneNames)
            {
                if (!ContainsIgnoreCase(n, bn)) continue;

                float score = pos.Y;
                if (score > bestScore) { bestScore = score; best = t; }
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// <c>ActorAttachPoints.TryGetNodeTransform(ActorAttachPointType, out Transform)</c>.
    /// Unity's out-parameter convention on IL2CPP: pass the address of a pointer slot that the
    /// callee fills in.
    /// </summary>
    private static nint TryGetAttachPoint(nint attachPoints, int pointType)
    {
        var method = GameApi.AttachPointsTryGetNodeTransform;
        if (method == 0 || attachPoints == 0) return 0;

        var outTransform = nint.Zero;
        var typeValue = pointType;

        try
        {
            Il2Cpp.InvokeInstance(attachPoints, method, new[]
            {
                Il2Cpp.Addr(ref typeValue),
                Il2Cpp.Addr(ref outTransform),
            });
        }
        catch { }

        return outTransform;
    }

    private static bool IsUsableBone(nint transform)
    {
        if (transform == 0) return false;
        var n = new TransformRef(transform).Name ?? string.Empty;
        if (ContainsIgnoreCase(n, "lod")) return false;
        if (n.Contains("LOD0") || n.Contains("LOD1") || n.Contains("LOD2")) return false;
        return true;
    }

    // ── Helpers ──

    internal static bool ContainsIgnoreCase(string haystack, string needle)
        => !string.IsNullOrEmpty(haystack) &&
           haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static float LerpAngle(float a, float b, float t)
    {
        float delta = (b - a) % 360f;
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        return a + delta * Math.Clamp(t, 0f, 1f);
    }

    private void SyncEnabledFlagFromState(bool state) => Enabled = state;

    // ── Renderer hiding (original: HideCharacterRenderers / RestoreRenderers) ──
    private sealed class RenderHider
    {
        private readonly FirstPersonCameraController _owner;

        public RenderHider(FirstPersonCameraController owner) => _owner = owner;

        public void Hide(ActorRef actor, float radius)
        {
            if (actor == null || !actor.IsValid) return;

            if (_owner._hideCandidates.Count == 0 || _owner._scanCooldown <= 0)
            {
                _owner._hideCandidates.Clear();

                var actorPos = actor.Position;
                foreach (var ptr in UnityApi.FindObjectsOfType(GameApi.RendererClass, true))
                {
                    if (ptr == 0) continue;
                    var renderer = new Il2CppObject(ptr);
                    if (!renderer.TryGetBounds(out var b)) continue;

                    if (Vec3.Distance(b.Center, actorPos) >= radius) continue;

                    var n = renderer.Name ?? string.Empty;
                    bool keep = false;
                    foreach (var keepName in KeepNames)
                    {
                        if (ContainsIgnoreCase(n, keepName)) { keep = true; break; }
                    }
                    if (!keep && ContainsIgnoreCase(n, "outfit")) keep = true;
                    if (!keep && ContainsIgnoreCase(n, "costume")) keep = true;
                    if (!keep && ContainsIgnoreCase(n, "accessory")) keep = true;

                    if (!keep) _owner._hideCandidates.Add(ptr);
                }

                _owner._scanCooldown = 20;
            }
            else
            {
                _owner._scanCooldown--;
            }

            foreach (var ptr in _owner._hideCandidates)
            {
                if (ptr == 0 || Il2Cpp.ObjectGetClass(ptr) == 0) continue;
                var renderer = new Il2CppObject(ptr);

                if (!_owner._rendererStates.ContainsKey(ptr))
                    _owner._rendererStates[ptr] = renderer.Enabled;

                if (renderer.Enabled) renderer.Enabled = false;
            }
        }

        /// <summary>Restores every renderer the mod disabled.</summary>
        public void RestoreAll()
        {
            foreach (var kv in _owner._rendererStates)
            {
                try
                {
                    var ptr = kv.Key;
                    if (ptr == 0 || Il2Cpp.ObjectGetClass(ptr) == 0) continue;
                    new Il2CppObject(ptr).Enabled = kv.Value;
                }
                catch { }
            }
            _owner._rendererStates.Clear();
        }

        public void ForgetStates() => _owner._rendererStates.Clear();
    }
}
