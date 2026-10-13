using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace IsaacValley
{
    /// <summary>
    /// Loads Isaac's sprites from the player's own extracted Isaac files and draws them.
    /// Frame positions come from Isaac's 001.000_player.anm2, ui_hearts.anm2 and 002.000_tear.anm2.
    /// </summary>
    internal sealed class IsaacSprite
    {
        public Texture2D? Body { get; private set; }   // gfx/characters/costumes/character_001_isaac.png
        public Texture2D? Tears { get; private set; }  // gfx/tears.png
        public Texture2D? Hearts { get; private set; } // gfx/ui/ui_hearts.png
        public Texture2D? Bomb { get; private set; }   // gfx/items/pick ups/pickup_016_bomb.png
        public Texture2D? Explosion { get; private set; } // gfx/effects/effect_029_explosion.png
        public Texture2D? Pickups { get; private set; } // gfx/ui/hudpickups.png
        public Texture2D? Knife { get; private set; }   // gfx/effects/effect_momsknife.png
        public Texture2D? Lasers { get; private set; }  // gfx/effects/effect_018_lasereffects.png
        public bool Loaded => this.Body != null;

        public const float Scale = 3f;       // Isaac's sprites are tiny; 3x looks right next to Stardew
        public const float HeartScale = 3f;

        // ---------- frames (x, y) on character_001_isaac.png, each 32x32 ----------
        // walk cycles: 10 frames, each shown for 2 Isaac updates (= 4 Stardew ticks)
        private static readonly Point[] WalkVertical =
        {
            new(0, 32), new(32, 32), new(64, 32), new(96, 32), new(128, 32),
            new(160, 32), new(192, 32), new(224, 32), new(192, 0), new(224, 0)
        };
        private static readonly Point[] WalkSideways =
        {
            new(0, 64), new(32, 64), new(64, 64), new(96, 64), new(128, 64),
            new(160, 64), new(192, 64), new(224, 64), new(0, 96), new(32, 96)
        };
        // heads: [looking, crying] for down / right / up (left = right flipped)
        private static readonly Point[] HeadDown = { new(0, 0), new(32, 0) };
        private static readonly Point[] HeadRight = { new(64, 0), new(96, 0) };
        private static readonly Point[] HeadUp = { new(128, 0), new(160, 0) };

        // pivots and offsets from the anm2 file
        private static readonly Vector2 BodyPivot = new(16, 25);
        private static readonly Vector2 BodyOffset = new(0, 2);
        private static readonly Vector2 HeadPivot = new(16, 28);
        private static readonly Vector2 HeadOffset = new(0, -5);

        // ---------- ui_hearts.png, each 16x16 ----------
        public static readonly Rectangle RedFull = new(0, 0, 16, 16);
        public static readonly Rectangle RedHalf = new(16, 0, 16, 16);
        public static readonly Rectangle Empty = new(32, 0, 16, 16);
        public static readonly Rectangle SoulFull = new(0, 16, 16, 16);
        public static readonly Rectangle SoulHalf = new(16, 16, 16, 16);

        // ---------- tears.png: RegularTear1..13, bigger tears for more damage ----------
        public static Rectangle TearForDamage(float damage)
        {
            // Isaac's tears grow with damage: 3.5 damage = RegularTear6
            int size = (int)Math.Round(6 * Math.Sqrt(Math.Max(0.1f, damage) / 3.5f));
            size = Math.Clamp(size, 1, 13);
            return size <= 8
                ? new Rectangle((size - 1) * 32, 0, 32, 32)
                : new Rectangle((size - 9) * 32, 32, 32, 32);
        }

        // ---------- bomb: pickup_016_bomb.png, pivot (16, 25) ----------
        public static readonly Rectangle BombFrame = new(0, 0, 32, 32);
        public static readonly Vector2 BombPivot = new(16, 25);

        // ---------- explosion: effect_029_explosion.png, 12 frames of 96x96, pivot (48, 84) ----------
        public static readonly int[] ExplosionDelays = { 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2 }; // in Isaac updates
        public static Rectangle ExplosionFrame(int i) => new((i % 4) * 96, (i / 4) * 96, 96, 96);
        public static readonly Vector2 ExplosionPivot = new(48, 84);

        // ---------- Mom's Knife: effect_momsknife.png, first frame points down ----------
        public static readonly Rectangle KnifeFrame = new(0, 0, 32, 32);

        // ---------- lasers: effect_018_lasereffects.png, vertical strips 32x64 ----------
        // Brimstone (thick, grey, tinted red by Isaac): 4 animation frames
        public static Rectangle ThickLaserFrame(uint tick) => new(192 + 32 * (int)(tick / 2 % 4), 0, 32, 64);
        // Technology (thin, already red): 3 animation frames
        public static Rectangle ThinLaserFrame(uint tick) => new(64 - 32 * (int)(tick / 2 % 3), 0, 32, 64);

        // ---------- hudpickups.png, 16x16 ----------
        public static readonly Rectangle CoinIcon = new(0, 0, 16, 16);
        public static readonly Rectangle KeyIcon = new(16, 0, 16, 16);
        public static readonly Rectangle BombIcon = new(0, 16, 16, 16);

        /// <summary>Load the textures. Returns an error message, or null if everything loaded.</summary>
        public string? Load(string gfxFolder)
        {
            try
            {
                this.Body = LoadTexture(Path.Combine(gfxFolder, "characters", "costumes", "character_001_isaac.png"));
                this.Tears = LoadTexture(Path.Combine(gfxFolder, "tears.png"));
                this.Hearts = LoadTexture(Path.Combine(gfxFolder, "ui", "ui_hearts.png"));
                this.Bomb = LoadTexture(Path.Combine(gfxFolder, "items", "pick ups", "pickup_016_bomb.png"));
                this.Explosion = LoadTexture(Path.Combine(gfxFolder, "effects", "effect_029_explosion.png"));
                this.Pickups = LoadTexture(Path.Combine(gfxFolder, "ui", "hudpickups.png"));
                this.Knife = LoadTexture(Path.Combine(gfxFolder, "effects", "effect_momsknife.png"));
                this.Lasers = LoadTexture(Path.Combine(gfxFolder, "effects", "effect_018_lasereffects.png"));
                return this.Body == null ? $"Isaac's sprites not found in {gfxFolder}" : null;
            }
            catch (Exception ex)
            {
                this.Body = null;
                return $"Couldn't load Isaac's sprites: {ex.Message}";
            }
        }

        private static Texture2D? LoadTexture(string path)
        {
            return File.Exists(path)
                ? Texture2D.FromFile(Game1.graphics.GraphicsDevice, path)
                : null;
        }

        /// <summary>Draw Isaac standing at a world position (his feet).</summary>
        /// <param name="facing">0 = up, 1 = right, 2 = down, 3 = left (Stardew's numbers).</param>
        public void DrawIsaac(SpriteBatch b, Vector2 feetWorld, int bodyFacing, int headFacing, bool walking, int walkFrame, bool crying, float alpha)
        {
            if (this.Body == null)
                return;

            Vector2 feet = Game1.GlobalToLocal(Game1.viewport, feetWorld);
            float depth = Math.Max(0f, feetWorld.Y / 10000f);
            Color color = Color.White * alpha;

            // shadow
            Rectangle shadow = Game1.shadowTexture.Bounds;
            b.Draw(Game1.shadowTexture, feet, shadow, Color.White * alpha, 0f,
                new Vector2(shadow.Center.X, shadow.Center.Y), 3f, SpriteEffects.None, Math.Max(0f, depth - 0.0001f));

            // body
            bool sideways = bodyFacing == 1 || bodyFacing == 3;
            Point bodyFrame = walking
                ? (sideways ? WalkSideways : WalkVertical)[walkFrame % 10]
                : WalkVertical[0];
            SpriteEffects bodyFlip = walking && bodyFacing == 3 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            b.Draw(this.Body, feet + BodyOffset * Scale, new Rectangle(bodyFrame.X, bodyFrame.Y, 32, 32), color, 0f,
                BodyPivot, Scale, bodyFlip, depth + 0.00010f);

            // head (drawn on top of the body)
            Point[] head = headFacing switch
            {
                0 => HeadUp,
                1 or 3 => HeadRight,
                _ => HeadDown
            };
            Point headFrame = head[crying ? 1 : 0];
            SpriteEffects headFlip = headFacing == 3 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            b.Draw(this.Body, feet + HeadOffset * Scale, new Rectangle(headFrame.X, headFrame.Y, 32, 32), color, 0f,
                HeadPivot, Scale, headFlip, depth + 0.00020f);
        }
    }
}
