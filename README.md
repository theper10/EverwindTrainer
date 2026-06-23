# Everwind Trainer

Workspace for building a local Everwind trainer.

Current direction: **external-only trainer/probe**. The tool should attach to an
already-running Everwind process and should not copy DLLs, mods, or config files
into the game install.

Important safety notes:

- Start the game normally with:
  `%USERPROFILE%\Documents\games\Everwind\Everwind.exe`
- Do not use the shipping executable directly for normal play/testing.
- The old UE4SS injection installer is disabled by default because it caused
  Everwind to crash on launch during testing.
- Target scope is single-player and private, host-controlled sessions only.

## Current useful tool

`tools/Everwind.RuntimeProbe` is read-only by default. It does not modify the
Everwind install, and it only writes to game memory when `--train` is explicitly
passed. Its first job is to find Unreal runtime globals, starting with
`GUObjectArray`, in a running Everwind process.

Build it with:

```powershell
dotnet build .\tools\Everwind.RuntimeProbe\Everwind.RuntimeProbe.csproj -c Release
```

Run it only after Everwind has been started through `Everwind.exe`.

Compact live-instance scan:

```powershell
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --instances-only
```

Compact player-stat verification:

```powershell
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --player-stats
```

Compact inventory slot verification:

```powershell
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --inventory-slots
```

## Guarded external trainer mode

The same executable now has an explicit `--train` mode. It still does not copy
anything into the game install; it attaches to the already-running game process
and writes only the selected stat values. Do not use `--train` until the
read-only scan shows a live `BP_SkyverseCharacter_C_...` player component.

## Windows UI

The easier UI wrapper lives in `tools\Everwind.TrainerApp`.

Build/publish it with:

```powershell
dotnet publish .\tools\Everwind.TrainerApp\Everwind.TrainerApp.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Open:

```text
tools\Everwind.TrainerApp\bin\Release\net8.0-windows\win-x64\publish\Everwind.TrainerApp.exe
```

The app asks for Administrator permission so Windows allows process-memory
writes. It still uses the external RuntimeProbe helper and does not install or
copy anything into the Everwind game folder.

Examples:

```powershell
# Keep health and stamina full until Ctrl+C
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --infinite-health --infinite-stamina

# Add multipliers
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --damage 5 --block-damage 10 --xp 3

# Movement/jump + no durability loss
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --speed 1.5 --jump 2 --no-durability-loss

# Keep non-empty inventory slots at least at 99 items
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --item-amount 99

# Put the player-facing trainer stats back to the observed safe defaults
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --reset-player-defaults --once

# Re-enable normal durability usage without resetting other stats
.\tools\Everwind.RuntimeProbe\bin\Release\net8.0-windows\Everwind.RuntimeProbe.exe --train --durability 1 --once
```

Current implemented external features:

- Infinite health
- Infinite stamina
- Damage multiplier
- Block damage multiplier
- Experience gain multiplier
- Movement speed multiplier
- Jump boost multiplier
- No durability loss via the character durability-usage stat
- Non-empty inventory slot amount pinning with `--item-amount N`
- Reset known player trainer stats back to the observed safe defaults
- Set durability usage multiplier directly with `--durability N`

Not implemented yet:

- Invisibility
- True one-hit kills independent of damage multiplier
- Ignore crafting ingredients
- Generator/crafting fuel pinning
- Instant acceleration

## Legacy / unstable files

The `mod`, `runtime`, and UE4SS installer scripts are kept only as research
artifacts. `scripts/Install.ps1` now requires `-AllowUnstableInjection` and
should not be used for the normal trainer path.
