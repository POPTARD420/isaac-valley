using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace IsaacValley
{
    /// <summary>
    /// Engine mode, the SkyCraft way: Isaac's own engine runs your attacks, Stardew mirrors them.
    /// Stardew sends: which way you shoot, bomb presses, where you stand, where the monsters are.
    /// Isaac sends back: every tear/laser/knife/bomb/familiar/effect it draws, and every hit on a monster.
    /// </summary>
    internal sealed partial class ModEntry
    {
        private EngineLink? engine;
        private Vector2? engineAnchor;                 // the Stardew spot that matches the middle of Isaac's room
        private GameLocation? engineAnchorLocation;
        private int engineBombPresses = 0;
        private readonly Dictionary<Monster, int> monsterIds = new();
        private readonly Dictionary<int, Monster> monstersById = new();
        private int nextMonsterId = 1;
        private readonly Dictionary<int, float> lastHits = new();
        private readonly Dictionary<Monster, float> pendingDamage = new();
        private readonly HashSet<int> tearsToKill = new();
        private HashSet<int> seenIndexes = new();
        private readonly HashSet<int> explodedIndexes = new();
        private readonly HashSet<string> knownAnimFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Is Isaac's engine linked and running right now?</summary>
        private bool EngineLive => this.config.UseIsaacEngine && this.engine?.Live == true;

        private void SetUpEngine()
        {
            this.engine = new EngineLink(this.GetBridgeFolder());
        }

        /*********
        ** Every tick
        *********/
        /// <summary>Send our side to Isaac and read Isaac's side. Runs every tick while a save is loaded.</summary>
        private void SyncEngine()
        {
            if (this.engine == null || !this.config.UseIsaacEngine)
                return;

            bool wasLive = this.engine.Live;
            this.engine.Read();
            if (this.engine.Live != wasLive)
                this.Monitor.Log(this.engine.Live ? "Isaac's engine is linked: Isaac runs your attacks now." : "Isaac's engine stopped; back to classic mode.", LogLevel.Info);

            this.engine.Write(this.BuildEngineInput());
        }

        private string BuildEngineInput()
        {
            var sb = new StringBuilder();
            Farmer player = Game1.player;
            GameLocation location = player.currentLocation;
            bool active = Context.IsWorldReady && location != null;

            sb.Append("seq=").Append(this.engine!.NextSeq()).Append('\n');
            sb.Append("active=").Append(active ? "1" : "0").Append('\n');
            if (!active)
                return sb.ToString();

            int dir = Context.IsPlayerFree ? this.ReadShootDirection() : -1;
            Vector2 feet = this.PlayerFeet;

            // keep Isaac inside its room: move the anchor when you walk too far from it
            Vector2 half = this.engine.Frame?.RoomHalf ?? new Vector2(780, 420);
            if (this.engineAnchor == null || this.engineAnchorLocation != location
                || Math.Abs(feet.X - this.engineAnchor.Value.X) > half.X * 0.6f
                || Math.Abs(feet.Y - this.engineAnchor.Value.Y) > half.Y * 0.6f)
            {
                this.engineAnchor = feet;
                this.engineAnchorLocation = location;
            }
            Vector2 anchor = this.engineAnchor.Value;

            sb.Append("shoot=").Append(dir).Append('\n');
            sb.Append("bombs=").Append(this.engineBombPresses).Append('\n');
            sb.Append("px=").Append(N(feet.X)).Append('\n');
            sb.Append("py=").Append(N(feet.Y)).Append('\n');
            sb.Append("ax=").Append(N(anchor.X)).Append('\n');
            sb.Append("ay=").Append(N(anchor.Y)).Append('\n');

            // monsters near you (Isaac places an invisible stand-in for each)
            foreach (Monster m in location!.characters.OfType<Monster>())
            {
                if (m.Health <= 0)
                    continue;
                Rectangle box = m.GetBoundingBox();
                Vector2 center = box.Center.ToVector2();
                if (Math.Abs(center.X - anchor.X) > half.X * 1.5f || Math.Abs(center.Y - anchor.Y) > half.Y * 1.5f)
                    continue;
                int id = this.GetMonsterId(m);
                float radius = Math.Max(box.Width, box.Height) / 2f;
                sb.Append("m=").Append(id).Append('|').Append(N(center.X)).Append('|').Append(N(center.Y)).Append('|').Append(N(radius)).Append('\n');
            }

            // tears that flew into a Stardew wall
            foreach (int index in this.tearsToKill)
                sb.Append("kill=").Append(index).Append('\n');

            // the animation names in each of Isaac's sprite files, so Isaac can tell us which one is playing
            foreach (string file in this.knownAnimFiles)
            {
                Anm2? anm = this.files?.Anim(IsaacFiles.FromGamePath(file));
                if (anm != null && anm.AnimationNames.Count > 0)
                    sb.Append("anim=").Append(file).Append('|').Append(string.Join(",", anm.AnimationNames)).Append('\n');
            }

            return sb.ToString();
        }

        private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private int GetMonsterId(Monster m)
        {
            if (!this.monsterIds.TryGetValue(m, out int id))
            {
                id = this.nextMonsterId++;
                this.monsterIds[m] = id;
                this.monstersById[id] = m;
            }
            return id;
        }

        /// <summary>Engine mode's part of the combat update: apply Isaac's hits, explosions, sounds and wall splashes.</summary>
        private void UpdateEngineMode()
        {
            EngineFrame? frame = this.engine?.Frame;
            if (frame == null)
                return;
            GameLocation location = Game1.player.currentLocation;

            // forget monsters that are gone
            foreach (Monster gone in this.monsterIds.Keys.Where(m => m.Health <= 0 || m.currentLocation != location).ToList())
            {
                this.monstersById.Remove(this.monsterIds[gone]);
                this.monsterIds.Remove(gone);
                this.pendingDamage.Remove(gone);
            }

            // hits: Isaac tells us the total damage each stand-in took; we apply the new part
            foreach ((int id, float total) in frame.Hits)
            {
                float before = this.lastHits.TryGetValue(id, out float b) ? b : 0f;
                this.lastHits[id] = total;
                if (total <= before || !this.monstersById.TryGetValue(id, out Monster? m))
                    continue;
                this.pendingDamage[m] = (this.pendingDamage.TryGetValue(m, out float p) ? p : 0f) + (total - before) * DamageMultiplier;
            }
            foreach (Monster m in this.pendingDamage.Keys.ToList())
            {
                int damage = (int)this.pendingDamage[m];
                if (damage < 1)
                    continue;
                location.damageMonster(m.GetBoundingBox(), damage, damage, false, Game1.player);
                this.pendingDamage[m] -= damage;
            }

            // status effects Isaac put on the stand-ins (poison/burn damage already comes in as hits)
            foreach ((int id, HashSet<string> flags) in frame.Status)
            {
                if (!this.monstersById.TryGetValue(id, out Monster? m))
                    continue;
                if (!this.statuses.TryGetValue(m, out MonsterStatus? s))
                {
                    s = new MonsterStatus { LastPos = m.Position, FrozenAt = m.Position };
                    this.statuses[m] = s;
                }
                if (flags.Contains("FREEZE") || flags.Contains("MIDAS_FREEZE"))
                {
                    if (s.Freeze <= 0) s.FrozenAt = m.Position;
                    s.Freeze = 4;
                }
                if (flags.Contains("SLOW")) s.Slow = 4;
                if (flags.Contains("FEAR")) s.Fear = 4;
                if (flags.Contains("CHARM") || flags.Contains("CONFUSION")) s.Confuse = 4;
            }
            this.UpdateStatuses();

            // what's new and what's gone since last time: sounds and explosions
            var now = new HashSet<int>(frame.Entities.Select(e => e.Index));
            bool fired = false, splashed = false;
            if (this.debugEngine)
            {
                // bombs, every tick, so we can see how they animate
                foreach (EngineEntity bomb in frame.Entities.Where(x => x.Type == 4))
                    this.Monitor.Log($"[bomb] #{bomb.Index} anim '{bomb.Anim}' frame {bomb.Frame} height {bomb.Height:0} scale {bomb.Scale.X:0.##},{bomb.Scale.Y:0.##} pos {bomb.Pos.X:0},{bomb.Pos.Y:0}", LogLevel.Info);
            }

            foreach (EngineEntity e in frame.Entities)
            {
                this.knownAnimFiles.Add(e.File);
                if (this.seenIndexes.Contains(e.Index))
                    continue;

                if (this.debugEngine)
                {
                    this.Monitor.Log($"[new] type {e.Type}.{e.Variant}.{e.SubType} #{e.Index} file '{e.File}' anim '{e.Anim}' frame {e.Frame} " +
                        $"pos {e.Pos.X:0},{e.Pos.Y:0} height {e.Height:0} scale {e.Scale.X:0.##},{e.Scale.Y:0.##} rot {e.Rotation:0} tint {e.Tint}", LogLevel.Info);
                }

                if (e.Type == 2 && !fired)
                {
                    this.PlaySfx("TearFire", "dropItemInWater");
                    this.lastShotTick = this.tick; // Isaac cries
                    fired = true;
                }
                else if (e.Type == 7)
                {
                    bool thick = e.File.Contains("thick", StringComparison.OrdinalIgnoreCase) || e.File.Contains("giant", StringComparison.OrdinalIgnoreCase);
                    this.PlaySfx(thick ? "Brimstone" : "Technology", thick ? "furnace" : "shiny4");
                    this.lastShotTick = this.tick;
                }
                else if (e.Type == 1000 && e.File.Contains("bomb explosion", StringComparison.OrdinalIgnoreCase) && this.explodedIndexes.Add(e.Index))
                {
                    // Isaac's explosion also happens in Stardew: breaks rocks and hurts monsters there
                    this.PlaySfx("Explosion", "explosion");
                    Vector2 tile = new((int)(e.Pos.X / TileSize), (int)(e.Pos.Y / TileSize));
                    location.explode(tile, BombRadius, Game1.player, damageFarmers: false);
                }
            }
            foreach (int gone in this.seenIndexes.Where(i => !now.Contains(i)))
            {
                if (!splashed && this.lastEngineTypes.TryGetValue(gone, out int type) && type == 2)
                {
                    this.PlaySfx("TearSplash", "waterSlosh");
                    splashed = true;
                }
            }
            this.seenIndexes = now;
            this.lastEngineTypes = frame.Entities.GroupBy(e => e.Index).ToDictionary(g => g.Key, g => g.First().Type);
            this.explodedIndexes.IntersectWith(now);

            // tears that hit a wall in Stardew should splash in Isaac too
            this.tearsToKill.Clear();
            bool spectral = this.GetFlags().Contains("SPECTRAL");
            if (!spectral)
            {
                foreach (EngineEntity e in frame.Entities.Where(e => e.Type == 2))
                {
                    if (this.HitsWall(location, e.Pos))
                        this.tearsToKill.Add(e.Index);
                }
            }

            // charge bar while holding a charge weapon (Isaac charges it for real)
            Weapon weapon = this.GetWeapon();
            int dir = Context.IsPlayerFree ? this.ReadShootDirection() : -1;
            if (dir >= 0)
                this.Aim(dir);
            if (frame.ChargeKind.Length > 0)
            {
                // Isaac's own charge, as 0..100
                this.chargeTicks = (int)Math.Clamp(frame.Charge / frame.ChargeMax * 100f, 0f, 100f);
            }
            else if ((weapon == Weapon.Brimstone || weapon == Weapon.Knife) && dir >= 0 && !this.EngineBrimstoneFiring)
                this.chargeTicks = Math.Min(this.chargeTicks + 1, this.GetChargeMax());
            else
                this.chargeTicks = 0;
        }

        private Dictionary<int, int> lastEngineTypes = new();

        /// <summary>Is a Brimstone laser firing in Isaac right now?</summary>
        private bool EngineBrimstoneFiring => this.EngineLive && this.engine!.Frame!.Entities.Any(e =>
            e.Type == 7 && (e.File.Contains("thick", StringComparison.OrdinalIgnoreCase) || e.File.Contains("giant", StringComparison.OrdinalIgnoreCase)));

        /// <summary>Isaac's head direction in Stardew numbers (0 up, 1 right, 2 down, 3 left), or -1.</summary>
        private int EngineHeadDirection()
        {
            int d = this.engine?.Frame?.HeadDir ?? -1;
            return d switch { 0 => 3, 1 => 0, 2 => 1, 3 => 2, _ => -1 };
        }

        /*********
        ** Drawing what Isaac draws
        *********/
        // Isaac picks a tear's size animation (1-13) from its scale
        private static readonly float[] TearSizeSteps = { 0.3f, 0.55f, 0.675f, 0.8f, 0.925f, 1.05f, 1.175f, 1.425f, 1.675f, 1.925f, 2.175f, 2.55f };

        private static string TearAnimation(Anm2 anm, EngineEntity e)
        {
            if (!string.IsNullOrEmpty(e.Anim) && anm.Animations.ContainsKey(e.Anim))
                return e.Anim;
            int size = 1;
            while (size <= TearSizeSteps.Length && e.TearScale > TearSizeSteps[size - 1])
                size++;
            string first = anm.PickAnimation(null);
            string prefix = first.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            string wanted = prefix + size;
            return anm.Animations.ContainsKey(wanted) ? wanted : first;
        }

        private void DrawEngineEntities(SpriteBatch b)
        {
            EngineFrame? frame = this.engine?.Frame;
            if (frame == null || this.files == null)
                return;

            // order like Isaac: things further down the screen in front
            foreach (EngineEntity e in frame.Entities.OrderBy(e => e.Pos.Y))
            {
                string rel = IsaacFiles.FromGamePath(e.File);
                Anm2? anm = this.files.Anim(rel);
                if (anm == null)
                    continue;
                string anmDir = System.IO.Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";

                Vector2 ground = Game1.GlobalToLocal(Game1.viewport, e.Pos);
                Vector2 drawAt = ground + new Vector2(0, e.Height);
                float depth = Math.Clamp(e.Pos.Y / 10000f + 0.001f, 0f, 1f);

                // shadows under tears and bombs, like Isaac
                if (e.Type == 2 || e.Type == 4)
                {
                    Rectangle shadow = Game1.shadowTexture.Bounds;
                    float size = e.Type == 2 ? 1.2f + e.TearScale : 2.5f;
                    b.Draw(Game1.shadowTexture, ground, shadow, Color.White * 0.5f, 0f,
                        new Vector2(shadow.Center.X, shadow.Center.Y), size, SpriteEffects.None, Math.Max(0f, depth - 0.0005f));
                }

                if (e.Type == 7 && e.End != null)
                {
                    this.DrawEngineLaser(b, anm, anmDir, e, depth);
                    continue;
                }

                string animation = e.Type == 2 ? TearAnimation(anm, e) : anm.PickAnimation(e.Anim);
                Vector2 scale = e.Scale * IsaacSprite.Scale;
                if (e.FlipX)
                    scale.X = -scale.X;

                foreach (Anm2Layer layer in anm.Layers)
                {
                    // some files hold many looks in one (the bomb file has every bomb type); Isaac says which layers are on
                    if (e.Layers != null && !e.Layers.Contains(layer.Name))
                        continue;
                    Anm2Frame? f = anm.FrameAt(animation, layer.Id, e.Frame);
                    if (f == null || !anm.Sheets.TryGetValue(layer.SheetId, out string? sheet))
                        continue;
                    string sheetPath = System.IO.Path.Combine(anmDir, sheet).Replace('\\', '/');
                    Texture2D? tex = this.files.Texture(sheetPath);
                    if (tex == null)
                        continue;
                    Anm2.DrawFrameTransformed(b, tex, f, drawAt, scale, e.Rotation, e.Tint, depth);

                    // Isaac's color offsets (the bomb's flashing before it blows): a bright layer on top
                    if (f.Offset != Vector3.Zero && f.Visible)
                    {
                        Texture2D? flash = this.files.Silhouette(sheetPath);
                        float strength = Math.Max(f.Offset.X, Math.Max(f.Offset.Y, f.Offset.Z));
                        if (flash != null && strength > 0f)
                        {
                            var c = new Color(f.Offset.X / strength, f.Offset.Y / strength, f.Offset.Z / strength) * Math.Min(1f, strength * 0.8f);
                            Anm2.DrawFrameTransformed(b, flash, f, drawAt, scale, e.Rotation, c, depth + 0.00001f);
                        }
                    }
                }
            }
        }

        /// <summary>Draw a laser like Isaac: the beam strip repeated from start to end, with its tip at the end.</summary>
        private void DrawEngineLaser(SpriteBatch b, Anm2 anm, string anmDir, EngineEntity e, float depth)
        {
            Vector2 start = Game1.GlobalToLocal(Game1.viewport, e.Pos) + new Vector2(0, e.Height);
            Vector2 end = Game1.GlobalToLocal(Game1.viewport, e.End!.Value) + new Vector2(0, e.Height);
            Vector2 along = end - start;
            float length = along.Length();
            if (length < 1f)
                return;
            Vector2 dir = along / length;
            float rotation = (float)Math.Atan2(dir.Y, dir.X) - MathHelper.PiOver2; // the strips point down

            // Isaac's beam sprites are grey and get their color from the laser; red if Isaac didn't say
            Color tint = e.Tint;
            if (tint == Color.White && e.File.Contains("red", StringComparison.OrdinalIgnoreCase) && e.File.Contains("thick", StringComparison.OrdinalIgnoreCase))
                tint = new Color(255, 60, 60);

            string animation = anm.PickAnimation(e.Anim);
            float width = Math.Abs(e.Scale.X) * IsaacSprite.Scale;

            foreach (Anm2Layer layer in anm.Layers)
            {
                Anm2Frame? f = anm.FrameAt(animation, layer.Id, e.Frame);
                if (f == null || !anm.Sheets.TryGetValue(layer.SheetId, out string? sheet))
                    continue;
                Texture2D? tex = this.files!.Texture(System.IO.Path.Combine(anmDir, sheet).Replace('\\', '/'));
                if (tex == null)
                    continue;

                bool tip = layer.Name.Contains("tip", StringComparison.OrdinalIgnoreCase) || layer.Name.Contains("impact", StringComparison.OrdinalIgnoreCase);
                if (tip)
                {
                    b.Draw(tex, end, f.Crop, tint, rotation, new Vector2(f.Crop.Width / 2f, 0), width, SpriteEffects.None, depth + 0.0001f);
                    continue;
                }

                float segment = f.Crop.Height * IsaacSprite.Scale;
                int count = (int)Math.Ceiling(length / segment);
                for (int i = 0; i < count; i++)
                {
                    // the last strip is cut short so the beam ends exactly at the end point
                    float remaining = Math.Min(segment, length - i * segment);
                    var crop = new Rectangle(f.Crop.X, f.Crop.Y, f.Crop.Width, Math.Max(1, (int)(f.Crop.Height * remaining / segment)));
                    b.Draw(tex, start + dir * (i * segment), crop, tint, rotation, new Vector2(f.Crop.Width / 2f, 0),
                        new Vector2(width, IsaacSprite.Scale), SpriteEffects.None, depth);
                }
            }
        }
    }
}
