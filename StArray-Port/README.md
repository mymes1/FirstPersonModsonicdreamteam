# StArray-Port — FirstPersonCamera for Android

Android / IL2CPP port of the `FirstPersonCameraMod` MelonLoader mod for Sonic Dream Team
(Project Dream), rewritten against **StArray.ModManager**.

```
StArray-Port/
  Source/     the ported C# project (compile this)
  Build/      FirstPersonCameraMod.dll — the built artifact + RequiredDependencies/
  Reports/    ConversionReport.md, UnsupportedFeatures.md, BuildLog.txt
```

Read `Reports/ConversionReport.md` first: it maps every original feature to its ported implementation
and states plainly what is compile-verified versus device-unverified.

## Install

1. Copy `Build/FirstPersonCameraMod.dll` to `/sdcard/ModManager/mods/com.mymes.firstpersoncamera/`.
2. Start the game, open the StArray menu (INSERT-equivalent), enable *FirstPersonCamera*.
3. Toggle the view with **F5**, gamepad **L3/R3**, or either on-screen **FP: ON/OFF** button
   (top-left / top-right), then drag anywhere on screen to look.

Tuning (sensitivity, invert Y, pitch clamp, smoothing, offsets, hide radius) lives in the mod's
settings page and is saved to `settings.json` next to the DLL.

## Build

```bash
# from the repo root, with .NET 10 SDK
# 1. put the real StArray assemblies in StArray-Port/lib/  (see Reports/ConversionReport.md)
dotnet build StArray-Port/Source/FirstPersonCameraMod.csproj -c Release
```

The CI workflow does all of this end to end and even verifies the generated hook surface in the
produced DLL.
