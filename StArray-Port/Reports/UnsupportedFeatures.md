# UnsupportedFeatures / degraded paths

Nothing from the original mod was removed. This file lists the places where the *mechanism* had to
change, what replaces it, and what cannot be guaranteed without a device.

---

## 1. `[UnmanagedHook]` on `CinemachineBrain.ManualUpdate` — implemented, but not as the primary path

**Missing capability:** StArray's `[UnmanagedHook]` can target a managed (IL2CPP) method by
`assembly / class / method`, but the detour body it generates is a native function pointer with a
hand-written signature. For `CinemachineBrain.ManualUpdate` that means declaring `this` plus whatever
the Cinemachine version takes, and on ARM64 the return-value convention differs again when the method
returns a struct. Declaring the wrong prototype on an IL2CPP stub does not throw — it corrupts the
stack on the first call.

**What the port does instead:** the prefix's *predicate* is preserved (`ShouldAllowOriginal()`), and the
suppression is achieved through the behaviour API (`CinemachineBrain.enabled = false`, which makes
`Update()` bail out before `ManualUpdate`). Additionally, a real `[NativeHook]` detour is installed when
`libil2cpp.so` exports the method in its dynamic symbol table — verified with
`NativeFuncResolver.FindSymbolRva`, skipped silently when absent.

**Residual risk:** if a build both (a) exports `ManualUpdate` and (b) calls it from a path that ignores
`enabled`, the native detour is required. In that case the detour is installed, so behaviour matches.
If the build does not export it, layer 1 covers it — same result, different mechanism.

---

## 2. Cross-assembly reflection into `StArray.ModManager` internals

StArray keeps a few helpers `internal`:

| Member | Why the port needs it |
|---|---|
| `Il2CppFunctions.il2cpp_object_unbox` | `InvokeUnbox` is public on `Il2CppMethod`, but arrays and enum/primitive returns come back boxed through the untyped path |
| `Il2CppFunctions.il2cpp_gchandle_new/_get_target/_free` | rooting game objects so the GC cannot free a character/camera the mod still holds |
| `Il2CppFunctions.il2cpp_array_length`, `il2cpp_object_get_virtual_method` | array walking for `FindObjectsOfType` results |

Because a mod loads into its own `AssemblyLoadContext` but shares the host's `StArray.ModManager`
instance, reflection over the public type surface works. `Runtime/StArrayBridge.cs` binds these once
into typed delegates (no per-frame `MethodInfo.Invoke`) and **fails soft**: if a future StArray release
renames them, one warning is logged and only the affected feature degrades.

**Missing capability:** there is no public StArray API for GC-handle rooting or unboxing arbitrary
returns. If a future release exposes them publicly, `StArrayBridge` can be deleted outright.

---

## 3. Direct native `il2cpp` P/Invoke (one file)

`Runtime/Il2Cpp.cs` → `Il2CppNative` declares ten `il2cpp_*` exports transcribed from standard IL2CPP
headers, because they are internal in StArray. The library name is `libil2cpp.so` (Android). If it
cannot be resolved, **one** error is logged and the managed path (StArray's own `RuntimeObject` /
`RuntimeManager` API) continues to work — the camera functions but with the read-heavy helpers
(`FindMethodInHierarchy`, array walking) unavailable.

This is not a `[NativeHook]`: no functions are intercepted, only called.

---

## 4. Mouse look on a touchscreen

The original read `Input.GetAxisRaw("Mouse X"/"Mouse Y")`. On Android there is no mouse. Both sources
are wired up:

- **Touch:** a transparent, full-screen ImGui surface captures a finger drag; pixels are scaled into
  the same unit the desktop axis used, so the original sensitivity numbers keep their meaning.
- **Legacy mouse:** still polled, so a desktop/emulator build behaves exactly like the original.

Two on-screen buttons (top-left / top-right) replace `L3`/`R3`, and the `F5` binding became a
`Hotkey` field that the user can rebind in the manager. The original's exact key codes are still
polled when the game exposes `UnityEngine.Input`.

---

## 5. Things that cannot be verified without the device

These are **not** claimed to work; they are implemented and instrumented:

1. Reflection names for the game's own types, taken from the desktop Il2Cpp assemblies:
   `Hardlight.Actor`, `Hardlight.Character`, `Hardlight.ActorAttachPoints.TryGetNodeTransform`,
   `Actor.ActorAttachPoints`, `Actor.VisualProxy.AnimationRoot`,
   `Cinemachine.CinemachineCameraManager` (+ `m_playerCharacter`, `MainCamera`, `CinemachineBrain`),
   `Cinemachine.CinemachineBrain`.
   An Android build with aggressive managed stripping may not expose some of these; `SelfTest` logs
   each one as `ok` / `missing` at startup, and every consumer has a fallback
   (name scan → bounds → fixed height for the head bone; `Camera.main` for the camera).
2. `il2cpp_runtime_invoke` conventions: value types passed **by address**, reference types passed
   **directly**, struct returns unboxed. This is standard IL2CPP and matches both StArray's own
   `UnityScreen` and its stub generator, but it is untested on this device build.
3. Whether `ManualUpdate` appears in `libil2cpp.so`'s dynsym (see §1).
4. Field names with the `m_` prefix (`m_playerCharacter`) — the original used the same name through
   Il2CppInterop, so it should hold; `get_m_playerCharacter` is tried and its absence is handled.
5. Performance of the renderer sweep (`FindObjectsOfType(Renderer)` every 20 frames) on a phone. The
   original ran it on PC; the interval is unchanged and can be raised from the settings panel
   (`Hide Radius` gates the distance test).

## 6. Deliberate non-goals

- No attempt to rebuild or replace any MelonLoader functionality.
- No generated-stub project: the port reflects over the live runtime instead, so nothing is pinned to
  one build's metadata.
- The original desktop project is untouched; the port is a copy.
