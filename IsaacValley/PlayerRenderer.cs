using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace IsaacValley
{
    /// <summary>An item costume from Isaac's costumes2.xml.</summary>
    internal sealed class CostumeInfo
    {
        public int ItemId;
        public string Anm2Path = "";   // relative to gfx/characters/
        public int Priority;
        public int SkinColor = -1;     // -1 = doesn't change Isaac's skin
        public bool HasSkinAlt;
    }

    /// <summary>What Isaac's head is doing (Brimstone-style costumes have their own animations for these).</summary>
    internal enum HeadMode { Normal, Charge, ChargeFull, Shoot }

    /// <summary>
    /// Draws Isaac exactly the way the game does: the player animation from 001.000_player.anm2,
    /// with every item costume layered on top using Isaac's own costume files and rules.
    /// </summary>
    internal sealed class PlayerRenderer
    {
        private readonly IsaacFiles files;
        private readonly Anm2? player;
        private readonly Dictionary<int, CostumeInfo> costumes = new();
        private readonly Dictionary<int, CostumeInfo> nullCostumes = new(); // costumes not tied to an item (transformations)

        // Isaac's transformation number -> its costume in costumes2.xml
        private static readonly int[] FormCostume = { 5, 3, 20, 21, 22, 23, 24, 25, 26, 27, 37, 38, 39 };

        // Isaac's player layers, bottom to top
        private static readonly string[] Slots = { "body", "body0", "body1", "head", "head0", "head1", "head2", "head3", "head4", "head5", "top0" };

        // Isaac's SkinColor values -> sprite sheet suffix
        private static readonly string[] SkinSuffix = { "_white", "_black", "_blue", "_red", "_green", "_grey" };

        public bool Ready => this.player != null;
        public int CostumeCount => this.costumes.Count;

        public PlayerRenderer(IsaacFiles files)
        {
            this.files = files;
            this.player = files.Anim("001.000_player.anm2");
            this.LoadCostumeList();
        }

        private void LoadCostumeList()
        {
            // costumes2.xml sits next to the gfx folder
            string path = Path.Combine(Path.GetDirectoryName(this.files.GfxFolder.TrimEnd('/', '\\')) ?? "", "costumes2.xml");
            if (!File.Exists(path))
                return;

            foreach (XElement c in XDocument.Load(path).Descendants("costume"))
            {
                string type = (string?)c.Attribute("type") ?? "";
                bool passive = type.Equals("passive", StringComparison.OrdinalIgnoreCase);
                bool none = type.Equals("none", StringComparison.OrdinalIgnoreCase);
                if (!passive && !none)
                    continue;
                if (!int.TryParse((string?)c.Attribute("id"), out int id))
                    continue;
                Dictionary<int, CostumeInfo> list = passive ? this.costumes : this.nullCostumes;
                if (list.ContainsKey(id))
                    continue;

                list[id] = new CostumeInfo
                {
                    ItemId = id,
                    Anm2Path = ((string?)c.Attribute("anm2path") ?? "").Replace('\\', '/'),
                    Priority = int.TryParse((string?)c.Attribute("priority"), out int p) ? p : 0,
                    SkinColor = int.TryParse((string?)c.Attribute("skinColor"), out int s) ? s : -1,
                    HasSkinAlt = string.Equals((string?)c.Attribute("hasSkinAlt"), "true", StringComparison.OrdinalIgnoreCase)
                };
            }
        }

        /// <summary>The costumes for the items Isaac is holding, in drawing order.</summary>
        public List<CostumeInfo> CostumesFor(IEnumerable<int> itemIds, IEnumerable<int>? forms = null)
        {
            IEnumerable<CostumeInfo> transformations = (forms ?? Enumerable.Empty<int>())
                .Where(f => f >= 0 && f < FormCostume.Length && this.nullCostumes.ContainsKey(FormCostume[f]))
                .Select(f => this.nullCostumes[FormCostume[f]]);
            return itemIds
                .Distinct()
                .Where(this.costumes.ContainsKey)
                .Select(id => this.costumes[id])
                .Concat(transformations)
                .Distinct()
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.ItemId)
                .ToList();
        }

        /// <summary>The skin color items give Isaac, if Isaac didn't tell us directly.</summary>
        public static int SkinFromCostumes(List<CostumeInfo> worn)
        {
            CostumeInfo? top = worn.Where(c => c.SkinColor >= 0).OrderByDescending(c => c.Priority).FirstOrDefault();
            return top?.SkinColor ?? -1;
        }

        /// <summary>Path of a sheet, swapped for its skin-colored version when the costume has one.</summary>
        private string SheetPath(string anmDir, string sheet, int skin, bool hasSkinAlt)
        {
            string path = Path.Combine(anmDir, sheet).Replace('\\', '/');
            if (hasSkinAlt && skin >= 0 && skin < SkinSuffix.Length)
            {
                string alt = Path.ChangeExtension(path, null) + SkinSuffix[skin] + ".png";
                if (this.files.Exists(alt))
                    return alt;
            }
            return path;
        }

        /// <summary>Draw Isaac and his costumes.</summary>
        /// <param name="feetScreen">Isaac's position on screen.</param>
        /// <param name="bodyAnim">WalkDown / WalkUp / WalkLeft / WalkRight.</param>
        /// <param name="bodyTime">Time into the walk cycle, in Isaac updates.</param>
        /// <param name="headDir">Down / Up / Left / Right.</param>
        /// <param name="crying">Show the crying frame.</param>
        /// <param name="mode">Normal, or Brimstone-style charging/shooting.</param>
        /// <param name="modeTime">Time into the charge/shoot animation.</param>
        public void Draw(SpriteBatch b, Vector2 feetScreen, float scale, float depth, string bodyAnim, int bodyTime,
            string headDir, bool crying, HeadMode mode, int modeTime, List<CostumeInfo> worn, int bodySkin, int headSkin,
            string? exactHeadAnim = null, int exactHeadFrame = 0, Color? tint = null)
        {
            if (this.player == null)
                return;
            Color color = tint ?? Color.White;

            // when Isaac's engine tells us exactly which head animation is playing, use it
            bool exact = !string.IsNullOrEmpty(exactHeadAnim) && this.player.Animations.ContainsKey(exactHeadAnim);
            string headBase = exact ? exactHeadAnim! : "Head" + headDir;
            string headSpecial = mode switch
            {
                HeadMode.Charge => headBase + "Charge",
                HeadMode.ChargeFull => headBase + "ChargeFull",
                HeadMode.Shoot => headBase + "Shoot",
                _ => headBase
            };
            int headTime = exact ? exactHeadFrame : (crying ? 2 : 0); // Isaac's head: frame 1 = looking, frame 2 = crying
            string playerDir = Path.GetDirectoryName(this.player.Path) ?? "";
            string playerRel = Path.GetRelativePath(this.files.GfxFolder, playerDir);

            for (int slotIndex = 0; slotIndex < Slots.Length; slotIndex++)
            {
                string slot = Slots[slotIndex];
                bool isHead = !slot.StartsWith("body");
                int skin = isHead ? headSkin : bodySkin;
                float slotDepth = depth + slotIndex * 0.00002f;

                // 1. Isaac himself
                foreach (Anm2Layer layer in this.player.Layers.Where(l => l.Name == slot))
                {
                    string anim = isHead ? headBase : bodyAnim;
                    int time = isHead ? headTime : bodyTime;
                    Anm2Frame? frame = this.player.FrameAt(anim, layer.Id, time);
                    if (frame == null || !this.player.Sheets.TryGetValue(layer.SheetId, out string? sheet))
                        continue;
                    // Isaac's base sheet has a version for each skin color
                    Texture2D? tex = this.files.Texture(this.SheetPath(playerRel, sheet, skin, hasSkinAlt: true));
                    if (tex != null)
                        Anm2.DrawFrame(b, tex, frame, feetScreen, scale, color, slotDepth);
                }

                // 2. every costume layer that goes in this slot
                for (int c = 0; c < worn.Count; c++)
                {
                    CostumeInfo costume = worn[c];
                    Anm2? anm = this.files.Anim(Path.Combine("characters", costume.Anm2Path).Replace('\\', '/'));
                    if (anm == null)
                        continue;
                    string anmRel = Path.GetDirectoryName(Path.Combine("characters", costume.Anm2Path)) ?? "characters";

                    foreach (Anm2Layer layer in anm.Layers.Where(l => l.Name == slot))
                    {
                        if (!anm.Sheets.TryGetValue(layer.SheetId, out string? sheet))
                            continue;

                        Anm2Frame? frame = null;
                        if (isHead)
                        {
                            // the costume's own head animation (charge/shoot first, then normal)
                            if (anm.HasLayerIn(headSpecial, layer.Id))
                                frame = anm.FrameAt(headSpecial, layer.Id, mode == HeadMode.Normal ? headTime : modeTime);
                            else if (anm.HasLayerIn(headBase, layer.Id))
                                frame = anm.FrameAt(headBase, layer.Id, headTime);
                        }
                        else if (anm.HasLayerIn(bodyAnim, layer.Id))
                        {
                            frame = anm.FrameAt(bodyAnim, layer.Id, bodyTime);
                        }

                        // costumes without their own animation use Isaac's own head/body frames on the costume's sheet
                        if (frame == null && anm.Animations.Count == 0)
                        {
                            Anm2Layer? baseLayer = this.player.Layers.FirstOrDefault(l => l.Name == (isHead ? "head" : "body"));
                            if (baseLayer != null)
                                frame = this.player.FrameAt(isHead ? headBase : bodyAnim, baseLayer.Id, isHead ? headTime : bodyTime);
                        }

                        if (frame == null)
                            continue;

                        Texture2D? tex = this.files.Texture(this.SheetPath(anmRel, sheet, skin, costume.HasSkinAlt));
                        if (tex != null)
                            Anm2.DrawFrame(b, tex, frame, feetScreen, scale, color, slotDepth + (c + 1) * 0.000001f);
                    }
                }
            }
        }
    }
}
