# ConversionReport — MelonLoader → StArray.ModManager (Android / IL2CPP)

**Mod:** FirstPersonCamera (originally `FirstPersonCameraMod`, MelonLoader, Sonic Dream Team PC)
**Port:** `FirstPersonCamera.Port`, StArray.ModManager plugin, Android ARM64-v8a
**Result:** compiles clean in Release (0 errors, 0 warnings) against the real StArray 1.0.6 assemblies.
**Original project:** left untouched — the port lives in a copy under `StArray-Port/Source/`.

---

## 1. Build facts (verifiable)

| Item | Value |
|---|---|
| Output | `StArray-Port/Build/FirstPersonCameraMod.dll` |
| Size / SHA-256 | 54,272 bytes / `82ada62253de5c09943fe820a79e5aa91e84818793e36c98c7c9cca0536df115` |
| Target framework | `.NETCoreApp,Version=v10.0` (baked into the assembly; matches the CoreCLR 10.0.6 the StArray Android AAR ships) |
| Compiler | .NET SDK 10 (GitHub Actions `ubuntu-latest`), `dotnet build -c Release` |
| References | `StArray.ModManager.dll`, `ImGui.NET.dll` — both extracted from the **real** release `modmanager-android.zip` 1.0.6 |
| Analyzer | `StArray.ModManager.Analyzer.dll`, built from the StArray source tree at build time (Roslyn source generator that emits the hook plumbing) |
| MelonLoader / Harmony / Il2CppInterop / `Il2CppCinemachine` / `Il2CppHardlightProject` references | **none** (verified by scanning the built DLL) |
| Generated hook surface present | `OnManualUpdateOriginal`, `InstallHooks`, `UninstallHooks` (verified in the DLL) |
| `IModPlugin` + `ModEntryPointAttribute` present | yes (verified) |

Build log: [`BuildLog.txt`](BuildLog.txt). Reproduce with `.github/workflows/build-mod.yml`.

## 2. What the original mod did, and where it went

| Original (`FirstPersonCameraMod.cs`) | Port |
|---|---|
| `MelonMod` + `MelonInfo` + `MelonGame` | `FirstPersonCameraPlugin : IModPlugin, IModSettings` + `[assembly: ModEntryPoint]` |
| `OnInitializeMelon()` | `OnLoad()` |
| `OnDeinitializeMelon()` (implicit) | `OnUnload()` |
| `OnUpdate()` / `OnLateUpdate()` | `FirstPersonCameraBehaviour : GameBehaviour` (`OnUpdate`, `OnLateUpdate`) driven by `BehaviourManager` |
| `OnSceneWasLoaded(int, string)` | `FirstPersonCameraBehaviour.WatchScene()` + `SceneProbe` (polled, 4 Hz) |
| `OnApplicationQuit()` | `OnUnload()` + `IGameBehaviour.OnStop()` |
| `MelonPreferences` category + 9 entries | 9 public fields with `[ModSettingLabel]` / `[ModSettingRange]` / `[ModSettingTooltip]`, drawn by `ModInspector`, persisted by the manager to `settings.json` |
| `MelonLogger.Msg/Warning/Error` | `Logger.Info/Warn/Error` |
| `MelonCoroutines` | *not used by the original* — nothing to port |
| Harmony prefix on `CinemachineBrain.ManualUpdate` | two-layer port, see §4 |
| `Il2CppCinemachine.CinemachineBrain`, `.CinemachineCameraManager` | runtime-resolved IL2CPP types (`GameApi`), no reference assemblies |
| `Il2CppHardlightProject.Actor` / `Character` | `ActorRef` over a GC-rooted IL2CPP pointer |
| `UnityEngine.*` (desktop reference DLLs) | IL2CPP reflection bindings in `Runtime/GameApi.cs` (Camera, Time, Input, Object, Transform, Renderer) |
| `WasCollected` | `ObjectHandle` (`il2cpp_gchandle_new` root + `get_target` liveness) and `Il2CppObject.WasCollected` |
| `KeyCode.F5` / `JoystickButton8/9` | `Hotkey` field (ImGui key capture, persisted) + gamepad KeyCode poll + two on-screen touch buttons |
| Mouse look (`Input.GetAxisRaw("Mouse X"/"Mouse Y")`) | touch drag surface (`TouchLook`, full-screen invisible ImGui surface) **plus** the legacy mouse axis when present |

## 3. Feature parity checklist

Every behaviour in the original file has a counterpart; nothing was dropped to make it compile.

- [x] Toggle first-person on/off (hotkey, gamepad, on-screen button)
- [x] Resolve the player via `CinemachineCameraManager.m_playerCharacter`, with the
      `Character`-subclass / known-name / first-found fallback chain (`PickPlayerActor`)
- [x] Fallback path when no camera manager exists (drive `Camera.main` directly)
- [x] Head-bone search: `ActorAttachPoints.TryGetNodeTransform(Head → Body)`, then hierarchy scan with
      the same accept/reject name lists and the same "highest Y outside 0.3 m of the actor origin" score
- [x] Head-top fallback from combined renderer bounds (`max.y - 0.15`), then fixed height fallback
- [x] Camera placement: forward offset, vertical offset, 2 m leash from the actor, position smoothing
- [x] Yaw/pitch look with the original sensitivity/invert/clamp and exponential smoothing
      (`1 - exp(-k·dt)`), including yaw initialisation from the actor's facing
- [x] Cinemachine brain suppression while active, restore on disable/scene change/cutscene
- [x] Cutscene detection (brain enabled) suspends the camera and restores renderers, exactly as before
- [x] Character hiding: renderer sweep within `HideRadius`, keep-list (`hand`, `arm`, … `accessory`),
      20-frame rescan, original state captured and restored
- [x] Late-update camera re-apply
- [x] All 9 tuning values with the original defaults and ranges
- [x] Log lines for enable/disable/new character/errors (same wording, via `Logger`)

## 4. Harmony patch → StArray hook (detail)

Original:

```csharp
var brainType = AccessTools.TypeByName("Il2CppCinemachine.CinemachineBrain");
var manualUpdate = brainType?.GetMethod("ManualUpdate", ...);
HarmonyInstance.Patch(manualUpdate, prefix: new HarmonyMethod(typeof(...), nameof(SkipBrainUpdate)));

private static bool SkipBrainUpdate(object __instance)
    => !(self != null && self._fpActive && self._camera != null && self._actor != null);
```

Port — `Hooks/CinemachineHook.cs`, predicate preserved verbatim in `ShouldAllowOriginal()`:

1. **Managed (primary).** `CinemachineBrain.enabled = false` while first-person owns the camera.
   `CinemachineBrain.Update()` returns immediately when the behaviour is disabled, so `ManualUpdate`
   is never reached — the same observable effect as the prefix returning `false`. This path needs no
   symbol, no RVA and no per-build offset.
2. **Native (secondary, real `[NativeHook]`).** If `ManualUpdate` exists in `libil2cpp.so`'s dynamic
   symbol table, `CinemachineManualUpdateHook` detours it and skips the trampoline while the view is
   active. Symbols are probed with `NativeFuncResolver.FindSymbolRva` (no load, no side effects) and
   the address comes from `FindSymbolRva` + `GetFuncPtr`. The generated `InstallHooks()` is called
   after `HookHelper.Instance` is set by the manager, so installation actually takes effect.
   If the symbol is absent, the installer logs exactly that and layer 1 carries the behaviour.

Why not a plain `[UnmanagedHook]` on `ManualUpdate`: the detour needs an exact native signature, which
varies with the Cinemachine version and, on ARM64, with struct-return handling. A wrong prototype on
an IL2CPP stub corrupts the stack. See `UnsupportedFeatures.md` §1.

## 5. Architecture of the port

```
Source/
  Plugin/   FirstPersonCameraPlugin.cs     IModPlugin + IModSettings (metadata, settings UI, GUI layers)
            FirstPersonCameraBehaviour.cs  GameBehaviour pump: toggle polling, scene watch, Tick/LateTick
            Hud.cs                         touch buttons, status line, crosshair (foreground layer)
  Camera/   FirstPersonCameraController.cs the original algorithm (resolve → look → position → hide)
  Runtime/  Il2Cpp.cs / Il2CppNative.cs     invocation + method cache + transcribed il2cpp exports
            Il2CppObject.cs                 ObjectHandle (GC root), Il2CppObject, TransformRef
            GameApi.cs                      cached binds for Unity + game members, UnityTime/UnityApi
            ActorRef.cs                     Hardlight.Actor view
            StArrayBridge.cs                cached, non-fatal access to StArray internals
            TouchLook.cs / PointerLook       touch drag + legacy mouse axes
            Vec.cs                          Vector3/Quaternion/Bounds maths (Unity layout)
  Hooks/    CinemachineHook.cs              the ported prefix + [NativeHook] + symbol locator
            SceneProbe.cs                   scene watchdog + gamepad polling
  Diagnostics/SelfTest.cs                   startup capability report
```

## 6. Runtime-verification status (read this)

**What is proven:** it compiles against the genuine StArray assemblies, the Roslyn generator ran, the
emitted plugin type implements `IModPlugin`, `ModEntryPoint` is present, the hook plumbing exists,
and no MelonLoader/Harmony/interop reference survives.

**What is NOT proven here:** behaviour in-game. This environment has no Android device, no Project
Dream install, and no `global-metadata.dat`, so nothing below could be executed:

- game/Unity reflection names (`Hardlight.Actor`, `Cinemachine.CinemachineCameraManager`,
  `get_m_playerCharacter`, `ActorAttachPoints.TryGetNodeTransform`, `get_VisualProxy`,
  `get_AnimationRoot`) — taken from the desktop build's Il2Cpp assemblies; an Android build with
  different stripping will show up in the logs as `missing` from `SelfTest`
- `il2cpp_runtime_invoke` / `il2cpp_object_unbox` ABI on the device
- whether `ManualUpdate` is exported at all in `libil2cpp.so` (layer 1 works either way)
- touch-drag feel and sensitivity calibration

This is why `Diagnostics/SelfTest.cs` logs the complete binding table at startup, and why every
resolve failure is logged rather than thrown.

## 7. Install

```
/sdcard/ModManager/mods/com.mymes.firstpersoncamera/
  FirstPersonCameraMod.dll      ← from StArray-Port/Build/
```

No dependency DLLs are shipped: `StArray.ModManager.dll` and `ImGui.NET.dll` come from the manager
itself, and nothing else is referenced. See `StArray-Port/Build/RequiredDependencies/README.md`.
