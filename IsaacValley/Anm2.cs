using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace IsaacValley
{
    /// <summary>One frame of one layer in an Isaac animation.</summary>
    internal sealed class Anm2Frame
    {
        public Vector2 Position;     // offset from the entity, in Isaac pixels
        public Vector2 Pivot;
        public Vector2 Scale = Vector2.One;
        public float Rotation;       // degrees
        public Rectangle Crop;
        public int Delay = 1;        // in Isaac updates (30 per second)
        public bool Visible = true;
        public Color Tint = Color.White;
        public Vector3 Offset;       // Isaac's color offset (added to the color), 0..1
    }

    internal sealed class Anm2Animation
    {
        public string Name = "";
        public int FrameNum;
        public bool Loop;
        public Dictionary<int, List<Anm2Frame>> Layers = new(); // layer id -> frames
    }

    internal sealed class Anm2Layer
    {
        public int Id;
        public string Name = "";
        public int SheetId;
    }

    /// <summary>An Isaac .anm2 animation file: sprite sheets, layers and animations.</summary>
    internal sealed class Anm2
    {
        public string Path = "";
        public Dictionary<int, string> Sheets = new();          // sheet id -> path as written in the file
        public List<Anm2Layer> Layers = new();
        public Dictionary<string, Anm2Animation> Animations = new(StringComparer.OrdinalIgnoreCase);
        public List<string> AnimationNames = new();  // in file order
        public string DefaultAnimation = "";

        private static float F(XElement e, string name, float fallback = 0f)
        {
            string? v = (string?)e.Attribute(name);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        private static bool B(XElement e, string name, bool fallback = true)
        {
            string? v = (string?)e.Attribute(name);
            return v == null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static Anm2? Load(string path)
        {
            if (!File.Exists(path))
                return null;

            XDocument doc = XDocument.Load(path);
            var anm = new Anm2 { Path = path };

            foreach (XElement s in doc.Descendants("Spritesheet"))
                anm.Sheets[(int)F(s, "Id")] = ((string?)s.Attribute("Path") ?? "").Replace('\\', '/');

            foreach (XElement l in doc.Descendants("Layer"))
                anm.Layers.Add(new Anm2Layer { Id = (int)F(l, "Id"), Name = (string?)l.Attribute("Name") ?? "", SheetId = (int)F(l, "SpritesheetId") });

            XElement? animations = doc.Descendants("Animations").FirstOrDefault();
            anm.DefaultAnimation = (string?)animations?.Attribute("DefaultAnimation") ?? "";

            foreach (XElement a in doc.Descendants("Animation"))
            {
                var anim = new Anm2Animation
                {
                    Name = (string?)a.Attribute("Name") ?? "",
                    FrameNum = (int)F(a, "FrameNum", 1),
                    Loop = B(a, "Loop", false)
                };

                foreach (XElement la in a.Descendants("LayerAnimation"))
                {
                    if (!B(la, "Visible"))
                        continue;
                    var frames = new List<Anm2Frame>();
                    foreach (XElement f in la.Elements("Frame"))
                    {
                        frames.Add(new Anm2Frame
                        {
                            Position = new Vector2(F(f, "XPosition"), F(f, "YPosition")),
                            Pivot = new Vector2(F(f, "XPivot"), F(f, "YPivot")),
                            Scale = new Vector2(F(f, "XScale", 100) / 100f, F(f, "YScale", 100) / 100f),
                            Rotation = F(f, "Rotation"),
                            Crop = new Rectangle((int)F(f, "XCrop"), (int)F(f, "YCrop"), (int)F(f, "Width"), (int)F(f, "Height")),
                            Delay = Math.Max(1, (int)F(f, "Delay", 1)),
                            Visible = B(f, "Visible"),
                            Tint = new Color((int)F(f, "RedTint", 255), (int)F(f, "GreenTint", 255), (int)F(f, "BlueTint", 255), (int)F(f, "AlphaTint", 255)),
                            Offset = new Vector3(F(f, "RedOffset"), F(f, "GreenOffset"), F(f, "BlueOffset")) / 255f
                        });
                    }
                    if (frames.Count > 0)
                        anim.Layers[(int)F(la, "LayerId")] = frames;
                }

                anm.Animations[anim.Name] = anim;
                anm.AnimationNames.Add(anim.Name);
            }

            return anm;
        }

        /// <summary>The frame a layer shows at a given time (in Isaac updates), or null.</summary>
        public Anm2Frame? FrameAt(string animation, int layerId, int time)
        {
            if (!this.Animations.TryGetValue(animation, out Anm2Animation? anim) || !anim.Layers.TryGetValue(layerId, out List<Anm2Frame>? frames))
                return null;

            int total = frames.Sum(f => f.Delay);
            if (total <= 0)
                return frames[0];
            time = anim.Loop ? ((time % total) + total) % total : Math.Clamp(time, 0, total - 1);

            foreach (Anm2Frame f in frames)
            {
                if (time < f.Delay)
                    return f;
                time -= f.Delay;
            }
            return frames[^1];
        }

        public bool HasLayerIn(string animation, int layerId)
        {
            return this.Animations.TryGetValue(animation, out Anm2Animation? anim) && anim.Layers.ContainsKey(layerId);
        }

        /// <summary>The animation to show: the one asked for if it exists, else the file's default, else the first.</summary>
        public string PickAnimation(string? wanted)
        {
            if (!string.IsNullOrEmpty(wanted) && this.Animations.ContainsKey(wanted))
                return wanted;
            if (this.Animations.ContainsKey(this.DefaultAnimation))
                return this.DefaultAnimation;
            return this.AnimationNames.Count > 0 ? this.AnimationNames[0] : "";
        }

        /// <summary>
        /// Draw one frame of an entity the way Isaac does, with the entity's own scale (negative x = mirrored)
        /// and rotation on top of the frame's.
        /// </summary>
        public static void DrawFrameTransformed(SpriteBatch b, Texture2D texture, Anm2Frame frame, Vector2 origin, Vector2 scale, float rotationDegrees, Color color, float depth)
        {
            if (!frame.Visible || frame.Crop.Width <= 0 || frame.Crop.Height <= 0)
                return;

            Vector2 total = frame.Scale * scale;   // component-wise
            bool flipX = total.X < 0;
            bool flipY = total.Y < 0;
            var effects = SpriteEffects.None;
            if (flipX) effects |= SpriteEffects.FlipHorizontally;
            if (flipY) effects |= SpriteEffects.FlipVertically;

            Vector2 pivot = new(
                flipX ? frame.Crop.Width - frame.Pivot.X : frame.Pivot.X,
                flipY ? frame.Crop.Height - frame.Pivot.Y : frame.Pivot.Y);

            float rad = MathHelper.ToRadians(rotationDegrees);
            Vector2 offset = frame.Position * scale;
            offset = new Vector2(
                offset.X * (float)Math.Cos(rad) - offset.Y * (float)Math.Sin(rad),
                offset.X * (float)Math.Sin(rad) + offset.Y * (float)Math.Cos(rad));
            float frameRotation = (flipX ^ flipY) ? -frame.Rotation : frame.Rotation;

            Color tint = new(
                frame.Tint.R * color.R / 255,
                frame.Tint.G * color.G / 255,
                frame.Tint.B * color.B / 255,
                frame.Tint.A * color.A / 255);

            b.Draw(texture, origin + offset, frame.Crop, tint, MathHelper.ToRadians(frameRotation) + rad,
                pivot, new Vector2(Math.Abs(total.X), Math.Abs(total.Y)), effects, Math.Clamp(depth, 0f, 1f));
        }

        /// <summary>Draw one frame like Isaac does: offset, pivot, scale (negative = mirrored), rotation and tint.</summary>
        public static void DrawFrame(SpriteBatch b, Texture2D texture, Anm2Frame frame, Vector2 screenPos, float scale, Color color, float depth)
        {
            if (!frame.Visible || frame.Crop.Width <= 0 || frame.Crop.Height <= 0)
                return;

            bool flipX = frame.Scale.X < 0;
            bool flipY = frame.Scale.Y < 0;
            var effects = SpriteEffects.None;
            if (flipX) effects |= SpriteEffects.FlipHorizontally;
            if (flipY) effects |= SpriteEffects.FlipVertically;

            // when mirrored, the pivot is mirrored too
            Vector2 pivot = new(
                flipX ? frame.Crop.Width - frame.Pivot.X : frame.Pivot.X,
                flipY ? frame.Crop.Height - frame.Pivot.Y : frame.Pivot.Y);

            Color tint = new(
                frame.Tint.R * color.R / 255,
                frame.Tint.G * color.G / 255,
                frame.Tint.B * color.B / 255,
                frame.Tint.A * color.A / 255);

            b.Draw(texture, screenPos + frame.Position * scale, frame.Crop, tint, MathHelper.ToRadians(frame.Rotation),
                pivot, new Vector2(Math.Abs(frame.Scale.X), Math.Abs(frame.Scale.Y)) * scale, effects, Math.Clamp(depth, 0f, 1f));
        }
    }

    /// <summary>Loads Isaac files (anm2 and png) from the extracted gfx folder, once each.</summary>
    internal sealed class IsaacFiles
    {
        private readonly string gfx;
        private readonly Dictionary<string, Anm2?> anims = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D?> textures = new(StringComparer.OrdinalIgnoreCase);

        public IsaacFiles(string gfxFolder)
        {
            this.gfx = gfxFolder;
        }

        public string GfxFolder => this.gfx;

        /// <summary>Load an anm2 by path relative to the gfx folder.</summary>
        public Anm2? Anim(string relativePath)
        {
            if (this.anims.TryGetValue(relativePath, out Anm2? cached))
                return cached;
            Anm2? anm = null;
            try
            {
                anm = Anm2.Load(this.Resolve(relativePath));
            }
            catch
            {
                // a broken file just means no costume
            }
            this.anims[relativePath] = anm;
            return anm;
        }

        /// <summary>Load a png by path relative to the gfx folder.</summary>
        public Texture2D? Texture(string relativePath)
        {
            if (this.textures.TryGetValue(relativePath, out Texture2D? cached))
                return cached;
            Texture2D? tex = null;
            try
            {
                string path = this.Resolve(relativePath);
                if (File.Exists(path))
                    tex = Texture2D.FromFile(Game1.graphics.GraphicsDevice, path);
            }
            catch
            {
                // missing sprite: skip it
            }
            this.textures[relativePath] = tex;
            return tex;
        }

        private readonly Dictionary<string, Texture2D?> silhouettes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The same png in plain white (same shape), for Isaac's color flashes.</summary>
        public Texture2D? Silhouette(string relativePath)
        {
            if (this.silhouettes.TryGetValue(relativePath, out Texture2D? cached))
                return cached;
            Texture2D? white = null;
            Texture2D? tex = this.Texture(relativePath);
            if (tex != null)
            {
                var data = new Color[tex.Width * tex.Height];
                tex.GetData(data);
                for (int i = 0; i < data.Length; i++)
                    data[i] = new Color(data[i].A, data[i].A, data[i].A, data[i].A); // premultiplied white
                white = new Texture2D(Game1.graphics.GraphicsDevice, tex.Width, tex.Height);
                white.SetData(data);
            }
            this.silhouettes[relativePath] = white;
            return white;
        }

        public bool Exists(string relativePath) => File.Exists(this.Resolve(relativePath));

        /// <summary>Path relative to gfx for a file name as Isaac reports it ("gfx/002.000_tear.anm2").</summary>
        public static string FromGamePath(string gamePath)
        {
            string p = gamePath.Replace('\\', '/').TrimStart('/');
            return p.StartsWith("gfx/", StringComparison.OrdinalIgnoreCase) ? p[4..] : p;
        }

        private string Resolve(string relativePath)
        {
            string clean = relativePath.Replace('\\', '/').TrimStart('/');
            string path = System.IO.Path.Combine(this.gfx, clean);
            if (File.Exists(path))
                return path;
            // the files on disk are lower-case; the game's own paths sometimes aren't
            string lower = System.IO.Path.Combine(this.gfx, clean.ToLowerInvariant());
            return File.Exists(lower) ? lower : path;
        }
    }
}
