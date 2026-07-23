using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using TCNNOutfits.Core;

namespace TCNNOutfits.Content
{
    public sealed class ContentPackSource : IOutfitSource
    {
        private readonly string _root;
        private readonly ManualLogSource _log;

        public ContentPackSource(string root, ManualLogSource log)
        {
            _root = root;
            _log = log;
        }

        public string Name => "contentpack";

        public IEnumerable<OutfitDefinition> Load()
        {
            var results = new List<OutfitDefinition>();
            if (!Directory.Exists(_root))
                return results;  // no packs installed — perfectly fine

            foreach (var dir in Directory.GetDirectories(_root))
            {
                var manifestPath = Path.Combine(dir, "outfit.json");
                if (!File.Exists(manifestPath)) continue;

                OutfitManifest m;
                try
                {
                    m = JsonConvert.DeserializeObject<OutfitManifest>(File.ReadAllText(manifestPath));
                }
                catch (Exception e)
                {
                    _log.LogWarning($"Bad outfit.json in '{dir}': {e.Message}");
                    continue;
                }
                if (m == null || string.IsNullOrEmpty(m.Id) || string.IsNullOrEmpty(m.BaseSkin))
                {
                    _log.LogWarning($"Incomplete manifest in '{dir}' (need at least Id and BaseSkin).");
                    continue;
                }

                var texturePath = string.IsNullOrEmpty(m.Texture) ? null : Path.Combine(dir, m.Texture);
                results.Add(new OutfitDefinition
                {
                    Id = m.Id,
                    DisplayName = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name,
                    Author = m.Author ?? "",
                    Version = m.Version ?? "",
                    TargetSkeletons = (m.Skeletons != null && m.Skeletons.Length > 0)
                        ? new List<string>(m.Skeletons) : null,
                    BaseSkin = m.BaseSkin,
                    Art = texturePath != null ? (IOutfitArt)new TextureFileArt(texturePath) : null,
                });
            }
            return results;
        }
    }

    public sealed class OutfitManifest
    {
        public string Id;
        public string Name;
        public string Author;
        public string Version;
        public string BaseSkin;  // e.g. "outfits/default/coat"
        public string[] Skeletons;  // e.g. ["PortraitZoey","Zoey_overworld"]; null
        public string Texture;  // file name within the pack folder
    }
}
