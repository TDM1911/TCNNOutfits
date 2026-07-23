using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Spine;
using UnityEngine;

namespace TCNNOutfits.Spine
{
    public sealed class SkinImporter
    {
        private readonly ManualLogSource _log;

        private readonly Dictionary<Material, Texture> _originalTextures = new Dictionary<Material, Texture>();

        public SkinImporter(ManualLogSource log) { _log = log; }

        public Dictionary<string, Texture> LoadFiles(Skeleton skeleton, string skinName, string folder)
            => LoadPagesForSkin(skeleton?.Data, skinName, folder);

        public Dictionary<string, Texture> LoadPagesForSkin(SkeletonData data, string skinName, string folder)
        {
            var result = new Dictionary<string, Texture>();
            var skin = data?.FindSkin(skinName);
            if (skin == null) { _log.LogWarning($"Import: skin '{skinName}' not found."); return result; }
            if (!Directory.Exists(folder)) { _log.LogWarning($"Import: no export folder at {folder}. Run outfit.export first."); return result; }

            var pageNames = new HashSet<string>();
            foreach (var entry in skin.Attachments)
            {
                var region = (entry.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                if (region?.page != null) pageNames.Add(region.page.name);
            }

            foreach (var pn in pageNames)
            {
                string file = Path.Combine(folder, SkinExporter.PageFileName(pn));
                if (!File.Exists(file)) { _log.LogWarning($"Import: missing edited page '{file}'."); continue; }
                var tex = LoadTexture(file);
                if (tex != null) result[pn] = tex;
            }
            _log.LogInfo($"Import: loaded {result.Count} edited page(s) for '{skinName}'.");
            return result;
        }

        public int ApplyTo(Skeleton skeleton, string skinName, Dictionary<string, Texture> pageMap)
        {
            var skin = skeleton?.Data?.FindSkin(skinName);
            if (skin == null || pageMap == null || pageMap.Count == 0) return 0;

            var seen = new HashSet<Material>();
            int applied = 0;
            foreach (var entry in skin.Attachments)
            {
                var region = (entry.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                var page = region?.page;
                var mat = page?.rendererObject as Material;
                if (page == null || mat == null || seen.Contains(mat)) continue;

                if (pageMap.TryGetValue(page.name, out var edited) && edited != null)
                {
                    if (!_originalTextures.ContainsKey(mat)) _originalTextures[mat] = mat.mainTexture;
                    mat.mainTexture = edited;
                    seen.Add(mat);
                    applied++;
                }
            }
            if (applied > 0) _log.LogInfo($"Import: swapped {applied} atlas page texture(s) for '{skinName}' (persistent).");
            return applied;
        }

        public int RestoreAll()
        {
            int n = 0;
            foreach (var kv in _originalTextures)
            {
                if (kv.Key != null) { kv.Key.mainTexture = kv.Value; n++; }
            }
            _originalTextures.Clear();
            if (n > 0) _log.LogInfo($"Import: restored {n} original atlas texture(s).");
            return n;
        }

        private static Texture2D LoadTexture(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
    }
}
