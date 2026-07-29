using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using Spine;
using UnityEngine;

namespace TCNNOutfits.Spine
{
    public sealed class SkinExporter
    {
        private readonly ManualLogSource _log;
        public SkinExporter(ManualLogSource log) { _log = log; }

        public string Export(SkeletonData data, string skinName, string outRoot, string skeletonName)
        {
            var skin = data.FindSkin(skinName);
            if (skin == null) { _log.LogWarning($"Export: skin '{skinName}' not found on '{skeletonName}'."); return null; }

            var pages = new Dictionary<string, Texture2D>();  // page name -> atlas texture
            var regions = new List<RegionInfo>();

            foreach (var entry in skin.Attachments)
            {
                var region = (entry.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                if (region == null) continue;
                var page = region.page;
                var tex = (page?.rendererObject as Material)?.mainTexture as Texture2D;
                if (page == null || tex == null) continue;

                if (!pages.ContainsKey(page.name)) pages[page.name] = tex;

                regions.Add(new RegionInfo
                {
                    attachment = entry.Name,
                    slot = entry.SlotIndex,
                    page = page.name,
                    x = region.x, y = region.y,
                    width = region.packedWidth, height = region.packedHeight,
                    rotate = region.rotate, degrees = region.degrees,
                    offsetX = region.offsetX, offsetY = region.offsetY,
                    originalWidth = region.originalWidth, originalHeight = region.originalHeight,
                });
            }

            if (regions.Count == 0) { _log.LogWarning($"Export: skin '{skinName}' had no texture regions."); return null; }

            string dir = Path.Combine(outRoot, Sanitize(skinName));
            Directory.CreateDirectory(dir);

            int pagesWritten = 0;
            foreach (var kv in pages)
            {
                try
                {
                    string pageFile = PageFileName(kv.Key);
                    var readable = Readback(kv.Value);
                    File.WriteAllBytes(Path.Combine(dir, pageFile), readable.EncodeToPNG());
                    Object.Destroy(readable);
                    pagesWritten++;
                }
                catch (System.Exception e) { _log.LogError($"Export: failed to read page '{kv.Key}': {e.Message}"); }
            }

            var mapping = new ExportMapping
            {
                skeleton = skeletonName,
                skin = skinName,
                pages = new List<string>(pages.Keys),
                regions = regions,
            };
            File.WriteAllText(Path.Combine(dir, $"mapping.{Sanitize(skeletonName)}.json"), JsonConvert.SerializeObject(mapping, Formatting.Indented));

            _log.LogInfo($"Exported '{skinName}': {pagesWritten} page(s), {regions.Count} region(s) -> {dir}");
            return dir;
        }

        public bool ExportSprite(Sprite sprite, string dir, string fileName)
        {
            if (sprite == null || sprite.texture == null) return false;
            try
            {
                var readable = Readback(sprite.texture);
                var r = sprite.textureRect;                 // sprite may be a sub-rect of an atlas
                int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);

                var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                outTex.SetPixels(readable.GetPixels((int)r.x, (int)r.y, w, h));
                outTex.Apply(false, false);

                File.WriteAllBytes(Path.Combine(dir, fileName), outTex.EncodeToPNG());
                Object.Destroy(readable);
                Object.Destroy(outTex);
                _log.LogInfo($"Exported sprite -> {fileName} ({w}x{h})");
                return true;
            }
            catch (System.Exception e) { _log.LogError("Sprite export failed: " + e.Message); return false; }
        }

        // Write any atlas page texture (usually non-readable) to a PNG. Used by the skeleton dumper.
        public bool ExportTexture(Texture2D tex, string path)
        {
            if (tex == null) return false;
            try
            {
                var readable = Readback(tex);
                File.WriteAllBytes(path, readable.EncodeToPNG());
                Object.Destroy(readable);
                return true;
            }
            catch (System.Exception e) { _log.LogError($"Page export failed for '{path}': {e.Message}"); return false; }
        }

        private static Texture2D Readback(Texture2D src)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            readable.Apply(false, false);
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        internal static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace('/', '_').Replace(' ', '_');
        }

        internal static string PageFileName(string pageName)
        {
            string f = Sanitize(pageName);
            if (!f.ToLowerInvariant().EndsWith(".png")) f += ".png";
            return f;
        }

        private sealed class ExportMapping
        {
            public string skeleton;
            public string skin;
            public List<string> pages;
            public List<RegionInfo> regions;
        }

        private sealed class RegionInfo
        {
            public string attachment;
            public int slot;
            public string page;
            public int x, y, width, height;
            public bool rotate;
            public int degrees;
            public float offsetX, offsetY;
            public int originalWidth, originalHeight;
        }
    }
}
