# Everwind Trainer

External WPF trainer for Everwind. The trainer launches or attaches to the game,
then uses a separate runtime probe process to apply selected single-player
features.

The current supported path is external-only: no DLL injection, no UE4SS install,
and no files copied into the Everwind game folder.

## Safety notes

- Start the game normally with:
  `%USERPROFILE%\Documents\games\Everwind\Everwind.exe`
- Do not launch the shipping executable directly for normal play/testing.
- Keep this to single-player or private, host-controlled sessions.
- The trainer asks for Administrator permission because Windows requires it for
  process-memory writes.

## Build

Build everything:

```powershell
dotnet build .\EverwindTrainer.slnx -c Release
```

Publish the Windows app:

```powershell
dotnet publish .\tools\Everwind.TrainerApp\Everwind.TrainerApp.csproj -c Release -r win-x64 --self-contained false
```

Run the published app:

```text
tools\Everwind.TrainerApp\bin\Release\net8.0-windows\win-x64\publish\Everwind.TrainerApp.exe
```

## Project layout

- `tools/Everwind.TrainerApp` - WPF desktop UI.
- `tools/Everwind.RuntimeProbe` - external process-memory probe/trainer helper.
- `tools/Everwind.Analyzer` - developer utility for inspecting executable code
  and nearby instructions while researching offsets.

## Implemented trainer features

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
- Pinning favorite features into a collapsible pinned section

## RuntimeProbe CLI

`Everwind.RuntimeProbe` is read-only unless `--train` is passed.

Read-only checks:

```powershell
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --instances-only
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --player-stats
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --inventory-slots
```

Trainer examples:

```powershell
# Keep health and stamina guarded until Ctrl+C
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --infinite-health --infinite-stamina --interval 250

# Apply multipliers once
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --damage 5 --block-damage 10 --xp 3 --once

# Movement/jump + no durability loss
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --speed 1.5 --jump 2 --no-durability-loss

# Raise only inventory slot 1 to at least 99 items once
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --item-amount 99 --once
```

## Not implemented

- Invisibility
- True one-hit kills independent of the damage multiplier
- Ignore crafting ingredients
- Generator/crafting fuel pinning
- Instant acceleration
