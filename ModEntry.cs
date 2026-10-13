using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;

namespace IsaacValley
{
    /// <summary>Settings players can change in config.json (created on first launch).</summary>
    internal sealed class ModConfig
    {
        /// <summary>Folder of the Isaac Valley Bridge mod. Leave empty to find it automatically (Windows, Mac or Linux).</summary>
        public string BridgeFolder { get; set; } = "";

        /// <summary>The "gfx" folder of your extracted Isaac resources. Leave empty to look in Documents/IsaacExtracted/resources/gfx.</summary>
        public string IsaacGfxFolder { get; set; } = "";

        /// <summary>Draw Isaac instead of your farmer.</summary>
        public bool PlayAsIsaac { get; set; } = true;

        /// <summary>Let Isaac's own engine run your attacks when it's linked (needs --luadebug in Isaac).</summary>
        public bool UseIsaacEngine { get; set; } = true;

        /// <summary>The key that drops an Isaac bomb (only while linked to Isaac).</summary>
        public SButton BombKey { get; set; } = SButton.E;
    }

    /// <summary>Isaac's stats, as read from the bridge file.</summary>
    internal sealed class IsaacStats
    {
        public float Damage = 3.5f;
        public float FireDelay = 10f;
        public float ShotSpeed = 1f;
        public float MoveSpeed = 1f;
        public float Range = 23.75f;
        public float Luck = 0f;
        public int RedHearts = 6;     // in half hearts
        public int MaxHearts = 6;     // in half hearts
        public int SoulHearts = 0;    // in half hearts
        public int Coins = 0;
        public int Bombs = 1;
        public int Keys = 0;
        public int AppliedDamage = 0; // how much of Stardew's damage Isaac has applied so far
        public int AppliedBombs = 0;  // how many of Stardew's bombs Isaac has taken off its count

        /// <summary>Active tear effects, straight from Isaac (HOMING, PIERCING, POISON, ...).</summary>
        public HashSet<string> TearFlags = new();
        /// <summary>Attack-changing items and how many of each (BRIMSTONE, THE_INNER_EYE, ...).</summary>
        public Dictionary<string, int> Items = new();
        public Color TearColor = Color.White;
        public Color? LaserColor;     // null if Isaac didn't say

        /// <summary>Every item Isaac holds (item id -> how many), for drawing costumes.</summary>
        public Dictionary<int, int> Collectibles = new();
        public HashSet<int> Forms = new();   // transformations: 0 Guppy, 1 Beelzebub, 2 Fun Guy, ...
        public int? BodySkin, HeadSkin; // Isaac's SkinColor values, if Isaac told us

        public bool Has(string flag) => this.TearFlags.Contains(flag);
        public int Count(string item) => this.Items.TryGetValue(item, out int n) ? n : 0;
    }

    /// <summary>
    /// Isaac Valley: you ARE Isaac in Stardew.
    /// - You're drawn as Isaac (from your own extracted Isaac files).
    /// - Arrow keys shoot real Isaac tears using all of Isaac's tear stats.
    /// - E drops an Isaac bomb, using your real Isaac bomb count.
    /// - Isaac's hearts are your health: getting hit in Stardew costs hearts in your Isaac run.
    /// - You move at Isaac's speed, and Isaac's luck is your luck.
    /// </summary>
    internal sealed partial class ModEntry : Mod
    {
        // ---------- Default tear settings (used when Isaac isn't linked) ----------
        private const int TearDamage = 10;
        private const float TearSpeed = 10f;
        private const int FireDelayTicks = 20;
        private const float TearRangePixels = 7 * 64;  // about 7 tiles
        private const int HealthPerHeart = 20;         // Stardew health per heart when Isaac isn't linked

        // ---------- How Isaac's stats turn into Stardew numbers ----------
        private const float DamageMultiplier = 3f;     // Isaac 3.5 damage -> about 10 in Stardew
        private const float SpeedPerIsaacSpeed = 4f;   // Isaac speed 1.5 -> +2 Stardew speed
        private const float BaseIsaacRange = 23.75f;   // Isaac's starting range = TearRangePixels
        private const int BigHitDamage = 30;           // Stardew hits this strong cost a full heart
        private const double LinkTimeoutSeconds = 5;
        private const string StatsBuffId = "IsaacValley.Stats";

        // ---------- Bombs ----------
        private const int BombFuseTicks = 90;          // 1.5 seconds, like Isaac
        private const int BombRadius = 3;              // tiles, like a Stardew cherry bomb

        internal static ModEntry Instance = null!;

        private ModConfig config = new();
        private readonly IsaacSprite sprite = new();
        private PlayerRenderer? renderer;          // draws Isaac + item costumes from Isaac's own animation files
        private IsaacFiles? files;                 // Isaac's animation files and sprites
        private IsaacSounds? sounds;               // Isaac's sound effects
        private bool debugEngine;                  // isaac_debug console command
        private IsaacStats? isaac;                 // null = not linked
        private string linkStatus = "Isaac: not linked";
        private bool wasLive = false;
        private string? bridgeFile;                // the save*.dat file we're talking through

        // things Stardew sends to Isaac (running totals, so nothing gets lost or applied twice)
        private int stardewDamageTotal = 0;        // half hearts of damage taken in Stardew
        private int stardewBombsTotal = 0;         // bombs used in Stardew
        private bool countersLoaded = false;

        // two-way health
        private int lastHealth = -1;
        private bool deadUntilNewRun = false;

        // animation
        private int shotDirection = 2;
        private uint lastShotTick = 0;
        private uint tick = 0;
        private (float speed, int luck) appliedBuff = (float.NaN, int.MinValue);

        /*********
        ** Setup
        *********/
        public override void Entry(IModHelper helper)
        {
            Instance = this;
            this.config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.SaveLoaded += (_, _) => this.ResetRunState();
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.Player.Warped += (_, _) => this.OnWarped();
            helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            helper.Events.Display.RenderedHud += this.OnRenderedHud;

            // draw Isaac instead of the farmer
            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(Farmer), nameof(Farmer.draw), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(FarmerDrawPrefix))
            );

            this.SetUpEngine();
            helper.ConsoleCommands.Add("isaac_debug", "Logs what Isaac's engine says about Isaac every tick (type again to stop).", (_, _) =>
            {
                this.debugEngine = !this.debugEngine;
                this.Monitor.Log($"Isaac engine debug log is {(this.debugEngine ? "on" : "off")}.", LogLevel.Info);
            });
            this.Monitor.Log($"Isaac Valley loaded. Looking for Isaac in: {this.GetBridgeFolder()}", LogLevel.Info);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            string gfx = this.GetGfxFolder();
            string? error = this.sprite.Load(gfx);
            if (error != null)
                this.Monitor.Log($"{error}. You'll look like your normal farmer. Set IsaacGfxFolder in config.json if your files are somewhere else.", LogLevel.Warn);
            else
                this.Monitor.Log($"Loaded Isaac's sprites from {gfx}", LogLevel.Info);

            try
            {
                this.files = new IsaacFiles(gfx);
                this.renderer = new PlayerRenderer(this.files);
                if (this.renderer.Ready)
                    this.Monitor.Log($"Loaded Isaac's animations and {this.renderer.CostumeCount} item costumes.", LogLevel.Info);
                else
                    this.Monitor.Log("Couldn't find 001.000_player.anm2; using the simple Isaac sprite.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't load Isaac's animations, using the simple Isaac sprite: {ex.Message}", LogLevel.Warn);
                this.renderer = null;
            }

            this.sounds = new IsaacSounds(gfx, this.Monitor);
            this.Monitor.Log(this.sounds.Available ? "Using Isaac's sound effects." : "Isaac's sfx folder not found; using Stardew sounds.", LogLevel.Info);
        }

        private void ResetRunState()
        {
            this.lastHealth = -1;
            this.deadUntilNewRun = false;
            this.appliedBuff = (float.NaN, int.MinValue);
            this.ResetCombat();
        }

        private string? bridgeFolderCache;
        private string? gfxFolderCache;

        /// <summary>Your Documents folder (on Windows it can be moved, e.g. into OneDrive).</summary>
        private static string DocumentsFolder()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return string.IsNullOrEmpty(docs) ? Path.Combine(home, "Documents") : docs;
            }
            return Path.Combine(home, "Documents");
        }

        /// <summary>Isaac's Steam folders on this computer (for mods and extracted files kept there).</summary>
        private static IEnumerable<string> IsaacGameFolders()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                yield return Path.Combine(x86, "Steam", "steamapps", "common", "The Binding of Isaac Rebirth");
                yield return @"C:\Program Files\Steam\steamapps\common\The Binding of Isaac Rebirth";
            }
            else if (OperatingSystem.IsMacOS())
                yield return Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "The Binding of Isaac Rebirth");
            else
                yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "The Binding of Isaac Rebirth");
        }

        /// <summary>Where Isaac keeps Afterbirth+ mods on this kind of computer, best guess first.</summary>
        private static IEnumerable<string> BridgeFolderCandidates()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            const string mods = "Binding of Isaac Afterbirth+ Mods";
            if (OperatingSystem.IsWindows())
                yield return Path.Combine(DocumentsFolder(), "My Games", mods, "IsaacValleyBridge");
            else if (OperatingSystem.IsMacOS())
                yield return Path.Combine(home, "Library", "Application Support", mods, "IsaacValleyBridge");
            else
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is string x && x.Length > 0 ? x : Path.Combine(home, ".local", "share");
                yield return Path.Combine(data, mods.ToLowerInvariant(), "IsaacValleyBridge");
                yield return Path.Combine(data, mods, "IsaacValleyBridge");
            }
            foreach (string game in IsaacGameFolders())
                yield return Path.Combine(game, "mods", "IsaacValleyBridge");
        }

        private string GetBridgeFolder()
        {
            if (!string.IsNullOrWhiteSpace(this.config.BridgeFolder))
                return this.config.BridgeFolder;
            if (this.bridgeFolderCache != null)
                return this.bridgeFolderCache;
            List<string> candidates = BridgeFolderCandidates().ToList();
            string? found = candidates.FirstOrDefault(Directory.Exists);
            if (found != null)
                this.bridgeFolderCache = found; // keep looking until the bridge is installed
            return found ?? candidates[0];
        }

        private string GetGfxFolder()
        {
            if (!string.IsNullOrWhiteSpace(this.config.IsaacGfxFolder))
                return this.config.IsaacGfxFolder;
            if (this.gfxFolderCache != null)
                return this.gfxFolderCache;

            var candidates = new List<string>
            {
                Path.Combine(DocumentsFolder(), "IsaacExtracted", "resources", "gfx"),
                Path.Combine(DocumentsFolder(), "IsaacExtracted", "gfx")
            };
            foreach (string game in IsaacGameFolders())
            {
                candidates.Add(Path.Combine(game, "extracted_resources", "resources", "gfx"));
                candidates.Add(Path.Combine(game, "extracted_resources", "gfx"));
                candidates.Add(Path.Combine(game, "tools", "ResourceExtractor", "resources", "gfx"));
            }
            // the real thing has Isaac's player animation in it
            this.gfxFolderCache = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "001.000_player.anm2"))) ?? candidates[0];
            return this.gfxFolderCache;
        }

        /*********
        ** Every tick
        *********/
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            this.tick = e.Ticks;

            if (e.IsMultipleOf(30))
                this.ReadBridge();

            if (!Context.IsWorldReady)
                return;

            this.SyncEngine();
            if (this.debugEngine && this.engine?.Frame?.Player is EnginePlayer debugPlayer)
                this.Monitor.Log($"[tick {e.Ticks}] charge {this.chargeTicks} | player={debugPlayer.Raw}", LogLevel.Info);
            this.UpdateHealth();
            this.UpdateBuff();
            this.UpdateCombat();
        }

        /*********
        ** The bridge (Isaac <-> Stardew file)
        *********/
        private void ReadBridge()
        {
            string folder = this.GetBridgeFolder();
            if (!Directory.Exists(folder))
            {
                this.SetUnlinked("Isaac: bridge mod not found");
                return;
            }

            // Isaac writes save1.dat, save2.dat or save3.dat depending on the save slot: use the newest
            FileInfo? file = new DirectoryInfo(folder)
                .GetFiles("save*.dat")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (file == null)
            {
                this.SetUnlinked("Isaac: start a run to link");
                return;
            }
            this.bridgeFile = file.FullName;

            // Isaac pauses itself when its window isn't in front, so the file stops updating.
            // That's fine: its stats can't change while paused, so we keep using the last ones.
            bool live = (DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds <= LinkTimeoutSeconds;

            string? text = ReadShared(file.FullName);
            if (text == null)
                return; // try again next time

            Dictionary<string, float> values = ParseValues(text);
            if (!values.ContainsKey("damage"))
                return; // half-written file; try again next time

            var stats = new IsaacStats();
            if (values.TryGetValue("damage", out float v)) stats.Damage = v;
            if (values.TryGetValue("fireDelay", out v)) stats.FireDelay = v;
            if (values.TryGetValue("shotSpeed", out v)) stats.ShotSpeed = v;
            if (values.TryGetValue("moveSpeed", out v)) stats.MoveSpeed = v;
            if (values.TryGetValue("range", out v)) stats.Range = v;
            if (values.TryGetValue("luck", out v)) stats.Luck = v;
            if (values.TryGetValue("redHearts", out v)) stats.RedHearts = (int)v;
            if (values.TryGetValue("maxHearts", out v)) stats.MaxHearts = (int)v;
            if (values.TryGetValue("soulHearts", out v)) stats.SoulHearts = (int)v;
            if (values.TryGetValue("coins", out v)) stats.Coins = (int)v;
            if (values.TryGetValue("bombs", out v)) stats.Bombs = (int)v;
            if (values.TryGetValue("keys", out v)) stats.Keys = (int)v;
            if (values.TryGetValue("appliedDamage", out v)) stats.AppliedDamage = (int)v;
            if (values.TryGetValue("appliedBombs", out v)) stats.AppliedBombs = (int)v;

            // items and tear effects (text values)
            Dictionary<string, string> raw = ParseRaw(text);
            if (raw.TryGetValue("tearFlags", out string? flags))
                stats.TearFlags = new HashSet<string>(flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (raw.TryGetValue("items", out string? items))
            {
                foreach (string entry in items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = entry.Split(':');
                    stats.Items[parts[0]] = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 1;
                }
            }
            if (raw.TryGetValue("tearColor", out string? color) && ParseColor(color) is Color tearColor)
                stats.TearColor = tearColor;
            if (raw.TryGetValue("laserColor", out string? laser))
                stats.LaserColor = ParseColor(laser);
            if (raw.TryGetValue("collectibles", out string? owned))
            {
                foreach (string entry in owned.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] parts = entry.Split(':');
                    if (int.TryParse(parts[0], out int id))
                        stats.Collectibles[id] = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 1;
                }
            }
            if (raw.TryGetValue("forms", out string? forms))
            {
                foreach (string entry in forms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(entry, out int form))
                        stats.Forms.Add(form);
            }
            if (raw.TryGetValue("skin", out string? skin))
            {
                string[] parts = skin.Split(';');
                if (parts.Length == 2 && int.TryParse(parts[0], out int bodySkin) && int.TryParse(parts[1], out int headSkin))
                {
                    stats.BodySkin = bodySkin;
                    stats.HeadSkin = headSkin;
                }
            }

            // the first time, continue Stardew's counters from the file (so restarting Stardew doesn't reset them)
            int fileDamage = values.TryGetValue("stardewDamage", out v) ? (int)v : 0;
            int fileBombs = values.TryGetValue("stardewBombs", out v) ? (int)v : 0;
            if (!this.countersLoaded)
            {
                this.stardewDamageTotal = Math.Max(this.stardewDamageTotal, Math.Max(fileDamage, stats.AppliedDamage));
                this.stardewBombsTotal = Math.Max(this.stardewBombsTotal, Math.Max(fileBombs, stats.AppliedBombs));
                this.countersLoaded = true;
            }

            // Isaac rewrites the file every second; make sure our lines are still in it
            if (fileDamage < this.stardewDamageTotal || fileBombs < this.stardewBombsTotal)
                this.WriteCountersToBridge();

            if (this.isaac == null || live != this.wasLive)
                this.Monitor.Log(live ? "Linked to Isaac!" : "Isaac is paused; using its last stats.", LogLevel.Info);

            this.isaac = stats;
            this.wasLive = live;
            this.linkStatus = live ? "Isaac linked" : "Isaac paused (last stats)";
        }

        private void SetUnlinked(string status)
        {
            if (this.isaac != null)
                this.Monitor.Log("Lost the link to Isaac.", LogLevel.Info);
            this.isaac = null;
            this.linkStatus = status;
        }

        private static string? ReadShared(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static Dictionary<string, float> ParseValues(string text)
        {
            var values = new Dictionary<string, float>();
            foreach (string line in text.Split('\n'))
            {
                string[] parts = line.Trim().Split('=');
                if (parts.Length != 2)
                    continue;
                string number = parts[1].Replace(',', '.'); // in case Isaac uses a comma decimal
                if (float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    values[parts[0]] = value;
            }
            return values;
        }

        /// <summary>Parse "r;g;b" (0 to 1) into a color.</summary>
        private static Color? ParseColor(string text)
        {
            float[] rgb = text.Split(';')
                .Select(p => float.TryParse(p.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float c) ? c : float.NaN)
                .ToArray();
            if (rgb.Length != 3 || rgb.Any(float.IsNaN))
                return null;
            return new Color(Math.Clamp(rgb[0], 0f, 1f), Math.Clamp(rgb[1], 0f, 1f), Math.Clamp(rgb[2], 0f, 1f));
        }

        private static Dictionary<string, string> ParseRaw(string text)
        {
            var values = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return values;
        }

        /// <summary>Put our "stardewDamage=N" and "stardewBombs=N" lines into the bridge file, keeping everything Isaac wrote.</summary>
        private void WriteCountersToBridge()
        {
            if (this.bridgeFile == null)
                return;

            try
            {
                string text = ReadShared(this.bridgeFile) ?? "";
                text = SetLine(text, "stardewDamage", this.stardewDamageTotal);
                text = SetLine(text, "stardewBombs", this.stardewBombsTotal);
                File.WriteAllText(this.bridgeFile, text);
            }
            catch (IOException)
            {
                // Isaac was writing at the same moment; ReadBridge will try again in half a second
            }
        }

        private static string SetLine(string text, string key, int value)
        {
            string pattern = $@"(?m)^{key}=\d+\s*$";
            string line = $"{key}={value}";
            return Regex.IsMatch(text, pattern)
                ? Regex.Replace(text, pattern, line)
                : text.TrimEnd('\n', '\r') + "\n" + line;
        }

        /*********
        ** Isaac's hearts are your health
        *********/
        /// <summary>Isaac's hearts after the damage Stardew has sent but Isaac hasn't applied yet (Isaac may be paused).</summary>
        private (int red, int soul) GetEffectiveHearts()
        {
            if (this.isaac == null)
                return (0, 0);

            int pending = Math.Max(0, this.stardewDamageTotal - this.isaac.AppliedDamage);
            int soul = Math.Max(0, this.isaac.SoulHearts - pending);
            int spill = Math.Max(0, pending - this.isaac.SoulHearts);
            int red = Math.Max(0, this.isaac.RedHearts - spill);
            return (red, soul);
        }

        private void UpdateHealth()
        {
            Farmer player = Game1.player;

            if (this.isaac == null || !this.countersLoaded)
            {
                this.lastHealth = -1; // not linked: normal Stardew health
                return;
            }

            (int red, int soul) = this.GetEffectiveHearts();
            bool isaacAlive = red + soul > 0;

            // after you die, wait for a new Isaac run (or healing) before managing health again
            if (this.deadUntilNewRun)
            {
                if (isaacAlive)
                    this.deadUntilNewRun = false;
                this.lastHealth = -1;
                return;
            }

            // a hit in Stardew = half a heart in Isaac (a full heart for big hits, like your own bombs)
            if (this.lastHealth > 0 && player.health < this.lastHealth)
            {
                int lost = this.lastHealth - player.health;
                this.stardewDamageTotal += lost >= BigHitDamage ? 2 : 1;
                this.WriteCountersToBridge();
                this.PlaySfx("Hurt", "ow");
                (red, soul) = this.GetEffectiveHearts();
                isaacAlive = red + soul > 0;
            }

            if (!isaacAlive)
            {
                // out of hearts: you pass out in Stardew, Isaac dies when you switch back to it
                this.Monitor.Log("Isaac is out of hearts!", LogLevel.Info);
                player.health = 0;
                this.deadUntilNewRun = true;
                this.lastHealth = -1;
                return;
            }

            // Stardew's own health bar stays full; Isaac's hearts decide when you die
            player.health = player.maxHealth;
            this.lastHealth = player.health;
        }

        /*********
        ** Isaac's speed and luck
        *********/
        private void UpdateBuff()
        {
            Farmer player = Game1.player;
            float speed = this.isaac == null ? 0f : (float)Math.Round((this.isaac.MoveSpeed - 1f) * SpeedPerIsaacSpeed, 1);
            int luck = this.isaac == null ? 0 : (int)Math.Round(this.isaac.Luck);

            if (speed == 0f && luck == 0)
            {
                if (player.hasBuff(StatsBuffId))
                    player.buffs.Remove(StatsBuffId);
                this.appliedBuff = (0f, 0);
                return;
            }

            if (this.appliedBuff == (speed, luck) && player.hasBuff(StatsBuffId))
                return;

            var effects = new BuffEffects();
            effects.Speed.Value = speed;
            effects.LuckLevel.Value = luck;
            player.applyBuff(new Buff(
                id: StatsBuffId,
                displayName: "Isaac's stats",
                duration: Buff.ENDLESS,
                effects: effects
            ));
            this.appliedBuff = (speed, luck);
        }

        /*********
        ** Drawing Isaac
        *********/
        /// <summary>Runs instead of Stardew's farmer drawing for the local player.</summary>
        private static bool FarmerDrawPrefix(Farmer __instance, SpriteBatch b)
        {
            try
            {
                ModEntry mod = Instance;
                if (!mod.config.PlayAsIsaac || !mod.sprite.Loaded || !__instance.IsLocalPlayer)
                    return true; // draw the normal farmer

                bool walking = __instance.isMoving();
                int walkFrame = (int)(mod.tick / 4 % 10);

                // Isaac looks where he's shooting, and cries for a moment after each tear
                uint sinceAim = mod.tick - mod.lastAimTick;
                uint sinceShot = mod.tick - mod.lastShotTick;
                int headFacing = sinceAim < 20 ? mod.shotDirection : __instance.FacingDirection;
                if (mod.EngineLive && mod.EngineHeadDirection() is int engineHead && engineHead >= 0)
                    headFacing = engineHead; // Isaac's engine knows where he's looking
                bool crying = sinceShot < 8;

                Vector2 feet = __instance.getStandingPosition() + new Vector2(0, 8);

                // the full version: Isaac's own animations with every item costume
                if (mod.renderer?.Ready == true)
                {
                    mod.DrawWithCostumes(b, __instance, feet, walking, headFacing, crying);
                    return false;
                }

                mod.sprite.DrawIsaac(b, feet, __instance.FacingDirection, headFacing, walking, walkFrame, crying, 1f);
                return false; // skip the normal farmer
            }
            catch (Exception ex)
            {
                Instance.Monitor.LogOnce($"Couldn't draw Isaac, drawing the farmer instead: {ex}", LogLevel.Error);
                return true;
            }
        }

        private static readonly string[] DirNames = { "Up", "Right", "Down", "Left" };

        /// <summary>Draw Isaac like the game does: walk cycle, head direction, crying, charge faces and item costumes.</summary>
        private void DrawWithCostumes(SpriteBatch b, Farmer farmer, Vector2 feetWorld, bool walking, int headFacing, bool crying)
        {
            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, feetWorld);
            float depth = Math.Max(0f, feetWorld.Y / 10000f);

            // shadow
            Rectangle shadow = Game1.shadowTexture.Bounds;
            b.Draw(Game1.shadowTexture, feet, shadow, Color.White, 0f,
                new Vector2(shadow.Center.X, shadow.Center.Y), 3f, SpriteEffects.None, Math.Max(0f, depth - 0.0001f));

            // body: Isaac stands on the first frame of WalkDown, and walks in the direction he's moving
            string bodyAnim = walking ? "Walk" + DirNames[farmer.FacingDirection % 4] : "WalkDown";
            int bodyTime = walking ? (int)(this.tick / 2) : 0;

            // head: Brimstone-style costumes have charging and shooting faces
            HeadMode mode = HeadMode.Normal;
            int modeTime = 0;
            Beam? brimstone = this.beams.FirstOrDefault(p => p.Thick);
            if (brimstone != null)
            {
                mode = HeadMode.Shoot;
                modeTime = (brimstone.Duration - brimstone.TicksLeft) / 2;
            }
            else if (this.EngineBrimstoneFiring)
            {
                mode = HeadMode.Shoot;
                modeTime = (int)(this.tick / 2);
            }
            else if (this.chargeTicks > 0 && this.GetWeapon() == Weapon.Brimstone)
            {
                int max = this.GetChargeMax();
                mode = this.chargeTicks >= max ? HeadMode.ChargeFull : HeadMode.Charge;
                modeTime = this.chargeTicks >= max ? (int)(this.tick / 2) : this.chargeTicks / 2;
            }

            List<CostumeInfo> worn = this.isaac != null
                ? this.renderer!.CostumesFor(this.isaac.Collectibles.Keys, this.isaac.Forms)
                : new List<CostumeInfo>();
            int bodySkin = this.isaac?.BodySkin ?? PlayerRenderer.SkinFromCostumes(worn);
            int headSkin = this.isaac?.HeadSkin ?? bodySkin;

            // engine mode: Isaac's own animation, frame for frame
            EnginePlayer? exact = this.EngineLive ? this.engine!.Frame!.Player : null;
            if (exact != null)
            {
                this.knownAnimFiles.Add(exact.File); // so Isaac can tell us the animation names
                if (!string.IsNullOrEmpty(exact.BodyAnim) && exact.BodyAnim.StartsWith("Walk", StringComparison.OrdinalIgnoreCase))
                {
                    // Isaac doesn't walk in its own room (Stardew moves him), so walk while you walk in Stardew
                    bodyTime = walking ? exact.BodyFrame + (int)(this.tick / 2) : 0;
                }
                else if (!string.IsNullOrEmpty(exact.BodyAnim))
                {
                    bodyAnim = exact.BodyAnim;   // pickup, hurt, and other whole-body animations
                    bodyTime = exact.BodyFrame;
                }
            }

            this.renderer!.Draw(b, feet, IsaacSprite.Scale, depth, bodyAnim, bodyTime,
                DirNames[headFacing % 4], crying, mode, modeTime, worn, bodySkin, headSkin,
                exact?.HeadAnim, exact?.HeadFrame ?? 0, exact?.Tint);
        }

        /*********
        ** HUD
        *********/
        private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            SpriteBatch b = e.SpriteBatch;
            bool linked = this.isaac != null && this.countersLoaded;

            // ---------- hearts (in half hearts) ----------
            int containers, red, soul;
            if (linked)
            {
                (red, soul) = this.GetEffectiveHearts();
                containers = (this.isaac!.MaxHearts + 1) / 2;
            }
            else
            {
                Farmer player = Game1.player;
                containers = (int)Math.Ceiling(player.maxHealth / (float)HealthPerHeart);
                red = (int)Math.Ceiling(player.health / (HealthPerHeart / 2f));
                soul = 0;
            }

            var hearts = new List<Rectangle>();
            for (int i = 0; i < containers; i++)
            {
                int left = red - i * 2;
                hearts.Add(left >= 2 ? IsaacSprite.RedFull : left == 1 ? IsaacSprite.RedHalf : IsaacSprite.Empty);
            }
            for (int i = 0; i < (soul + 1) / 2; i++)
                hearts.Add(soul - i * 2 >= 2 ? IsaacSprite.SoulFull : IsaacSprite.SoulHalf);

            // ---------- text lines under the hearts ----------
            var lines = new List<(string text, Color color)>();
            if (linked)
            {
                IsaacStats s = this.isaac!;
                lines.Add(($"Speed {s.MoveSpeed:0.00}   Tears {s.FireDelay:0.#}   Damage {s.Damage:0.00}", Color.White));
                lines.Add(($"Range {s.Range:0.00}   Shot speed {s.ShotSpeed:0.00}   Luck {s.Luck:0.#}", Color.White));
                lines.Add((this.GetItemsLine(), new Color(255, 220, 140)));
            }
            string mode = this.EngineLive ? "  |  Isaac engine: ON" : (this.config.UseIsaacEngine ? "  |  Isaac engine: off (classic)" : "");
            lines.Add((this.linkStatus + mode, linked ? Color.LightGreen : Color.White));

            // ---------- layout ----------
            const int perRow = 6;
            const int left0 = 16, top0 = 16;
            int heartSize = (int)(12 * IsaacSprite.HeartScale); // Isaac's hearts sit 12px apart
            int heartRows = Math.Max(1, (hearts.Count + perRow - 1) / perRow);
            int pickupsTop = top0 + heartRows * heartSize + 6;
            int pickupRowHeight = linked ? 36 : 0;
            int textTop = pickupsTop + pickupRowHeight + 4;
            int lineHeight = (int)Game1.smallFont.MeasureString("A").Y;

            float textWidth = lines.Max(l => Game1.smallFont.MeasureString(l.text).X);
            int panelWidth = (int)Math.Max(Math.Max(Math.Min(hearts.Count, perRow) * heartSize, textWidth), 3 * 110) + 24;
            int panelHeight = textTop + lines.Count * lineHeight + 4;
            b.Draw(Game1.staminaRect, new Rectangle(4, 4, panelWidth, panelHeight), Color.Black * 0.6f);

            // hearts
            for (int i = 0; i < hearts.Count; i++)
            {
                Vector2 pos = new(left0 + (i % perRow) * heartSize, top0 + (i / perRow) * heartSize);
                if (this.sprite.Hearts != null)
                {
                    b.Draw(this.sprite.Hearts, pos, hearts[i], Color.White, 0f, Vector2.Zero, IsaacSprite.HeartScale, SpriteEffects.None, 1f);
                }
                else
                {
                    Color color = hearts[i] == IsaacSprite.Empty ? Color.Black * 0.35f
                        : hearts[i] == IsaacSprite.SoulFull || hearts[i] == IsaacSprite.SoulHalf ? Color.CornflowerBlue
                        : Color.White;
                    b.Draw(Game1.mouseCursors, pos, new Rectangle(211, 428, 7, 6), color, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
                }
            }

            // coins, bombs, keys
            if (linked)
            {
                var pickups = new (Rectangle icon, int count)[]
                {
                    (IsaacSprite.CoinIcon, this.isaac!.Coins),
                    (IsaacSprite.BombIcon, this.GetEffectiveBombs()),
                    (IsaacSprite.KeyIcon, this.isaac!.Keys)
                };
                for (int i = 0; i < pickups.Length; i++)
                {
                    Vector2 pos = new(left0 + i * 110, pickupsTop);
                    if (this.sprite.Pickups != null)
                        b.Draw(this.sprite.Pickups, pos, pickups[i].icon, Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 1f);
                    b.DrawString(Game1.smallFont, pickups[i].count.ToString("00"), pos + new Vector2(36, 2), Color.White);
                }
            }

            // stats + link status
            for (int i = 0; i < lines.Count; i++)
                b.DrawString(Game1.smallFont, lines[i].text, new Vector2(left0, textTop + i * lineHeight), lines[i].color);
        }
    }
}
