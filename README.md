# Everwind Trainer

A community Windows trainer for **Everwind** with a modern desktop UI.

Download the release zip, extract it, start Everwind, then open the trainer and
toggle the features you want.

## Download

Get the latest Windows build from
[GitHub Releases](https://github.com/theper10/EverwindTrainer/releases).

Download:

```text
EverwindTrainer-*-win-x64.zip
```

Extract the zip and run:

```text
Everwind.TrainerApp.exe
```

Keep the extracted folder together. The trainer needs the included
`RuntimeProbe` folder beside the app.

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
- Pinned favorites section
- Game launcher with drag-and-drop path selection

Not implemented yet:

- Invisibility
- True one-hit kills independent of the damage multiplier
- Ignore crafting ingredients
- Generator/crafting fuel pinning
- Instant acceleration

## Requirements

- Windows x64
- A local Everwind install

The release build is self-contained, so you do not need to install the .NET SDK
unless you want to build the project from source.

## How to use

1. Start Everwind and load into a world.
2. Open `Everwind.TrainerApp.exe`.
3. If the trainer does not find the game automatically, click or drag your
   `Everwind.exe` into the path box.
4. Toggle features on or off.

## Troubleshooting

### Windows warns about the app

The trainer is not code-signed, so Windows SmartScreen may warn the first time
you run it. If you built or downloaded it from this repository, choose the option
to run it anyway.

### The trainer cannot find Everwind

Use the path box near the launch button to browse for `Everwind.exe`, or drag
`Everwind.exe` onto it.

### Features do not apply

Make sure you are loaded into a world, not sitting at the main menu. Some values
only exist after the player character has spawned.

If it still does not work, close the trainer and run it again as Administrator.

## Build from source

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
then run these commands from the repository root.

Build everything:

```powershell
dotnet build .\EverwindTrainer.slnx -c Release
```

Publish the desktop app:

```powershell
dotnet publish .\tools\Everwind.TrainerApp\Everwind.TrainerApp.csproj -c Release -r win-x64 --self-contained false
```

Run the published app:

```text
tools\Everwind.TrainerApp\bin\Release\net8.0-windows\win-x64\publish\Everwind.TrainerApp.exe
```

## Project layout

```text
tools/
  Everwind.TrainerApp/     WPF desktop app
  Everwind.RuntimeProbe/   Runtime helper used by the UI
  Everwind.Analyzer/       Developer analysis utility
```
