# Isaac Valley Bridge (Isaac side)

The Afterbirth+ Lua mod that links your Isaac run to Stardew Valley.
Install instructions are in the main [README](../README.md).

**Classic mode** (always on): once a second it saves your stats, hearts, coins, bombs, keys,
items, tear effects, costumes and transformations to `save1.dat` in this folder for Stardew,
and applies the damage you took and the bombs you used in Stardew to your Isaac run.
You'll see a `bridge: dmg 3.5 tears 10 ...` line at the bottom of Isaac's screen.

**Engine mode** (needs `--luadebug` in Isaac's Steam launch options): 30 times a second it
reads `link_in.txt` (written by Stardew), presses Isaac's shoot and bomb buttons for you,
keeps Isaac standing where you stand in Stardew, places invisible stand-ins where the
Stardew monsters are, and writes `link_out.txt` with every hit and everything Isaac draws.
The second status line at the bottom of Isaac's screen shows whether it's linked.

Mod folder:
- Windows: `Documents\My Games\Binding of Isaac Afterbirth+ Mods\IsaacValleyBridge`
- Mac: `~/Library/Application Support/Binding of Isaac Afterbirth+ Mods/IsaacValleyBridge`

Note: mods turn off achievements in Isaac while they're enabled.
