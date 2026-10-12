# Isaac Valley

**Play Stardew Valley as Isaac.** Isaac Valley links a live run of *The Binding of Isaac:
Afterbirth+* to Stardew Valley: you walk around the valley as Isaac, shoot his real tears,
drop his real bombs, and everything you pick up in Isaac changes how you fight in Stardew.

It works like [SkyCraft](https://github.com/chasmlol/SkyCraft) (Minecraft inside Skyrim):
in **engine mode**, Isaac's own engine runs your attacks in the background and Stardew draws
everything Isaac draws, frame by frame, from Isaac's own sprite files. Every item and synergy
works because Isaac itself is doing the work.

Works on **Windows** and **Mac**.

---

## What it does

- **You are Isaac.** Drawn with Isaac's own animations: walk cycle, head, crying, a costume
  for every item you hold, skin colors, Brimstone's charge face, and transformations
  (Guppy, Beelzebub, Fun Guy, Seraphim, Bob, Spun, Mom, Conjoined, Leviathan, Oh Crap,
  Bookworm, Adult, Spider Baby).
- **Shoot with the arrow keys.** Real tears with your run's damage, fire rate, shot speed,
  range and tear effects: homing, piercing, spectral, bouncing, Brimstone, Technology,
  Mom's Knife, Dr. Fetus and more.
- **Press E to drop a bomb.** It uses your Isaac bomb count, looks like your Isaac bombs
  (Hot, Sad, Butt, Bob's Curse, Glitter...) and breaks rocks in Stardew.
- **Shared hearts.** Isaac's hearts are your health. Get hit in Stardew and you lose hearts
  in your Isaac run, and the other way round.
- **Shared stats.** Speed and luck carry over. A panel shows hearts, coins, bombs, keys
  and all your stats.
- **Isaac's charge bar and sound effects.**

---

## What you need

| | |
|---|---|
| **Stardew Valley** 1.6 | on Steam (Windows or Mac) |
| **SMAPI** 4.0 or newer | the Stardew mod loader, free at [smapi.io](https://smapi.io) |
| **The Binding of Isaac: Afterbirth+** | on Steam. Shows up as *The Binding of Isaac: Rebirth* with the Afterbirth+ DLC. |

> **Repentance isn't supported yet.** If you own Repentance, you can switch back to
> Afterbirth+ in Steam: right-click the game → **Properties** → **DLC** → untick Repentance.

No Isaac or Stardew files are included in this mod. It loads Isaac's sprites and sounds
from **your own copy** of the game (step 2 below).

---

## Install

Download the two zips from the [**Releases**](../../releases) page:

- `IsaacValley-x.x.x-Stardew.zip`, the Stardew side
- `IsaacValleyBridge-x.x.x-Isaac.zip`, the Isaac side

### 1. Install SMAPI

Follow the guide at [smapi.io](https://smapi.io). Start Stardew once from Steam and check
that the SMAPI console window opens.

### 2. Unpack Isaac's sprites (one time)

Isaac comes with a free tool, **ResourceExtractor**, that unpacks the game's sprites and sounds.
Isaac Valley looks for them in a folder called **IsaacExtracted** in your Documents.

**Windows:** open the **Command Prompt** (press the Windows key, type `cmd`, press Enter) and
paste these two lines (press Enter after each):

```bat
set G=C:\Program Files (x86)\Steam\steamapps\common\The Binding of Isaac Rebirth
"%G%\tools\ResourceExtractor\ResourceExtractor.exe" "%G%" "%USERPROFILE%\Documents\IsaacExtracted"
```

(If Steam is installed somewhere else, change the first line. Steam → right-click the game
→ **Manage** → **Browse local files** shows you the folder.)

**Mac:** open **Terminal** and paste this one line:

```sh
G="$HOME/Library/Application Support/Steam/steamapps/common/The Binding of Isaac Rebirth"; mkdir -p ~/Documents/IsaacExtracted && "$G/tools/ResourceExtractor/ResourceExtractor" "$G"/*.app/Contents/Resources ~/Documents/IsaacExtracted
```

If macOS says ResourceExtractor "cannot be opened", go to **System Settings → Privacy &
Security**, click **Open Anyway**, and run the line again.

It unpacks about 600 MB and takes a minute. When it's done you'll have
`Documents/IsaacExtracted/resources/gfx`.

### 3. Install the Stardew side

Unzip `IsaacValley-x.x.x-Stardew.zip` into Stardew's **Mods** folder:

- **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods`
- **Mac:** `~/Library/Application Support/Steam/steamapps/common/Stardew Valley/Contents/MacOS/Mods`

(In Finder, press **Cmd + Shift + G** and paste the path. In Steam: right-click Stardew
→ **Manage** → **Browse local files**.)

You should end up with a `Mods/IsaacValley` folder.

### 4. Install the Isaac side

Unzip `IsaacValleyBridge-x.x.x-Isaac.zip` into Isaac's **mods** folder:

- **Windows:** `Documents\My Games\Binding of Isaac Afterbirth+ Mods`
- **Mac:** `~/Library/Application Support/Binding of Isaac Afterbirth+ Mods`

(Create the folder if it doesn't exist.) You should end up with an `IsaacValleyBridge`
folder inside it. Start Isaac, open **Mods**, and check that **Isaac Valley Bridge** is on.

### 5. Turn on engine mode (recommended)

Engine mode is the full SkyCraft-style link. Without it, Isaac Valley still works in
**classic mode** (stats, hearts, items and bombs are shared, but Stardew runs the attacks).

1. In Steam, right-click *The Binding of Isaac: Rebirth* → **Properties** → **Launch Options**
   and type:
   ```
   --luadebug
   ```
   This lets the bridge mod read and write its link file. It gives **every** Isaac mod
   file access, so only keep mods you trust turned on.
2. With Isaac closed, open Isaac's `options.ini` and change `PauseOnFocusLost=1` to
   `PauseOnFocusLost=0`, so Isaac keeps running while you play Stardew:
   - **Windows:** `Documents\My Games\Binding of Isaac Afterbirth+\options.ini`
   - **Mac:** `~/Library/Application Support/Binding of Isaac Afterbirth+/options.ini`

---

## Play

1. Start **Isaac** and begin a run. Walk into an **empty, cleared room** (bigger is better,
   since Isaac's tears still stop at Isaac's own room walls).
2. Start **Stardew** and load a save. You're Isaac now.
3. The panel in the top-left says **Isaac engine: ON** when engine mode is linked, or
   **Isaac linked** in classic mode.

| Key | |
|---|---|
| Arrow keys | Shoot |
| E | Drop a bomb (while linked; use **Esc** for the menu). Change it with `BombKey` in `config.json` |
| WASD | Walk, as usual |

Pick up items in Isaac (or use the console, e.g. `giveitem c118` for Brimstone), then
switch back to Stardew and see what changed.

> Mods turn off achievements in Isaac while they're on.
> Passing out in the Mines in Stardew loses items and gold as usual, and running out of
> hearts in Stardew kills Isaac.

---

## Troubleshooting

Open the SMAPI console window: Isaac Valley writes what it found and where it looked.

- **You still look like the farmer.** Isaac's sprites weren't found. Check step 2, or open
  `Mods/IsaacValley/config.json` and set `IsaacGfxFolder` to your `resources/gfx` folder.
- **"Isaac: bridge mod not found".** Check step 4, or set `BridgeFolder` in `config.json`
  to the full path of your `IsaacValleyBridge` folder.
- **"Isaac engine: off (classic)".** Check step 5, and make sure you're in a run.
  Isaac's screen shows the link status at the bottom.
- **The link drops when you switch windows.** `PauseOnFocusLost` is still on (step 5).
- **Something looks wrong.** Type `isaac_debug` in the SMAPI console. It logs what Isaac's
  engine reports. Open an issue with the log and a screenshot.

---

## Building from source

See [`IsaacValley/README.md`](IsaacValley/README.md). Short version: install the .NET 6 SDK,
then run `dotnet build` in the `IsaacValley` folder. To make the release zips, run
`make-release.bat` (Windows) or `make-release.command` (Mac).

A mod built on one system works on the others; SMAPI takes care of that.

## How it works

- **IsaacValleyBridge** (Lua, runs in Isaac) shares your stats, items, costumes and hearts
  through Isaac's save-data file. In engine mode it also reads `link_in.txt` from Stardew
  (where you aim, bombs, where you stand, where the monsters are), presses Isaac's buttons
  for you, places invisible stand-ins where the Stardew monsters are, and writes
  `link_out.txt` 30 times a second with everything Isaac draws and every hit.
- **IsaacValley** (C#, SMAPI) draws Isaac and everything in `link_out.txt` with Isaac's
  `.anm2` animations and sprite sheets, and applies the hits to Stardew's monsters.

## Credits

Made by Bertram, inspired by chasm's [SkyCraft](https://github.com/chasmlol/SkyCraft).
*The Binding of Isaac* is by Edmund McMillen and Nicalis. *Stardew Valley* is by
ConcernedApe. This is an unofficial fan mod; it includes no game files.

License: [MIT](LICENSE)
