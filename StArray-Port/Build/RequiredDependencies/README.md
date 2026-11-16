# RequiredDependencies

**This mod requires no additional DLLs.**

| Assembly | Where it comes from | Ship it? |
|---|---|---|
| `StArray.ModManager.dll` | the installed StArray.ModManager (`/sdcard/ModManager/manager/`) | **no** — the manager provides it |
| `ImGui.NET.dll` | same location | **no** |
| Unity / game assemblies (`UnityEngine.*`, Cinemachine, `Assembly-CSharp`) | the game's own IL2CPP metadata inside the APK | **no** — resolved at runtime, never referenced at build time |

Deploy only `FirstPersonCameraMod.dll` into `mods/com.mymes.firstpersoncamera/`.

If you ever see `FileNotFoundException: StArray.ModManager` in logcat, the mod was placed outside
`/sdcard/ModManager/mods/` or the manager version is older than 1.0.6.

## Rebuilding

`Source/FirstPersonCameraMod.csproj` expects the two StArray assemblies (and the analyzer) in
`StArray-Port/lib/`; override with `-p:StArrayLibDir=<dir>`. `.github/workflows/build-mod.yml` fetches
them automatically from the StArray 1.0.6 release and builds the analyzer from source.
