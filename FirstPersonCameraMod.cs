using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppCinemachine;
using Il2CppHardlightProject;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FirstPersonCameraMod), "FirstPersonCamera", "1.0.0", "mymes", "First-person camera for Sonic Dream Team")]
[assembly: MelonGame("SonicDreamTeam")]

public class FirstPersonCameraMod : MelonMod
{
    private const KeyCode ToggleKey = KeyCode.F5;
    private const KeyCode ControllerToggleKeyL3 = KeyCode.JoystickButton8;
    private const KeyCode ControllerToggleKeyR3 = KeyCode.JoystickButton9;

    private MelonPreferences_Category _prefCategory;
    private MelonPreferences_Entry<bool> _prefEnabled;
    private MelonPreferences_Entry<float> _prefSensitivity;
    private MelonPreferences_Entry<bool> _prefInvertY;
    private MelonPreferences_Entry<float> _prefHeadOffset;
    private MelonPreferences_Entry<float> _prefPitchClamp;
    private MelonPreferences_Entry<float> _prefLookSmooth;
    private MelonPreferences_Entry<float> _prefPosSmooth;
    private MelonPreferences_Entry<float> _prefHeadHeight;
    private MelonPreferences_Entry<float> _prefVerticalOffset;

    private bool _fpActive;
    private bool _wasInCutscene;
    private float _yaw;
    private float _pitch;
    private bool _yawPitchInitialised;

    private CinemachineCameraManager _camManager;
    private CinemachineBrain _brain;
    private bool _brainWasEnabled = true;
    private bool _brainDisabledByUs;
    private Actor _actor;
    private Transform _headBone;
    private Camera _camera;
    private int _resolveCooldown;
    private int _scanCooldown;
    private int _lastTickedFrame = -1;
    private bool _posInitialised;
    private Vector3 _camPos;

    private float _targetYaw;
    private float _targetPitch;

    private readonly List<Renderer> _hideCandidates = new List<Renderer>();
    private readonly Dictionary<Renderer, bool> _rendererStates = new Dictionary<Renderer, bool>();
    private static readonly string[] KeepNames = { "hand", "arm", "leg", "foot", "finger", "thumb", "toe", "wrist", "ankle", "knee", "elbow" };

    private static FirstPersonCameraMod _instance;

    public override void OnInitializeMelon()
    {
        _instance = this;

        _prefCategory = MelonPreferences.CreateCategory("FirstPersonCamera");
        _prefEnabled = _prefCategory.CreateEntry("Enabled", false, "Start with first-person camera enabled");
        _prefSensitivity = _prefCategory.CreateEntry("MouseSensitivity", 2.0f, "Mouse look sensitivity");
        _prefInvertY = _prefCategory.CreateEntry("InvertY", false, "Invert vertical look");
        _prefHeadOffset = _prefCategory.CreateEntry("HeadForwardOffset", 0f, "Camera distance forward of head bone");
        _prefPitchClamp = _prefCategory.CreateEntry("PitchClamp", 80f, "Max up/down look angle in degrees");
        _prefLookSmooth = _prefCategory.CreateEntry("LookSmoothing", 18f, "Look smoothing factor");
        _prefPosSmooth = _prefCategory.CreateEntry("PositionSmoothing", 30f, "Camera position smoothing factor");
        _prefHeadHeight = _prefCategory.CreateEntry("HeadHeightFallback", 1.8f, "Camera height above actor when no head bone found");
        _prefVerticalOffset = _prefCategory.CreateEntry("VerticalOffset", 0.05f, "Extra vertical camera offset");

        var brainType = AccessTools.TypeByName("Il2CppCinemachine.CinemachineBrain");
        var manualUpdate = brainType?.GetMethod("ManualUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (manualUpdate != null)
        {
            HarmonyInstance.Patch(manualUpdate, prefix: new HarmonyMethod(typeof(FirstPersonCameraMod), nameof(SkipBrainUpdate)));
        }

        MelonLogger.Msg("FirstPersonCamera loaded. Press F5 or L3/R3 to toggle.");
    }

    private static bool SkipBrainUpdate(object __instance)
    {
        var self = _instance;
        return !(self != null && self._fpActive && self._camera != null && self._actor != null);
    }

    public override void OnUpdate()
    {
        if (Input.GetKeyDown(ToggleKey) || Input.GetKeyDown(ControllerToggleKeyL3) || Input.GetKeyDown(ControllerToggleKeyR3))
        {
            ApplyEnabledState(!_fpActive);
        }
        TickFP();
    }

    private void ApplyEnabledState(bool enabled)
    {
        if (enabled == _fpActive) { SyncPref(); return; }
        _fpActive = enabled;
        if (_fpActive)
        {
            _yawPitchInitialised = false;
            MelonLogger.Msg("First-person camera: ENABLED");
        }
        else
        {
            RestoreRenderers();
            RestoreBrain();
            _camManager = null;
            _brain = null;
            _actor = null;
            _headBone = null;
            _camera = null;
            _posInitialised = false;
            _camPos = Vector3.zero;
            _scanCooldown = 0;
            _hideCandidates.Clear();
            _wasInCutscene = false;
            MelonLogger.Msg("First-person camera: DISABLED");
        }
        SyncPref();
    }

    private void SyncPref()
    {
        try { if (_prefEnabled != null) _prefEnabled.Value = _fpActive; } catch { }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _camManager = null;
        _actor = null;
        _headBone = null;
        _camera = null;
        _hideCandidates.Clear();
        _rendererStates.Clear();
        _resolveCooldown = 0;
        _yawPitchInitialised = false;
        _posInitialised = false;
        _scanCooldown = 0;
        _wasInCutscene = false;
        RestoreRenderers();
        RestoreBrain();
    }

    public override void OnLateUpdate()
    {
        TickFP();
    }

    private void TickFP()
    {
        if (Time.frameCount == _lastTickedFrame) return;
        _lastTickedFrame = Time.frameCount;

        try
        {
            if (!_fpActive && _prefEnabled != null && _prefEnabled.Value)
            {
                if (_resolveCooldown == 0 && ResolveTargets())
                {
                    ApplyEnabledState(true);
                }
                else
                {
                    _resolveCooldown = 30;
                }
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
            }

            if (!_wasInCutscene)
            {
                UpdateLook();
                PositionCamera();
                HideCharacterRenderers();
            }
            else
            {
                RestoreRenderers();
                RestoreBrain();
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"FP camera error: {ex}");
            _resolveCooldown = 60;
        }
    }

    private bool ResolveTargets()
    {
        if (_camManager == null || _camManager.WasCollected)
        {
            _camManager = UnityEngine.Object.FindFirstObjectByType<CinemachineCameraManager>();
            _actor = null;
            _headBone = null;
            _camera = null;
            _brain = null;
            _brainDisabledByUs = false;
        }

        if (_camManager == null)
        {
            try
            {
                _camera = Camera.main;
                if (_camera == null) return false;

                if (_actor == null || _actor.WasCollected)
                {
                    _actor = PickPlayerActor();
                    if (_actor == null) return false;
                    _headBone = null;
                    _hideCandidates.Clear();
                    _rendererStates.Clear();
                    _scanCooldown = 0;
                    _posInitialised = false;
                    _yawPitchInitialised = false;
                }

                if (_brain == null || _brain.WasCollected)
                {
                    _brain = _camera.GetComponent<Il2CppCinemachine.CinemachineBrain>();
                    _brainDisabledByUs = false;
                }

                return true;
            }
            catch { return false; }
        }

        Actor playerActor = null;
        if (_camManager != null)
        {
            try { playerActor = _camManager.m_playerCharacter as Actor; }
            catch { }
            if (playerActor != null && playerActor.WasCollected) playerActor = null;
        }

        if (playerActor != null && _actor != playerActor)
        {
            _actor = playerActor;
            _headBone = null;
            _hideCandidates.Clear();
            _rendererStates.Clear();
            _scanCooldown = 0;
            _posInitialised = false;
            _yawPitchInitialised = false;
            MelonLogger.Msg($"FP tracking new character: {_actor.name}");
        }

        if (_actor == null || _actor.WasCollected)
        {
            _actor = playerActor;
            if (_actor == null)
                _actor = PickPlayerActor();
            _headBone = null;
            _hideCandidates.Clear();
            _rendererStates.Clear();
            _scanCooldown = 0;
            _posInitialised = false;
            if (_actor == null) return false;
        }

        if (_camera == null || _camera.WasCollected)
            _camera = Camera.main;
        if (_camera == null && _camManager != null)
            _camera = _camManager.MainCamera;
        if (_camera == null) return false;

        if (_headBone == null || _headBone.WasCollected)
            _headBone = FindHeadBone(_actor);

        return true;
    }

    private static Actor PickPlayerActor()
    {
        var actors = UnityEngine.Object.FindObjectsOfType<Actor>(true);
        if (actors == null || actors.Length == 0) return null;

        Actor fallback = null;
        foreach (var a in actors)
        {
            if (a == null || a.WasCollected) continue;
            if (fallback == null) fallback = a;
            try
            {
                if (a is Character) return a;
            }
            catch { }
            var n = a.name ?? "";
            if (n.IndexOf("sonic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("tails", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("knuckles", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("amy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("cream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("rouge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                return a;
        }
        return fallback;
    }

    private void DisableBrainOnce()
    {
        if (_brainDisabledByUs) return;
        try
        {
            if (_brain == null || _brain.WasCollected)
            {
                _brain = _camManager?.CinemachineBrain;
                if (_brain == null && _camera != null && !_camera.WasCollected)
                    _brain = _camera.GetComponent<Il2CppCinemachine.CinemachineBrain>();
            }
            if (_brain != null)
            {
                _brainWasEnabled = _brain.enabled;
                if (_brain.enabled)
                    _brain.enabled = false;
                _brainDisabledByUs = true;
            }
        }
        catch { _brainDisabledByUs = true; }
    }

    private void RestoreBrain()
    {
        try
        {
            if (_brain != null && !_brain.WasCollected)
                _brain.enabled = _brainWasEnabled;
        }
        catch { }
        _brain = null;
        _brainDisabledByUs = false;
        _brainWasEnabled = true;
    }

    private static Transform FindHeadBone(Actor actor)
    {
        try
        {
            var attachPoints = actor.ActorAttachPoints;
            if (attachPoints != null)
            {
                Transform node = null;
                if (attachPoints.TryGetNodeTransform(ActorAttachPointType.Head, out node) && node != null)
                {
                    var n = node.name ?? "";
                    if (n.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0 &&
                        !n.Contains("LOD0") && !n.Contains("LOD1") && !n.Contains("LOD2"))
                        return node;
                }
                if (attachPoints.TryGetNodeTransform(ActorAttachPointType.Body, out node) && node != null)
                {
                    var n = node.name ?? "";
                    if (n.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0 &&
                        !n.Contains("LOD0") && !n.Contains("LOD1") && !n.Contains("LOD2"))
                        return node;
                }
            }
        }
        catch { }

        Transform searchRoot = null;
        try
        {
            var proxy = actor.VisualProxy;
            if (proxy != null)
            {
                var ar = proxy.AnimationRoot;
                if (ar != null) searchRoot = ar;
                else searchRoot = proxy.transform;
            }
        }
        catch { }
        if (searchRoot == null) searchRoot = actor.transform;
        if (searchRoot == null) return null;

        string[] boneNames = { "head", "neck", "skull", "face", "helmet" };
        string[] rejectNames = { "lod", "root", "mesh", "body_", "_body" };
        Transform best = null;
        float bestScore = float.MinValue;
        float actorY = actor.transform.position.y;
        foreach (var t in searchRoot.GetComponentsInChildren<Transform>(true))
        {
            var n = t.name ?? "";
            bool rejected = false;
            foreach (var rn in rejectNames)
            {
                if (n.IndexOf(rn, StringComparison.OrdinalIgnoreCase) >= 0) { rejected = true; break; }
            }
            if (n.Contains("LOD0") || n.Contains("LOD1") || n.Contains("LOD2")) rejected = true;
            if (rejected) continue;
            if (Mathf.Abs(t.position.y - actorY) < 0.3f) continue;
            foreach (var bn in boneNames)
            {
                if (n.IndexOf(bn, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    float score = t.position.y;
                    if (score > bestScore) { bestScore = score; best = t; }
                    break;
                }
            }
        }
        return best;
    }

    private void UpdateLook()
    {
        if (!_yawPitchInitialised)
        {
            var f = _camera.transform.forward;
            _yaw = _targetYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _pitch = _targetPitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            _yawPitchInitialised = true;
        }

        if (_wasInCutscene) return;

        float sens = _prefSensitivity?.Value ?? 2f;
        float invert = (_prefInvertY?.Value ?? false) ? 1f : -1f;
        _targetYaw += Input.GetAxis("Mouse X") * sens;
        _targetPitch += Input.GetAxis("Mouse Y") * sens * invert;

        float clamp = _prefPitchClamp?.Value ?? 80f;
        _targetPitch = Mathf.Clamp(_targetPitch, -clamp, clamp);

        float smooth = Mathf.Max(0.01f, _prefLookSmooth?.Value ?? 18f);
        float t = 1f - Mathf.Exp(-smooth * Time.deltaTime);
        _yaw = Mathf.Lerp(_yaw, _targetYaw, t);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, t);
    }

    private void PositionCamera()
    {
        if (_wasInCutscene)
        {
            if (_camera != null && !_camera.WasCollected)
            {
                var rot = Quaternion.Euler(_pitch, _yaw, 0f);
                _camera.transform.SetPositionAndRotation(_camPos, rot);
            }
            return;
        }

        var head = _headBone;
        float offset = _prefHeadOffset?.Value ?? 0f;

        Vector3 target;
        Vector3 forward;
        if (head != null)
        {
            target = head.position + head.forward * offset;
            forward = head.forward;
        }
        else
        {
            Vector3 top = GetActorHeadTop(_actor);
            if (top != Vector3.zero)
            {
                target = top;
                forward = _actor.transform.forward;
            }
            else
            {
                float h = _prefHeadHeight?.Value ?? 1.8f;
                target = _actor.transform.position + Vector3.up * h;
                forward = _actor.transform.forward;
            }
        }
        target += forward * offset + Vector3.up * (_prefVerticalOffset?.Value ?? 0.05f);

        Vector3 desiredPos = target;
        float maxDist = 2.0f;
        float dist = Vector3.Distance(desiredPos, _actor.transform.position);
        if (dist > maxDist)
        {
            Vector3 dir = (desiredPos - _actor.transform.position).normalized;
            desiredPos = _actor.transform.position + dir * maxDist;
        }

        if (!_posInitialised)
        {
            _camPos = desiredPos;
            _posInitialised = true;
        }
        else
        {
            float posSmooth = Mathf.Max(0.01f, _prefPosSmooth?.Value ?? 30f);
            float pt = 1f - Mathf.Exp(-posSmooth * Time.deltaTime);
            _camPos = Vector3.Lerp(_camPos, desiredPos, pt);
        }

        var cameraRot = Quaternion.Euler(_pitch, _yaw, 0f);
        _camera.transform.SetPositionAndRotation(_camPos, cameraRot);
    }

    private bool IsInCutscene()
    {
        try
        {
            if (_brain != null && !_brain.WasCollected && _brain.enabled)
                return true;
            if (_camera != null && !_camera.WasCollected)
            {
                var brain = _camera.GetComponent<Il2CppCinemachine.CinemachineBrain>();
                if (brain != null && brain.enabled)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static Vector3 GetActorHeadTop(Actor actor)
    {
        if (actor == null || actor.WasCollected) return Vector3.zero;
        try
        {
            var renderers = actor.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0) return Vector3.zero;
            Bounds b = renderers[0].bounds;
            bool first = true;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.WasCollected) continue;
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return new Vector3(b.center.x, b.max.y - 0.15f, b.center.z);
        }
        catch { }
        return Vector3.zero;
    }

    private void HideCharacterRenderers()
    {
        if (_actor == null || _actor.WasCollected) return;

        if (_hideCandidates.Count == 0 || _scanCooldown <= 0)
        {
            _hideCandidates.Clear();
            Vector3 actorPos = _actor.transform.position;
            var allRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            if (allRenderers != null)
            {
                for (int i = 0; i < allRenderers.Length; i++)
                {
                    var r = allRenderers[i];
                    if (r == null || r.WasCollected) continue;
                    float dist = Vector3.Distance(r.bounds.center, actorPos);
                    if (dist < 3f)
                    {
                        string n = r.name ?? "";
                        bool keep = false;
                        foreach (var keepName in KeepNames)
                        {
                            if (n.IndexOf(keepName, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                keep = true;
                                break;
                            }
                        }
                        if (!keep && n.IndexOf("outfit", StringComparison.OrdinalIgnoreCase) >= 0) keep = true;
                        if (!keep && n.IndexOf("costume", StringComparison.OrdinalIgnoreCase) >= 0) keep = true;
                        if (!keep && n.IndexOf("accessory", StringComparison.OrdinalIgnoreCase) >= 0) keep = true;
                        if (!keep)
                            _hideCandidates.Add(r);
                    }
                }
            }
            _scanCooldown = 20;
        }
        else
        {
            _scanCooldown--;
        }

        for (int i = 0; i < _hideCandidates.Count; i++)
        {
            var r = _hideCandidates[i];
            if (r == null || r.WasCollected) continue;
            if (!_rendererStates.ContainsKey(r))
                _rendererStates[r] = r.enabled;
            if (r.enabled)
                r.enabled = false;
        }
    }

    public override void OnApplicationQuit()
    {
        RestoreRenderers();
        RestoreBrain();
    }

    private void RestoreRenderers()
    {
        foreach (var kv in _rendererStates)
        {
            try
            {
                var r = kv.Key;
                if (r != null && !r.WasCollected)
                    r.enabled = kv.Value;
            }
            catch { }
        }
        _rendererStates.Clear();
    }
}
