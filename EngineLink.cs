using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;

namespace IsaacValley
{
    /// <summary>Something Isaac's engine is drawing right now (a tear, laser, knife, bomb, familiar or effect).</summary>
    internal sealed class EngineEntity
    {
        public int Type, Variant, SubType, Index;
        public Vector2 Pos;          // on the ground, Stardew world pixels
        public float Height;         // drawn this many pixels above/below the ground (negative = up)
        public string File = "";     // Isaac's animation file, e.g. "gfx/002.000_tear.anm2"
        public string Anim = "";     // the animation it's playing ("" = unknown)
        public int Frame;
        public float Rotation;       // degrees
        public Vector2 Scale = Vector2.One;
        public bool FlipX;
        public Color Tint = Color.White;
        public float TearScale;      // tears only
        public Vector2? End;         // lasers only: where the laser ends
        public HashSet<string>? Layers; // only these layers are shown (Isaac hides the rest), null = all
    }

    /// <summary>Isaac himself, exactly as the game is animating him.</summary>
    internal sealed class EnginePlayer
    {
        public string File = "";
        public string BodyAnim = "";  // e.g. WalkDown
        public int BodyFrame;
        public string HeadAnim = "";  // e.g. HeadLeft (Isaac's head is an overlay animation)
        public int HeadFrame;
        public Color Tint = Color.White;
        public int FireDelay, MaxFireDelay, HeadFrameDelay;
        public string Raw = "";
    }

    /// <summary>One update from Isaac's engine.</summary>
    internal sealed class EngineFrame
    {
        public List<EngineEntity> Entities = new();
        public Dictionary<int, float> Hits = new();               // stand-in id -> total damage so far
        public Dictionary<int, HashSet<string>> Status = new();   // stand-in id -> FREEZE, SLOW, ...
        public Vector2 RoomHalf = new(780, 420);                   // half of Isaac's room, in Stardew pixels
        public int HeadDir = -1;                                   // Isaac's Direction: 0 left, 1 up, 2 right, 3 down
        public EnginePlayer? Player;
        public string ChargeKind = "";   // "brimstone", "knife" or "" (no charge weapon)
        public float Charge, ChargeMax;  // brimstone: frames / frames needed; knife: 0..1 / 1
    }

    /// <summary>
    /// The fast link to Isaac's engine: two small files in the bridge mod's folder.
    /// Stardew writes link_in.txt (your input, position, monsters); Isaac writes link_out.txt
    /// (everything it draws, plus hits). Both are replaced in one step so neither side reads half a file.
    /// </summary>
    internal sealed class EngineLink
    {
        private readonly string inPath;
        private readonly string outPath;
        private string lastText = "";
        private DateTime lastChangeUtc = DateTime.MinValue;
        private int seq;

        public EngineFrame? Frame { get; private set; }

        /// <summary>Isaac's engine sent something in the last second.</summary>
        public bool Live => this.Frame != null && (DateTime.UtcNow - this.lastChangeUtc).TotalSeconds < 1.0;

        public EngineLink(string bridgeFolder)
        {
            this.inPath = Path.Combine(bridgeFolder, "link_in.txt");
            this.outPath = Path.Combine(bridgeFolder, "link_out.txt");
        }

        public int NextSeq() => ++this.seq;

        public void Write(string text)
        {
            try
            {
                string? dir = Path.GetDirectoryName(this.inPath);
                if (dir == null || !Directory.Exists(dir))
                    return;
                string tmp = this.inPath + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false)); // no invisible marker at the start; Isaac reads plain text
                File.Move(tmp, this.inPath, overwrite: true);
            }
            catch (IOException)
            {
                // Isaac was reading at that moment; we write again next tick
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Read Isaac's latest update. Returns true if there was a new one.</summary>
        public bool Read()
        {
            string text;
            try
            {
                if (!File.Exists(this.outPath))
                    return false;
                text = File.ReadAllText(this.outPath);
            }
            catch (IOException)
            {
                return false;
            }

            if (text == this.lastText || !text.EndsWith("\n"))
                return false; // nothing new, or caught Isaac mid-write
            this.lastText = text;
            this.lastChangeUtc = DateTime.UtcNow;
            this.Frame = Parse(text);
            return true;
        }

        private static float F(string s) => float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (int)F(s);

        private static EngineFrame Parse(string text)
        {
            var frame = new EngineFrame();
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line[..eq];
                string[] p = line[(eq + 1)..].Split('|');

                switch (key)
                {
                    case "e" when p.Length >= 24:
                        var e = new EngineEntity
                        {
                            Type = I(p[0]),
                            Variant = I(p[1]),
                            SubType = I(p[2]),
                            Index = I(p[3]),
                            Pos = new Vector2(F(p[4]), F(p[5])),
                            Height = F(p[6]),
                            File = p[7],
                            Anim = p[8],
                            Frame = I(p[9]),
                            Rotation = F(p[10]),
                            Scale = new Vector2(F(p[11]), F(p[12])),
                            FlipX = p[13] == "1",
                            TearScale = F(p[21])
                        };
                        // Isaac's color: tint plus offset; close enough to tint + offset, clamped
                        float r = Math.Clamp(F(p[14]) + F(p[18]), 0f, 1f);
                        float g = Math.Clamp(F(p[15]) + F(p[19]), 0f, 1f);
                        float bl = Math.Clamp(F(p[16]) + F(p[20]), 0f, 1f);
                        float a = Math.Clamp(F(p[17]), 0f, 1f);
                        e.Tint = new Color(r, g, bl) * a;
                        if (p[22].Length > 0 && p[23].Length > 0)
                            e.End = new Vector2(F(p[22]), F(p[23]));
                        if (p.Length >= 25 && p[24].Length > 0)
                            e.Layers = new HashSet<string>(p[24].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                        frame.Entities.Add(e);
                        break;

                    case "hit" when p.Length >= 2:
                        frame.Hits[I(p[0])] = F(p[1]);
                        break;

                    case "status" when p.Length >= 2:
                        frame.Status[I(p[0])] = new HashSet<string>(p[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
                        break;

                    case "roomHalf" when p.Length >= 2:
                        frame.RoomHalf = new Vector2(Math.Max(100, F(p[0])), Math.Max(100, F(p[1])));
                        break;

                    case "player" when p.Length >= 15:
                        frame.Player = new EnginePlayer
                        {
                            File = p[0],
                            BodyAnim = p[1],
                            BodyFrame = I(p[2]),
                            HeadAnim = p[3],
                            HeadFrame = I(p[4]),
                            Tint = new Color(
                                Math.Clamp(F(p[5]) + F(p[9]), 0f, 1f),
                                Math.Clamp(F(p[6]) + F(p[10]), 0f, 1f),
                                Math.Clamp(F(p[7]) + F(p[11]), 0f, 1f)) * Math.Clamp(F(p[8]), 0f, 1f),
                            FireDelay = I(p[12]),
                            MaxFireDelay = I(p[13]),
                            HeadFrameDelay = I(p[14]),
                            Raw = line[(eq + 1)..]
                        };
                        break;

                    case "charge" when p.Length >= 3:
                        frame.ChargeKind = p[0];
                        frame.Charge = F(p[1]);
                        frame.ChargeMax = Math.Max(0.01f, F(p[2]));
                        break;

                    case "headDir":
                        frame.HeadDir = I(p[0]);
                        break;
                }
            }
            return frame;
        }
    }
}
