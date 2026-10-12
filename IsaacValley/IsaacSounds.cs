using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;

namespace IsaacValley
{
    /// <summary>Plays Isaac's own sound effects, from the extracted sfx folder.</summary>
    internal sealed class IsaacSounds
    {
        // Isaac's sound files (from sounds.xml); one is picked at random each time, like the game does
        private static readonly Dictionary<string, string[]> Sounds = new()
        {
            ["TearFire"] = new[] { "tear fire 4.wav", "tear fire 5.wav" },
            ["TearSplash"] = new[] { "tear block.wav" },
            ["Brimstone"] = new[] { "blood laser long.wav" },
            ["Technology"] = new[] { "red lightning zap1.wav", "red lightning zap 2.wav", "red lightning zap 3.wav" },
            ["Explosion"] = new[] { "boss explosions 0.wav", "boss explosions 1.wav", "boss expolsions 2.wav" },
            ["Hurt"] = new[] { "Player/hurt grunt .wav", "Player/hurt grunt 1.wav", "Player/hurt grunt 2.wav" },
        };

        private readonly string sfxFolder;
        private readonly IMonitor monitor;
        private readonly Dictionary<string, SoundEffect?> loaded = new(StringComparer.OrdinalIgnoreCase);
        private readonly Random rng = new();

        public IsaacSounds(string gfxFolder, IMonitor monitor)
        {
            // the sfx folder sits next to gfx
            this.sfxFolder = Path.Combine(Path.GetDirectoryName(gfxFolder.TrimEnd('/', '\\')) ?? "", "sfx");
            this.monitor = monitor;
        }

        public bool Available => Directory.Exists(this.sfxFolder);

        /// <summary>Play an Isaac sound. Returns false if it couldn't (then the caller can use a Stardew sound).</summary>
        public bool Play(string name, float volume = 1f)
        {
            if (!Sounds.TryGetValue(name, out string[]? files))
                return false;

            SoundEffect? sound = this.Load(files[this.rng.Next(files.Length)]);
            if (sound == null)
                return false;

            try
            {
                float v = Math.Clamp(Game1.options.soundVolumeLevel * volume, 0f, 1f);
                if (v > 0f)
                    sound.Play(v, 0f, 0f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private SoundEffect? Load(string file)
        {
            if (this.loaded.TryGetValue(file, out SoundEffect? cached))
                return cached;

            SoundEffect? sound = null;
            try
            {
                string path = Path.Combine(this.sfxFolder, file);
                if (!File.Exists(path))
                    path = Path.Combine(this.sfxFolder, file.ToLowerInvariant());
                if (File.Exists(path))
                    sound = SoundEffect.FromFile(path);
            }
            catch (Exception ex)
            {
                this.monitor.LogOnce($"Couldn't load Isaac sound '{file}': {ex.Message}", LogLevel.Trace);
            }

            this.loaded[file] = sound;
            return sound;
        }
    }
}
