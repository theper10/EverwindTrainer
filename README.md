# Everwind Trainer

A Windows trainer for **Everwind** with a modern WPF interface and external
process-memory helper. It is built around a simple idea: run Everwind normally,
open the trainer, then toggle the features you want.

The trainer is external-only. It does not install mods, inject UE4SS, or copy
files into the Everwind game folder.

## Important notes

- Use this only in single-player or private, host-controlled sessions.
- Start the game with the normal `Everwind.exe`.
- Do not start the game from `Everwind-Win64-Shipping.exe` directly.
- The trainer requests Administrator permission so Windows allows it to read and
  write the running game process.
- This project does not include Everwind itself.

## Features

- Infinite health
- Infinite stamina
- Player damage multiplier
- Mining/block damage multiplier
- XP gain multiplier
- Movement speed multiplier
- Jump height multiplier
- Infinite durability
- Durability loss-rate multiplier
- `[Slot 1] Minimum Stack Size`
- Collapsible pinned-feature section
- Launch-game button with drag-and-drop path selection

Not implemented yet:

- Invisibility
- True one-hit kills independent of the damage multiplier
- Ignore crafting ingredients
- Generator/crafting fuel pinning
- Instant acceleration

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A local Everwind install

By default, the app looks for:

```text
%USERPROFILE%\Documents\games\Everwind\Everwind.exe
```

If your game is somewhere else, drag `Everwind.exe` onto the path box in the
trainer, or click the box and browse to it.

## Build and run

From the repository root:

```powershell
dotnet build .\EverwindTrainer.slnx -c Release
```

Publish the desktop app:

```powershell
dotnet publish .\tools\Everwind.TrainerApp\Everwind.TrainerApp.csproj -c Release -r win-x64 --self-contained false
```

Run:

```text
tools\Everwind.TrainerApp\bin\Release\net8.0-windows\win-x64\publish\Everwind.TrainerApp.exe
```

There is no packaged release build in this repository yet, so building from
source is currently the normal install path.

## How to use

1. Start Everwind normally with `Everwind.exe`.
2. Load into a world.
3. Open `Everwind.TrainerApp.exe`.
4. If the trainer does not find the game automatically, select or drag in your
   `Everwind.exe`.
5. Toggle features on or off in the trainer.

The trainer UI talks to `Everwind.RuntimeProbe`, a helper executable that is
copied beside the published app. Keep the published folder together; do not move
only `Everwind.TrainerApp.exe` by itself.

## Developer tools

### RuntimeProbe

`tools/Everwind.RuntimeProbe` is the external helper used by the UI. It is
read-only unless `--train` is passed.

Read-only examples:

```powershell
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --instances-only
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --player-stats
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --inventory-slots
```

Trainer examples:

```powershell
# Guard health and stamina until Ctrl+C
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --infinite-health --infinite-stamina --interval 250

# Apply multipliers once
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --damage 5 --block-damage 10 --xp 3 --once

# Movement/jump + no durability loss
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --speed 1.5 --jump 2 --no-durability-loss

# Raise only inventory slot 1 to at least 99 items once
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --item-amount 99 --once
```

### Analyzer

`tools\Everwind.Analyzer` is a small development utility for inspecting nearby
instructions in game executables while researching offsets. Normal trainer users
do not need it.

## Project layout

```text
tools/
  Everwind.TrainerApp/     WPF desktop app
  Everwind.RuntimeProbe/   External probe/trainer helper
  Everwind.Analyzer/       Developer analysis utility
```
