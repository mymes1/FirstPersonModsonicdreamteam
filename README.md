# FirstPersonCameraMod

First-person camera mod for Sonic Dream Team (PC).

## What it does

- Press F5 or controller L3/R3 to toggle first-person camera
- Camera attaches to the character's head
- Works with all playable characters (Sonic, Tails, Knuckles, Amy, Cream, Rouge, Shadow)
- Hides character model
- Smooth mouse/trackpad look

## Requirements

- Sonic Dream Team PC v1.10.1
- MelonLoader installed

## Install

1. Download `FirstPersonCameraMod.dll`
2. Put it in the `Mods` folder next to the game exe
3. Launch the game

## Building from source

Open `FirstPersonCameraMod.csproj` and update the `HintPath` values to point to your game install's `MelonLoader` folder. Then run `dotnet build`.

## Controls

- **F5** - toggle first-person camera
- **L3/R3** - toggle first-person camera (controller)
- **Mouse/trackpad** - look around

