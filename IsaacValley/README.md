# Isaac Valley: Stardew side (building from source)

This is the SMAPI mod. To *play*, you don't need any of this; download the release zips
(see the main [README](../README.md)).

## What you need

- Stardew Valley with **SMAPI** installed ([smapi.io](https://smapi.io))
- The **.NET 6 SDK**: https://dotnet.microsoft.com/download/dotnet/6.0
  (Windows: x64. Mac: Arm64 for Apple Silicon, x64 for Intel.)
- Optional: an editor such as Visual Studio, VS Code with C# Dev Kit, or Rider

## Build

Open a terminal in this folder (`IsaacValley`) and run:

```
dotnet build
```

The build finds your Stardew folder by itself and copies the mod into `Stardew Valley/Mods/IsaacValley`.
If it says it can't find the game, open `IsaacValley.csproj` and set `<GamePath>`
(there are Windows and Mac examples in the file).

```
dotnet build -c Release
```

also makes `bin/Release/net6.0/IsaacValley <version>.zip`, ready to upload.
`make-release.bat` / `make-release.command` in the main folder do this for both mods.

## Files

| File | What it does |
|---|---|
| `ModEntry.cs` | Setup, config, the classic stats bridge, hearts, buffs, the HUD, drawing Isaac |
| `IsaacEngine.cs` | Engine mode: writes `link_in.txt`, reads Isaac's frame, draws Isaac's entities, applies hits |
| `EngineLink.cs` | Reading and writing the link files |
| `IsaacCombat.cs` | Classic mode attacks (tears, lasers, knife, bombs) and the charge bar |
| `Anm2.cs` | Loads and draws Isaac's `.anm2` animations |
| `PlayerRenderer.cs` | Draws Isaac with every costume and transformation, from `costumes2.xml` |
| `IsaacSprite.cs`, `IsaacSounds.cs` | Simple sprites and Isaac's sound effects |

## config.json

Created in `Mods/IsaacValley` the first time you start the game.

| Setting | Default | |
|---|---|---|
| `BridgeFolder` | empty (found automatically) | the `IsaacValleyBridge` folder in Isaac's mods folder |
| `IsaacGfxFolder` | empty (found automatically) | your extracted `resources/gfx` folder |
| `PlayAsIsaac` | `true` | draw Isaac instead of your farmer |
| `UseIsaacEngine` | `true` | use engine mode when Isaac is linked with `--luadebug` |
| `BombKey` | `E` | the key that drops a bomb |

## Things to tweak

In the code: `DamageMultiplier` (how Isaac's damage turns into Stardew damage),
`TearDamage`, `TearSpeed`, `FireDelayTicks` (defaults when Isaac isn't linked),
`HealthPerHeart`.

## Console commands

- `isaac_debug` logs everything Isaac's engine reports, every tick. Type it again to stop.
