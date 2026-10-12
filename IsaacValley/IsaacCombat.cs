using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Monsters;

namespace IsaacValley
{
    internal enum TearKind { Tear, Knife, FetusBomb }

    internal enum Weapon { Tears, Brimstone, Technology, Knife, DrFetus }

    /// <summary>One of Isaac's tears (or a thrown knife, or a Dr. Fetus bomb) flying through Stardew.</summary>
    internal sealed class Tear
    {
        public TearKind Kind;
        public GameLocation Location = null!;
        public Vector2 Pos;            // on the ground, world pixels
        public Vector2 Vel;
        public float Speed;
        public float Traveled;
        public float MaxDistance;      // Isaac's range
        public int Damage;             // Stardew damage
        public float IsaacDamage;      // decides the tear's size
        public float Scale = 1f;
        public HashSet<string> Flags = new();
        public Color Tint = Color.White;
        public HashSet<Monster> Hit = new();
        public int Age;
        public int Bounces;
        public bool Returning;
        public Vector2 Side;           // sideways direction, for wiggle/spiral
        public float SideOffset;
        public float OrbitAngle;
        public bool Dead;

        public bool Has(string flag) => this.Flags.Contains(flag);
    }

    /// <summary>A Brimstone or Technology laser.</summary>
    internal sealed class Beam
    {
        public GameLocation Location = null!;
        public Vector2 Dir;
        public float Length;
        public bool Thick;             // Brimstone (thick) or Technology (thin)
        public int Duration;
        public int TicksLeft;
        public int HitEvery;
        public int Damage;
        public HashSet<string> Flags = new();
        public Color Tint = Color.White;
    }

    /// <summary>An Isaac bomb ticking in the world.</summary>
    internal sealed class PlacedBomb
    {
        public GameLocation Location = null!;
        public Vector2 Feet;           // world pixels
        public int TicksLeft;
        public Action? StopFuse;       // stops the hissing sound
    }

    /// <summary>An Isaac explosion animation playing in the world.</summary>
    internal sealed class ExplosionEffect
    {
        public GameLocation Location = null!;
        public Vector2 Feet;
        public int Age;                // ticks
        public float Scale = 1f;
    }

    /// <summary>What Isaac's tear effects are doing to a monster right now.</summary>
    internal sealed class MonsterStatus
    {
        public int Freeze, Slow, Fear, Confuse, Poison, Burn;
        public int DotDamage;          // poison / burn damage per pulse
        public Vector2 FrozenAt, LastPos, Knock;
    }

    /// <summary>Everything about attacking: tears, items, lasers, knife, bombs and status effects.</summary>
    internal sealed partial class ModEntry
    {
        private const float TileSize = 64f;
        private const int BombFuseTicksCombat = BombFuseTicks;
        private const int KnifeMaxCharge = 40;

        private int ticksUntilNextTear = 0;
        private int chargeTicks = 0;
        private int chargeDir = 2;
        private uint lastAimTick = 0;
        private Tear? knife;
        private int prevChargeTicks = 0;
        private int chargeFullTicks = 0;           // how long the bar has been full
        private int chargeBarDisappear = -1;       // ticks into the bar's disappear animation, or -1
        private readonly List<Tear> tears = new();
        private readonly List<Beam> beams = new();
        private readonly List<PlacedBomb> bombs = new();
        private readonly List<ExplosionEffect> explosions = new();
        private readonly Dictionary<Monster, MonsterStatus> statuses = new();
        private readonly Random rng = new();

        /*********
        ** Lifecycle
        *********/
        private void ResetCombat()
        {
            foreach (PlacedBomb bomb in this.bombs)
                bomb.StopFuse?.Invoke();
            this.bombs.Clear();
            this.explosions.Clear();
            this.tears.Clear();
            this.beams.Clear();
            this.statuses.Clear();
            this.knife = null;
            this.chargeTicks = 0;
            this.ticksUntilNextTear = 0;
        }

        private void OnWarped()
        {
            // tears and lasers don't follow you through doors; bombs left behind still go off, silently
            foreach (PlacedBomb bomb in this.bombs)
                bomb.StopFuse?.Invoke();
            this.tears.Clear();
            this.beams.Clear();
            this.statuses.Clear();
            this.knife = null;
            this.chargeTicks = 0;
        }

        private void UpdateCombat()
        {
            // engine mode: Isaac's own engine does the attacking; we mirror it
            if (this.EngineLive)
            {
                if (this.tears.Count > 0 || this.beams.Count > 0 || this.knife != null)
                {
                    this.tears.Clear();
                    this.beams.Clear();
                    this.knife = null;
                }
                this.UpdateEngineMode();
                this.UpdateChargeBarState();
                return;
            }

            this.UpdateBombs();
            this.UpdateTears();
            this.UpdateBeams();
            this.UpdateStatuses();

            int explosionTicks = IsaacSprite.ExplosionDelays.Sum() * 2;
            this.explosions.RemoveAll(fx => ++fx.Age >= explosionTicks);

            this.UpdateChargeBarState();

            if (this.ticksUntilNextTear > 0)
                this.ticksUntilNextTear--;

            if (!Context.IsPlayerFree)
            {
                this.chargeTicks = 0;
                return;
            }

            int dir = this.ReadShootDirection();
            Weapon weapon = this.GetWeapon();
            switch (weapon)
            {
                case Weapon.Brimstone:
                    this.UpdateBrimstone(dir);
                    break;

                case Weapon.Knife:
                    this.UpdateKnife(dir);
                    break;

                default:
                    if (dir >= 0)
                    {
                        this.Aim(dir);
                        if (this.ticksUntilNextTear <= 0)
                        {
                            this.Fire(weapon, dir);
                            this.ticksUntilNextTear = this.GetFireDelayTicks();
                        }
                    }
                    break;
            }
        }

        /// <summary>Which arrow key is held (0 up, 1 right, 2 down, 3 left), or -1.</summary>
        private int ReadShootDirection()
        {
            if (this.Helper.Input.IsDown(SButton.Up)) return 0;
            if (this.Helper.Input.IsDown(SButton.Right)) return 1;
            if (this.Helper.Input.IsDown(SButton.Down)) return 2;
            if (this.Helper.Input.IsDown(SButton.Left)) return 3;
            return -1;
        }

        private void Aim(int dir)
        {
            this.shotDirection = dir;
            this.lastAimTick = this.tick;
        }

        private static Vector2 DirVector(int dir) => dir switch
        {
            0 => new Vector2(0, -1),
            1 => new Vector2(1, 0),
            2 => new Vector2(0, 1),
            _ => new Vector2(-1, 0)
        };

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = MathHelper.ToRadians(degrees);
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }

        private Vector2 PlayerFeet => Game1.player.getStandingPosition();

        /*********
        ** Stats -> Stardew numbers
        *********/
        private int GetDamage()
        {
            return this.isaac == null
                ? TearDamage
                : Math.Max(1, (int)Math.Round(this.isaac.Damage * DamageMultiplier));
        }

        private int GetFireDelayTicks()
        {
            // Isaac fires every (MaxFireDelay + 1) updates at 30 updates per second;
            // Stardew ticks 60 times per second, so double it
            return this.isaac == null
                ? FireDelayTicks
                : Math.Max(2, (int)Math.Round((this.isaac.FireDelay + 1) * 2));
        }

        private float GetTearSpeed()
        {
            return this.isaac == null
                ? TearSpeed
                : TearSpeed * Math.Clamp(this.isaac.ShotSpeed, 0.3f, 3f);
        }

        private float GetRangePixels()
        {
            return this.isaac == null
                ? TearRangePixels
                : TearRangePixels * Math.Clamp(this.isaac.Range / BaseIsaacRange, 0.2f, 5f);
        }

        private HashSet<string> GetFlags() => this.isaac?.TearFlags ?? new HashSet<string>();

        private int ItemCount(string item) => this.isaac?.Count(item) ?? 0;

        /// <summary>Which attack Isaac uses, in Isaac's own priority order.</summary>
        private Weapon GetWeapon()
        {
            if (this.isaac == null) return Weapon.Tears;
            if (this.ItemCount("MOMS_KNIFE") > 0) return Weapon.Knife;
            if (this.ItemCount("BRIMSTONE") > 0) return Weapon.Brimstone;
            if (this.ItemCount("TECHNOLOGY") > 0) return Weapon.Technology;
            if (this.ItemCount("DR_FETUS") > 0 || this.ItemCount("EPIC_FETUS") > 0) return Weapon.DrFetus;
            return Weapon.Tears;
        }

        private static string WeaponName(Weapon w) => w switch
        {
            Weapon.Brimstone => "Brimstone",
            Weapon.Technology => "Technology",
            Weapon.Knife => "Mom's Knife",
            Weapon.DrFetus => "Dr. Fetus",
            _ => "Tears"
        };

        /// <summary>How many tears per shot: 20/20, The Inner Eye, Mutant Spider.</summary>
        private int GetMultiShot()
        {
            int spider = this.ItemCount("MUTANT_SPIDER");
            int inner = this.ItemCount("THE_INNER_EYE");
            int twenty = this.ItemCount("20_20");

            int n = 1;
            if (spider > 0) n = 4 + (spider - 1) * 2;
            else if (inner > 0) n = 3 + (inner - 1);
            else if (twenty > 0) n = 2;

            if (spider > 0 && inner > 0) n += 1;
            if (twenty > 0 && (spider > 0 || inner > 0)) n += 1;
            return Math.Min(n, 16);
        }

        /// <summary>All the directions one shot goes in: the main fan, plus The Wiz, Mom's Eye and Loki's Horns.</summary>
        private List<Vector2> GetShotDirections(int dir, bool allowFan)
        {
            Vector2 main = DirVector(dir);
            var dirs = new List<Vector2>();
            float luck = this.isaac?.Luck ?? 0f;

            if (allowFan && this.ItemCount("THE_WIZ") > 0)
            {
                // The Wiz: two tears at an angle instead of straight
                dirs.Add(Rotate(main, -35));
                dirs.Add(Rotate(main, 35));
            }
            else
            {
                int n = allowFan ? this.GetMultiShot() : 1;
                float spread = 8f;
                for (int i = 0; i < n; i++)
                    dirs.Add(Rotate(main, (i - (n - 1) / 2f) * spread));
            }

            // Mom's Eye: 50% chance (more with luck) of a tear backwards
            if (this.ItemCount("MOMS_EYE") > 0 && this.rng.NextDouble() < Math.Min(1.0, 0.5 + luck * 0.1))
                dirs.Add(-main);

            // Loki's Horns: 25% chance (more with luck) of shooting in all four directions
            if (this.ItemCount("LOKIS_HORNS") > 0 && this.rng.NextDouble() < Math.Min(1.0, 0.25 + luck * 0.05))
            {
                dirs.Add(-main);
                dirs.Add(new Vector2(main.Y, -main.X));
                dirs.Add(new Vector2(-main.Y, main.X));
            }

            return dirs;
        }

        /// <summary>The color of Isaac's tears: from Isaac itself, or from the strongest tear effect.</summary>
        private Color GetTearTint(HashSet<string> flags)
        {
            Color isaacColor = this.isaac?.TearColor ?? Color.White;
            if (isaacColor != Color.White)
                return isaacColor;

            if (flags.Contains("EXPLOSIVE")) return new Color(140, 220, 60);   // Ipecac
            if (flags.Contains("POISON")) return new Color(110, 220, 110);
            if (flags.Contains("BURN")) return new Color(255, 150, 60);
            if (flags.Contains("FREEZE")) return new Color(170, 170, 170);
            if (flags.Contains("SLOW")) return new Color(235, 235, 255);
            if (flags.Contains("CHARM")) return new Color(255, 140, 200);
            if (flags.Contains("FEAR")) return new Color(140, 80, 170);
            if (flags.Contains("CONFUSION")) return new Color(170, 170, 200);
            if (flags.Contains("HOMING")) return new Color(220, 130, 255);
            return Color.White;
        }

        /*********
        ** Firing
        *********/
        private void Fire(Weapon weapon, int dir)
        {
            GameLocation location = Game1.player.currentLocation;
            this.lastShotTick = this.tick;

            switch (weapon)
            {
                case Weapon.Technology:
                    foreach (Vector2 d in this.GetShotDirections(dir, allowFan: false))
                        this.FireBeam(location, d, thick: false);
                    this.PlaySfx("Technology", "shiny4");
                    break;

                case Weapon.DrFetus:
                    foreach (Vector2 d in this.GetShotDirections(dir, allowFan: true))
                        this.SpawnTear(TearKind.FetusBomb, location, d);
                    this.PlaySfx("TearFire", "throw");
                    break;

                default:
                    foreach (Vector2 d in this.GetShotDirections(dir, allowFan: true))
                        this.SpawnTear(TearKind.Tear, location, d);
                    this.PlaySfx("TearFire", "dropItemInWater");
                    break;
            }
        }

        private Tear SpawnTear(TearKind kind, GameLocation location, Vector2 direction, float power = 1f)
        {
            direction.Normalize();
            float speed = kind switch
            {
                TearKind.Knife => 16f,
                TearKind.FetusBomb => this.GetTearSpeed() * 0.75f,
                _ => this.GetTearSpeed()
            };
            HashSet<string> flags = new(this.GetFlags());

            var tear = new Tear
            {
                Kind = kind,
                Location = location,
                Pos = this.PlayerFeet + direction * 48f, // just outside Isaac
                Vel = direction * speed,
                Speed = speed,
                MaxDistance = kind == TearKind.Knife ? this.GetRangePixels() * 1.6f * power : this.GetRangePixels(),
                Damage = kind == TearKind.Knife ? this.GetDamage() * 2 : this.GetDamage(),
                IsaacDamage = this.isaac?.Damage ?? 3.5f,
                Flags = flags,
                Tint = this.GetTearTint(flags),
                Side = new Vector2(-direction.Y, direction.X),
                OrbitAngle = (float)Math.Atan2(direction.Y, direction.X)
            };
            this.tears.Add(tear);
            return tear;
        }

        /*********
        ** Brimstone and Technology
        *********/
        private void UpdateBrimstone(int dir)
        {
            int chargeMax = this.GetChargeMax();

            // can't charge while the beam is still firing
            if (this.beams.Any(b => b.Thick))
            {
                this.chargeTicks = 0;
                return;
            }

            if (dir >= 0)
            {
                this.Aim(dir);
                this.chargeDir = dir;
                this.chargeTicks = Math.Min(this.chargeTicks + 1, chargeMax);
                this.lastShotTick = this.tick; // Isaac's face strains while charging
                return;
            }

            // released: fire if fully charged
            if (this.chargeTicks >= chargeMax)
            {
                GameLocation location = Game1.player.currentLocation;
                foreach (Vector2 d in this.GetShotDirections(this.chargeDir, allowFan: false))
                    this.FireBeam(location, d, thick: true);
                this.PlaySfx("Brimstone", "furnace");
                this.Aim(this.chargeDir);
                this.lastShotTick = this.tick;
            }
            this.chargeTicks = 0;
        }

        private void FireBeam(GameLocation location, Vector2 dir, bool thick)
        {
            HashSet<string> flags = new(this.GetFlags());
            this.beams.Add(new Beam
            {
                Location = location,
                Dir = Vector2.Normalize(dir),
                Length = Math.Max(10 * TileSize, this.GetRangePixels() * 1.5f),
                Thick = thick,
                Duration = thick ? 30 : 8,
                TicksLeft = thick ? 30 : 8,
                HitEvery = thick ? 5 : 100, // Brimstone hits again and again; Technology hits once
                Damage = this.GetDamage(),
                Flags = flags,
                Tint = this.GetLaserTint(flags, thick)
            });
        }

        /// <summary>Laser color, like Isaac: red, purple when homing (Spoon Bender), then Isaac's own laser color on top.</summary>
        private Color GetLaserTint(HashSet<string> flags, bool thick)
        {
            Color tint = thick ? new Color(255, 60, 60) : Color.White;
            if (flags.Contains("HOMING"))
                tint = thick ? new Color(200, 70, 230) : new Color(230, 150, 255);

            Color? laser = this.isaac?.LaserColor;
            if (laser is Color lc && lc != Color.White)
                tint = new Color(tint.R * lc.R / 255, tint.G * lc.G / 255, tint.B * lc.B / 255);

            Color tear = this.isaac?.TearColor ?? Color.White;
            if (tear != Color.White)
                tint = new Color(tint.R * tear.R / 255, tint.G * tear.G / 255, tint.B * tear.B / 255);
            return tint;
        }

        /// <summary>The area a laser covers, on the ground.</summary>
        private Rectangle GetBeamArea(Beam beam)
        {
            Vector2 origin = this.PlayerFeet;
            int half = beam.Thick ? 40 : 20;
            Vector2 end = origin + beam.Dir * beam.Length;
            int left = (int)Math.Min(origin.X, end.X) - half;
            int top = (int)Math.Min(origin.Y, end.Y) - half;
            int right = (int)Math.Max(origin.X, end.X) + half;
            int bottom = (int)Math.Max(origin.Y, end.Y) + half;
            return new Rectangle(left, top, right - left, bottom - top);
        }

        private void UpdateBeams()
        {
            foreach (Beam beam in this.beams)
            {
                int age = beam.Duration - beam.TicksLeft;
                if (age % beam.HitEvery == 0 && beam.Location == Game1.player.currentLocation)
                {
                    Rectangle area = this.GetBeamArea(beam);
                    foreach (Monster m in beam.Location.characters.OfType<Monster>().ToList())
                    {
                        if (m.Health > 0 && m.GetBoundingBox().Intersects(area))
                            this.ApplyStatus(beam.Flags, m, beam.Dir, beam.Damage);
                    }
                    beam.Location.damageMonster(area, beam.Damage, beam.Damage, false, Game1.player);
                }
                beam.TicksLeft--;
            }
            this.beams.RemoveAll(b => b.TicksLeft <= 0);
        }

        /*********
        ** Mom's Knife
        *********/
        private void UpdateKnife(int dir)
        {
            if (this.knife != null)
                return; // still flying

            if (dir >= 0)
            {
                this.Aim(dir);
                this.chargeDir = dir;
                this.chargeTicks = Math.Min(this.chargeTicks + 1, KnifeMaxCharge);
                return;
            }

            if (this.chargeTicks > 0)
            {
                float power = Math.Max(0.25f, this.chargeTicks / (float)KnifeMaxCharge);
                GameLocation location = Game1.player.currentLocation;
                this.knife = this.SpawnTear(TearKind.Knife, location, DirVector(this.chargeDir), power);
                this.lastShotTick = this.tick; // Isaac's knife makes no sound when thrown
            }
            this.chargeTicks = 0;
        }

        /*********
        ** Tears flying
        *********/
        private void UpdateTears()
        {
            foreach (Tear t in this.tears)
            {
                t.Age++;
                if (t.Location != Game1.player.currentLocation)
                {
                    t.Dead = true;
                    continue;
                }

                // Tiny Planet: tears circle around Isaac
                if (t.Has("ORBIT"))
                {
                    t.OrbitAngle += 0.08f;
                    t.Pos = this.PlayerFeet + new Vector2((float)Math.Cos(t.OrbitAngle), (float)Math.Sin(t.OrbitAngle)) * 96f;
                    if (t.Age > 150)
                        t.Dead = true;
                    else
                        this.CheckMonsterHits(t);
                    continue;
                }

                // homing: steer toward the nearest monster
                if (t.Has("HOMING"))
                {
                    Monster? target = this.FindNearestMonster(t.Location, t.Pos, 6 * TileSize);
                    if (target != null)
                    {
                        Vector2 want = target.GetBoundingBox().Center.ToVector2() - t.Pos;
                        if (want != Vector2.Zero)
                        {
                            want.Normalize();
                            t.Vel = Vector2.Lerp(t.Vel, want * t.Speed, 0.12f);
                            if (t.Vel != Vector2.Zero)
                                t.Vel = Vector2.Normalize(t.Vel) * t.Speed;
                        }
                    }
                }

                // boomerang (My Reflection) and Mom's Knife come back
                bool boomerang = t.Kind == TearKind.Knife || t.Has("BOOMERANG");
                if (boomerang && !t.Returning && t.Traveled > t.MaxDistance * (t.Kind == TearKind.Knife ? 1f : 0.6f))
                {
                    t.Returning = true;
                    t.Hit.Clear(); // can hit the same monsters on the way back
                }
                if (t.Returning)
                {
                    Vector2 back = this.PlayerFeet - t.Pos;
                    if (back.Length() < 40f)
                    {
                        t.Dead = true;
                        continue;
                    }
                    back.Normalize();
                    t.Vel = back * t.Speed;
                }

                // move, with wiggle (Wiggle Worm) or spiral (Ouroboros Worm) sideways motion
                Vector2 next = t.Pos + t.Vel;
                float sideways = 0f;
                if (t.Has("WIGGLE"))
                    sideways = (float)Math.Sin(t.Age * 0.35f) * 20f;
                else if (t.Has("SPIRAL") || t.Has("BIG_SPIRAL"))
                    sideways = (float)Math.Sin(t.Age * 0.25f) * Math.Min(60f, t.Age * 0.6f);
                next += t.Side * (sideways - t.SideOffset);
                t.SideOffset = sideways;

                t.Pos = next;
                t.Traveled += t.Vel.Length();

                // hit monsters first
                this.CheckMonsterHits(t);
                if (t.Dead)
                    continue;

                // walls and rocks (spectral tears and knives fly through)
                bool spectral = t.Has("SPECTRAL") || t.Kind == TearKind.Knife;
                if (!spectral && this.HitsWall(t.Location, t.Pos))
                {
                    if (t.Has("BOUNCE") && t.Bounces < 3)
                    {
                        // Rubber Cement: bounce off the wall
                        Vector2 before = t.Pos - t.Vel;
                        if (this.HitsWall(t.Location, new Vector2(t.Pos.X, before.Y))) t.Vel.X = -t.Vel.X;
                        if (this.HitsWall(t.Location, new Vector2(before.X, t.Pos.Y))) t.Vel.Y = -t.Vel.Y;
                        t.Pos = before;
                        t.Bounces++;
                    }
                    else
                    {
                        this.EndTear(t);
                        continue;
                    }
                }

                // out of range: the tear falls
                if (!boomerang && t.Traveled > t.MaxDistance)
                    this.EndTear(t);
            }

            if (this.knife != null && this.knife.Dead)
                this.knife = null;
            this.tears.RemoveAll(t => t.Dead);
        }

        /// <summary>Does a tear at this spot hit something solid? (walls, buildings, rocks, fences, trees, boulders)</summary>
        private bool HitsWall(GameLocation location, Vector2 pos)
        {
            Vector2 tile = new((int)(pos.X / TileSize), (int)(pos.Y / TileSize));

            // off the edge of the map
            if (!location.isTileOnMap(tile))
                return true;

            // walls, buildings, water edges... anything the map itself says you can't walk through
            if (!location.isTilePassable(new xTile.Dimensions.Location((int)tile.X, (int)tile.Y), Game1.viewport))
                return true;

            // objects: rocks in the mines, fences, chests, machines
            if (location.Objects.TryGetValue(tile, out StardewValley.Object? obj) && obj != null && !obj.isPassable())
                return true;

            // trees and other solid terrain features
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature != null && !feature.isPassable())
                return true;

            // big boulders and stumps
            var box = new Rectangle((int)pos.X - 12, (int)pos.Y - 12, 24, 24);
            foreach (var clump in location.resourceClumps)
            {
                if (clump.getBoundingBox().Intersects(box))
                    return true;
            }

            return false;
        }

        /// <summary>A tear lands: splash, or boom for Dr. Fetus.</summary>
        private void EndTear(Tear t)
        {
            t.Dead = true;
            if (t.Kind == TearKind.FetusBomb)
                this.ExplodeAt(t.Location, t.Pos, 2, damageFarmers: true);
            else if (t.Has("EXPLOSIVE"))
                this.ExplodeAt(t.Location, t.Pos, 1, damageFarmers: true);
            else
                this.PlaySfx("TearSplash", "waterSlosh");
        }

        private void CheckMonsterHits(Tear t)
        {
            var box = new Rectangle((int)t.Pos.X - 16, (int)t.Pos.Y - 16, 32, 32);
            if (t.Kind == TearKind.Knife)
                box.Inflate(8, 8);

            foreach (Monster m in t.Location.characters.OfType<Monster>().ToList())
            {
                if (m.Health <= 0 || t.Hit.Contains(m) || !m.GetBoundingBox().Intersects(box))
                    continue;

                t.Hit.Add(m);

                if (t.Kind == TearKind.FetusBomb)
                {
                    t.Dead = true;
                    this.ExplodeAt(t.Location, t.Pos, 2, damageFarmers: true);
                    return;
                }

                // Lump of Coal: more damage the further the tear flew
                float grow = t.Has("GROW") ? 1f + t.Traveled / (4 * TileSize) : 1f;
                int damage = Math.Max(1, (int)Math.Round(t.Damage * grow));
                t.Location.damageMonster(m.GetBoundingBox(), damage, damage, false, Game1.player);
                this.ApplyStatus(t.Flags, m, t.Vel, damage);

                if (t.Has("EXPLOSIVE"))
                    this.ExplodeAt(t.Location, t.Pos, 1, damageFarmers: true);

                // The Parasite / Cricket's Body: split into smaller tears
                if (t.Has("SPLIT") || t.Has("QUADSPLIT") || t.Has("BURSTSPLIT"))
                    this.SplitTear(t, m, t.Has("SPLIT") ? 2 : 4);

                bool piercing = t.Has("PIERCING") || t.Kind == TearKind.Knife;
                if (!piercing)
                {
                    t.Dead = true;
                    this.PlaySfx("TearSplash", "slimeHit");
                    return;
                }
            }
        }

        private void SplitTear(Tear parent, Monster hit, int count)
        {
            Vector2 dir = parent.Vel == Vector2.Zero ? new Vector2(1, 0) : Vector2.Normalize(parent.Vel);
            for (int i = 0; i < count; i++)
            {
                float angle = count == 2 ? (i == 0 ? 90 : -90) : 45 + 90 * i;
                Vector2 d = Rotate(dir, angle);
                var child = new Tear
                {
                    Kind = TearKind.Tear,
                    Location = parent.Location,
                    Pos = parent.Pos,
                    Vel = d * parent.Speed,
                    Speed = parent.Speed,
                    MaxDistance = 3 * TileSize,
                    Damage = Math.Max(1, parent.Damage / 2),
                    IsaacDamage = parent.IsaacDamage / 2,
                    Flags = new HashSet<string>(parent.Flags.Where(f => f != "SPLIT" && f != "QUADSPLIT" && f != "BURSTSPLIT")),
                    Tint = parent.Tint,
                    Side = new Vector2(-d.Y, d.X)
                };
                child.Hit.Add(hit);
                this.tears.Add(child);
            }
        }

        private Monster? FindNearestMonster(GameLocation location, Vector2 pos, float maxDistance)
        {
            Monster? best = null;
            float bestDistance = maxDistance;
            foreach (Monster m in location.characters.OfType<Monster>())
            {
                if (m.Health <= 0)
                    continue;
                float d = Vector2.Distance(pos, m.GetBoundingBox().Center.ToVector2());
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = m;
                }
            }
            return best;
        }

        /*********
        ** Status effects (slow, freeze, poison, burn, charm, fear, confusion, knockback)
        *********/
        private void ApplyStatus(HashSet<string> flags, Monster m, Vector2 direction, int damage)
        {
            if (flags.Count == 0)
                return;

            if (!this.statuses.TryGetValue(m, out MonsterStatus? s))
            {
                s = new MonsterStatus { LastPos = m.Position, FrozenAt = m.Position };
                this.statuses[m] = s;
            }

            int dot = Math.Max(1, damage / 3);
            if (flags.Contains("FREEZE")) { s.Freeze = 60; s.FrozenAt = m.Position; }   // Mom's Contacts
            if (flags.Contains("SLOW")) s.Slow = 120;                                    // Spider Bite
            if (flags.Contains("POISON")) { s.Poison = 120; s.DotDamage = dot; }         // Common Cold, Scorpio
            if (flags.Contains("BURN")) { s.Burn = 120; s.DotDamage = dot; }             // Fire Mind
            if (flags.Contains("CHARM") || flags.Contains("CONFUSION")) s.Confuse = 90;  // Mom's Perfume, Iron Bar
            if (flags.Contains("FEAR")) s.Fear = 90;                                     // Dark Matter
            if (flags.Contains("KNOCKBACK") && direction != Vector2.Zero)                // Pisces
                s.Knock = Vector2.Normalize(direction) * 8f;
        }

        private void UpdateStatuses()
        {
            foreach ((Monster m, MonsterStatus s) in this.statuses.ToList())
            {
                if (m.Health <= 0 || m.currentLocation != Game1.player.currentLocation)
                {
                    this.statuses.Remove(m);
                    continue;
                }

                if (s.Freeze > 0)
                {
                    s.Freeze--;
                    m.Position = s.FrozenAt;
                }
                else if (s.Slow > 0)
                {
                    s.Slow--;
                    m.Position = Vector2.Lerp(s.LastPos, m.Position, 0.4f);
                }

                if (s.Fear > 0)
                {
                    s.Fear--;
                    Vector2 away = m.GetBoundingBox().Center.ToVector2() - this.PlayerFeet;
                    if (away != Vector2.Zero)
                        this.TryMove(m, Vector2.Normalize(away) * 2.5f);
                }

                if (s.Confuse > 0)
                {
                    s.Confuse--;
                    float a = (float)(this.rng.NextDouble() * Math.PI * 2);
                    this.TryMove(m, new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * 2f);
                }

                if (s.Knock.LengthSquared() > 0.25f)
                {
                    this.TryMove(m, s.Knock);
                    s.Knock *= 0.8f;
                }

                if (s.Poison > 0 || s.Burn > 0)
                {
                    if (s.Poison > 0) s.Poison--;
                    if (s.Burn > 0) s.Burn--;
                    if (this.tick % 30 == 0)
                        m.currentLocation.damageMonster(m.GetBoundingBox(), s.DotDamage, s.DotDamage, false, Game1.player);
                }

                s.LastPos = m.Position;

                if (s.Freeze <= 0 && s.Slow <= 0 && s.Fear <= 0 && s.Confuse <= 0 && s.Poison <= 0 && s.Burn <= 0 && s.Knock.LengthSquared() <= 0.25f)
                    this.statuses.Remove(m);
            }
        }

        /// <summary>Push a monster, unless that would push it into a wall.</summary>
        private void TryMove(Monster m, Vector2 delta)
        {
            Rectangle box = m.GetBoundingBox();
            box.Offset((int)Math.Round(delta.X), (int)Math.Round(delta.Y));
            if (!m.currentLocation.isCollidingPosition(box, Game1.viewport, false, 0, false, m))
                m.Position += delta;
        }

        /*********
        ** Bombs
        *********/
        /// <summary>Isaac's bombs, minus the ones Stardew used that Isaac hasn't taken off yet.</summary>
        private int GetEffectiveBombs()
        {
            if (this.isaac == null)
                return 0;
            int pending = Math.Max(0, this.stardewBombsTotal - this.isaac.AppliedBombs);
            return Math.Max(0, this.isaac.Bombs - pending);
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            // engine mode: Isaac presses its own bomb button (Dr. Fetus, bomb items and all)
            if (e.Button == this.config.BombKey && Context.IsPlayerFree && this.EngineLive)
            {
                this.Helper.Input.Suppress(e.Button);
                this.engineBombPresses++;
                return;
            }

            // only while linked: otherwise E opens the menu as usual
            if (e.Button != this.config.BombKey || !Context.IsPlayerFree || this.isaac == null || !this.countersLoaded)
                return;

            this.Helper.Input.Suppress(e.Button); // don't open the menu

            if (this.GetEffectiveBombs() <= 0)
            {
                Game1.playSound("cancel");
                return;
            }

            Farmer player = Game1.player;
            var bomb = new PlacedBomb
            {
                Location = player.currentLocation,
                Feet = player.getStandingPosition() + new Vector2(0, 8),
                TicksLeft = BombFuseTicksCombat
            };

            // Isaac bombs don't hiss; they just blink and go off
            this.bombs.Add(bomb);
            this.stardewBombsTotal++;
            this.WriteCountersToBridge();
        }

        private void UpdateBombs()
        {
            for (int i = this.bombs.Count - 1; i >= 0; i--)
            {
                PlacedBomb bomb = this.bombs[i];
                if (--bomb.TicksLeft > 0)
                    continue;

                this.bombs.RemoveAt(i);
                bomb.StopFuse?.Invoke();
                this.ExplodeAt(bomb.Location, bomb.Feet, BombRadius, damageFarmers: true);
            }
        }

        /// <summary>An Isaac explosion: Stardew's explosion (breaks rocks, hurts monsters and you) plus Isaac's animation.</summary>
        private void ExplodeAt(GameLocation location, Vector2 feet, int radius, bool damageFarmers)
        {
            Vector2 tile = new((int)(feet.X / TileSize), (int)(feet.Y / TileSize));
            this.PlaySfx("Explosion", "explosion");
            location.explode(tile, radius, Game1.player, damageFarmers: damageFarmers);
            this.explosions.Add(new ExplosionEffect { Location = location, Feet = feet, Scale = radius >= 3 ? 1f : 0.45f + radius * 0.2f });
        }

        /*********
        ** Drawing tears, lasers, knife, bombs, explosions
        *********/
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            SpriteBatch b = e.SpriteBatch;
            GameLocation here = Game1.currentLocation;
            const float tearHeight = 56f; // tears fly at Isaac's head height

            // engine mode: draw exactly what Isaac's engine is drawing
            if (this.EngineLive)
            {
                this.DrawEngineEntities(b);
                this.DrawChargeBar(b);
                return;
            }

            // lasers
            if (this.sprite.Lasers != null)
            {
                foreach (Beam beam in this.beams.Where(p => p.Location == here))
                {
                    float scale = beam.Thick ? 3f : 2f;
                    float segment = 64 * scale;
                    float rotation = (float)Math.Atan2(beam.Dir.Y, beam.Dir.X) - MathHelper.PiOver2;
                    Rectangle frame = beam.Thick ? IsaacSprite.ThickLaserFrame(this.tick) : IsaacSprite.ThinLaserFrame(this.tick);
                    float fade = beam.Thick ? Math.Min(1f, beam.TicksLeft / 6f) : beam.TicksLeft / (float)beam.Duration;
                    Vector2 start = Game1.GlobalToLocal(Game1.viewport, this.PlayerFeet) - new Vector2(0, tearHeight);
                    int count = (int)Math.Ceiling(beam.Length / segment);
                    for (int i = 0; i < count; i++)
                    {
                        b.Draw(this.sprite.Lasers, start + beam.Dir * (i * segment), frame, beam.Tint * fade, rotation,
                            new Vector2(16, 0), scale, SpriteEffects.None, 1f);
                    }
                }
            }

            // tears, knife, Dr. Fetus bombs
            foreach (Tear t in this.tears.Where(p => p.Location == here))
            {
                Vector2 ground = Game1.GlobalToLocal(Game1.viewport, t.Pos);
                Rectangle shadow = Game1.shadowTexture.Bounds;
                b.Draw(Game1.shadowTexture, ground, shadow, Color.White * 0.5f, 0f,
                    new Vector2(shadow.Center.X, shadow.Center.Y), 1.5f * t.Scale, SpriteEffects.None, 0.99f);

                Vector2 air = ground - new Vector2(0, tearHeight);
                switch (t.Kind)
                {
                    case TearKind.Knife when this.sprite.Knife != null:
                        float angle = (float)Math.Atan2(t.Vel.Y, t.Vel.X) - MathHelper.PiOver2;
                        b.Draw(this.sprite.Knife, air, IsaacSprite.KnifeFrame, Color.White, angle,
                            new Vector2(16, 16), IsaacSprite.Scale, SpriteEffects.None, 1f);
                        break;

                    case TearKind.FetusBomb when this.sprite.Bomb != null:
                        b.Draw(this.sprite.Bomb, air, IsaacSprite.BombFrame, Color.White, 0f,
                            new Vector2(16, 16), 2f, SpriteEffects.None, 1f);
                        break;

                    default:
                        if (this.sprite.Tears != null)
                        {
                            b.Draw(this.sprite.Tears, air, IsaacSprite.TearForDamage(t.IsaacDamage), t.Tint, 0f,
                                new Vector2(16, 16), 2.5f * t.Scale, SpriteEffects.None, 1f);
                        }
                        else
                        {
                            b.Draw(Game1.staminaRect, new Rectangle((int)air.X - 8, (int)air.Y - 8, 16, 16), t.Tint);
                        }
                        break;
                }
            }

            // Mom's Knife in Isaac's hand (pulled back while charging)
            if (this.GetWeapon() == Weapon.Knife && this.knife == null && this.sprite.Knife != null && this.config.PlayAsIsaac)
            {
                Vector2 d = DirVector(this.shotDirection);
                float pull = this.chargeTicks / (float)KnifeMaxCharge * 20f;
                Vector2 pos = Game1.GlobalToLocal(Game1.viewport, this.PlayerFeet + d * (44f - pull)) - new Vector2(0, 40);
                float angle = (float)Math.Atan2(d.Y, d.X) - MathHelper.PiOver2;
                b.Draw(this.sprite.Knife, pos, IsaacSprite.KnifeFrame, Color.White, angle, new Vector2(16, 16), IsaacSprite.Scale, SpriteEffects.None, 1f);
            }

            // Isaac's charge bar (Brimstone, Mom's Knife), next to his head
            this.DrawChargeBar(b);

            // status effects: a little colored drop over affected monsters
            if (this.sprite.Tears != null)
            {
                foreach ((Monster m, MonsterStatus s) in this.statuses)
                {
                    if (m.currentLocation != here)
                        continue;
                    Color? c = s.Freeze > 0 ? new Color(170, 170, 170)
                        : s.Poison > 0 ? new Color(110, 220, 110)
                        : s.Burn > 0 ? new Color(255, 150, 60)
                        : s.Fear > 0 ? new Color(140, 80, 170)
                        : s.Confuse > 0 ? new Color(255, 140, 200)
                        : s.Slow > 0 ? new Color(235, 235, 255)
                        : null;
                    if (c == null)
                        continue;
                    Rectangle box = m.GetBoundingBox();
                    Vector2 over = Game1.GlobalToLocal(Game1.viewport, new Vector2(box.Center.X, box.Top - 48));
                    b.Draw(this.sprite.Tears, over, IsaacSprite.TearForDamage(1.5f), c.Value, 0f, new Vector2(16, 16), 2f, SpriteEffects.None, 1f);
                }
            }

            // ticking bombs: pulse faster and flash red as the fuse runs out
            if (this.sprite.Bomb != null)
            {
                foreach (PlacedBomb bomb in this.bombs.Where(p => p.Location == here))
                {
                    float progress = 1f - bomb.TicksLeft / (float)BombFuseTicksCombat;
                    float pulse = 1f + 0.12f * (float)Math.Sin(this.tick * (0.2f + progress * 0.6f));
                    Color tint = (bomb.TicksLeft / 6) % 2 == 0 && progress > 0.5f ? Color.Red : Color.White;
                    b.Draw(this.sprite.Bomb, Game1.GlobalToLocal(Game1.viewport, bomb.Feet), IsaacSprite.BombFrame, tint, 0f,
                        IsaacSprite.BombPivot, IsaacSprite.Scale * pulse, SpriteEffects.None, 1f);
                }
            }

            // explosions
            if (this.sprite.Explosion != null)
            {
                foreach (ExplosionEffect fx in this.explosions.Where(p => p.Location == here))
                {
                    int frame = 0, t = fx.Age;
                    while (frame < IsaacSprite.ExplosionDelays.Length - 1 && t >= IsaacSprite.ExplosionDelays[frame] * 2)
                    {
                        t -= IsaacSprite.ExplosionDelays[frame] * 2;
                        frame++;
                    }
                    b.Draw(this.sprite.Explosion, Game1.GlobalToLocal(Game1.viewport, fx.Feet), IsaacSprite.ExplosionFrame(frame), Color.White, 0f,
                        IsaacSprite.ExplosionPivot, IsaacSprite.Scale * fx.Scale, SpriteEffects.None, 1f);
                }
            }
        }

        private int GetChargeMax()
        {
            // engine mode: the charge comes from Isaac itself (shown as 0..100)
            if (this.EngineLive && this.engine!.Frame!.ChargeKind.Length > 0)
                return 100;
            // Isaac's fire delay already includes Brimstone's slower charge
            return this.GetWeapon() == Weapon.Knife ? KnifeMaxCharge : Math.Max(20, this.GetFireDelayTicks());
        }

        /// <summary>Track the charge bar's animation: filling, flashing when full, glowing, then disappearing on release.</summary>
        private void UpdateChargeBarState()
        {
            if (this.chargeTicks > 0)
            {
                this.chargeBarDisappear = -1;
                this.chargeFullTicks = this.chargeTicks >= this.GetChargeMax() ? this.chargeFullTicks + 1 : 0;
            }
            else
            {
                if (this.prevChargeTicks > 0)
                    this.chargeBarDisappear = 0; // just released
                else if (this.chargeBarDisappear >= 0)
                    this.chargeBarDisappear = this.chargeBarDisappear >= 18 ? -1 : this.chargeBarDisappear + 1;
                this.chargeFullTicks = 0;
            }
            this.prevChargeTicks = this.chargeTicks;
        }

        /// <summary>Draw Isaac's own charge bar (chargebar.anm2): Charging, StartCharged, Charged, Disappear.</summary>
        private void DrawChargeBar(SpriteBatch b)
        {
            Anm2? bar = this.files?.Anim("chargebar.anm2");
            string anim;
            int time;
            if (this.chargeTicks > 0)
            {
                if (this.chargeFullTicks > 0)
                {
                    int t = (this.chargeFullTicks - 1) / 2;   // in Isaac updates
                    anim = t < 12 ? "StartCharged" : "Charged";
                    time = t < 12 ? t : t - 12;
                }
                else
                {
                    anim = "Charging";
                    time = (int)(Math.Min(1f, this.chargeTicks / (float)this.GetChargeMax()) * 100);
                }
            }
            else if (this.chargeBarDisappear >= 0)
            {
                anim = "Disappear";
                time = this.chargeBarDisappear / 2;
            }
            else
            {
                return;
            }

            // next to Isaac's head, like in the game
            Vector2 pos = Game1.GlobalToLocal(Game1.viewport, this.PlayerFeet) + new Vector2(20, -38) * IsaacSprite.Scale;

            if (bar == null || !bar.Sheets.TryGetValue(0, out string? sheet) || this.files!.Texture(sheet) is not Texture2D tex)
            {
                // fallback: a simple bar
                if (anim == "Disappear")
                    return;
                float fill = Math.Min(1f, this.chargeTicks / (float)this.GetChargeMax());
                b.Draw(Game1.staminaRect, new Rectangle((int)pos.X - 30, (int)pos.Y, 60, 12), Color.Black * 0.7f);
                b.Draw(Game1.staminaRect, new Rectangle((int)pos.X - 28, (int)pos.Y + 2, (int)(56 * fill), 8), Color.White);
                return;
            }

            foreach (Anm2Layer layer in bar.Layers)
            {
                Anm2Frame? frame = bar.FrameAt(anim, layer.Id, time);
                if (frame != null)
                    Anm2.DrawFrame(b, tex, frame, pos, IsaacSprite.Scale, Color.White, 1f);
            }
        }

        /// <summary>Play Isaac's sound, or a Stardew sound if Isaac's sound files aren't available.</summary>
        private void PlaySfx(string isaacSound, string stardewFallback)
        {
            if (this.sounds?.Play(isaacSound) != true)
                Game1.playSound(stardewFallback);
        }

        /// <summary>A short line for the HUD: weapon and active tear effects.</summary>
        private string GetItemsLine()
        {
            string weapon = WeaponName(this.GetWeapon());
            int shots = this.GetMultiShot();
            if (shots > 1 && this.GetWeapon() is Weapon.Tears or Weapon.DrFetus)
                weapon += $" x{shots}";

            IEnumerable<string> effects = this.GetFlags()
                .Select(f => f.Replace('_', ' ').ToLowerInvariant())
                .OrderBy(f => f)
                .Take(6);
            string list = string.Join(", ", effects);
            return list.Length > 0 ? $"{weapon}  |  {list}" : weapon;
        }
    }
}
