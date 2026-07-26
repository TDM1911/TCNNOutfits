using System.Collections.Generic;
using BepInEx.Logging;
using Spine;
using Spine.Unity.AttachmentTools;
using UnityEngine;
using TCNNOutfits.Core;

namespace TCNNOutfits.Spine
{
    public sealed class SkinResolver
    {
        private readonly ManualLogSource _log;

        private readonly Dictionary<string, List<Skin.SkinEntry>> _vanilla = new Dictionary<string, List<Skin.SkinEntry>>();

        private readonly Dictionary<string, AtlasPage> _pageCache = new Dictionary<string, AtlasPage>();

        public SkinResolver(ManualLogSource log) { _log = log; }

        public void ClearCache() { _vanilla.Clear(); _pageCache.Clear(); }

        private static string SkinKey(SkeletonData data, string skinName) => skinName + "@" + data.GetHashCode();

        private void SnapshotVanilla(SkeletonData data, string skinName, Skin skin)
        {
            string key = SkinKey(data, skinName);
            if (_vanilla.ContainsKey(key)) return;
            var backup = new List<Skin.SkinEntry>();
            foreach (var e in skin.Attachments) backup.Add(e);
            _vanilla[key] = backup;
        }

        public bool RestoreSkin(SkeletonData data, string skinName)
        {
            var skin = data.FindSkin(skinName);
            string key = SkinKey(data, skinName);
            if (skin == null || !_vanilla.TryGetValue(key, out var backup)) return false;

            foreach (var e in backup) skin.SetAttachment(e.SlotIndex, e.Name, e.Attachment);
            _vanilla.Remove(key);
            _log.LogInfo($"Restored {backup.Count} vanilla attachment(s) in '{skinName}'.");
            return true;
        }

        public int SwapInPlace(SkeletonData data, OutfitDefinition def, Material sourceMaterial)
        {
            var skin = data.FindSkin(def.BaseSkin);
            if (skin == null) { _log.LogWarning($"[{def.Id}] base skin '{def.BaseSkin}' not found."); return 0; }

            var sprite = def.Art?.GetSprite();
            if (sprite == null) { _log.LogWarning($"[{def.Id}] art produced no sprite."); return 0; }

            SnapshotVanilla(data, def.BaseSkin, skin);

            var entries = new List<Skin.SkinEntry>(skin.Attachments);  // copy: we mutate while reading
            int swapped = 0;
            foreach (var e in entries)
            {
                if (e.Attachment == null) continue;
                var clone = e.Attachment.GetRemappedClone(
                    sprite, sourceMaterial,
                    premultiplyAlpha: true, cloneMeshAsLinked: true, useOriginalRegionSize: true);
                if (clone != null) { skin.SetAttachment(e.SlotIndex, e.Name, clone); swapped++; }
            }
            _log.LogInfo($"[{def.Id}] swapped {swapped} attachment(s) in '{def.BaseSkin}'.");
            return swapped;
        }

        public bool RestoreInPlace(SkeletonData data, OutfitDefinition def) => RestoreSkin(data, def.BaseSkin);

        public int SwapPagesInPlace(SkeletonData data, string outfitId, string skinName, Dictionary<string, Texture> editedPages)
        {
            var skin = data.FindSkin(skinName);
            if (skin == null) { _log.LogWarning($"[{outfitId}] skin '{skinName}' not found."); return 0; }
            if (editedPages == null || editedPages.Count == 0) return 0;

            SnapshotVanilla(data, skinName, skin);

            var entries = new List<Skin.SkinEntry>(skin.Attachments);
            int swapped = 0;
            foreach (var e in entries)
            {
                var region = (e.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                var origPage = region?.page;
                if (region == null || origPage == null) continue;
                if (!editedPages.TryGetValue(origPage.name, out var editedTex) || editedTex == null) continue;

                var modPage = GetOrCreateModPage(origPage, editedTex);
                if (modPage == null) continue;

                var modRegion = region.Clone();
                modRegion.page = modPage;  // same coords/UVs, different texture

                var clone = e.Attachment.GetRemappedClone(modRegion, cloneMeshAsLinked: true, useOriginalRegionSize: true);
                if (clone != null) { skin.SetAttachment(e.SlotIndex, e.Name, clone); swapped++; }
            }
            _log.LogInfo($"[{outfitId}] repointed {swapped} attachment(s) of '{skinName}' to mod page(s).");
            return swapped;
        }

        public int BuildDerivedSkin(SkeletonData data, string baseSkinName, string newSkinName,
                                    Dictionary<string, Texture> editedPages, Material sourceMaterial)
        {
            var baseSkin = data.FindSkin(baseSkinName);
            if (baseSkin == null) { _log.LogWarning($"Base skin '{baseSkinName}' not found."); return 0; }

            var existing = data.FindSkin(newSkinName);
            var newSkin = existing ?? new Skin(newSkinName);

            int n = 0;
            foreach (var e in new List<Skin.SkinEntry>(baseSkin.Attachments))
            {
                if (e.Attachment == null) continue;
                Attachment attachment = null;

                var region = (e.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                if (editedPages != null && region?.page != null &&
                    editedPages.TryGetValue(region.page.name, out var tex) && tex != null)
                {
                    var modPage = GetOrCreateModPage(region.page, tex);
                    if (modPage != null)
                    {
                        var modRegion = region.Clone();
                        modRegion.page = modPage;
                        attachment = e.Attachment.GetRemappedClone(modRegion, cloneMeshAsLinked: true, useOriginalRegionSize: true);
                    }
                }

                newSkin.SetAttachment(e.SlotIndex, e.Name, attachment ?? e.Attachment);
                n++;
            }

            if (existing == null) data.Skins.Add(newSkin);
            _log.LogInfo($"Built derived skin '{newSkinName}' from '{baseSkinName}' ({n} attachment(s)){(existing != null ? " [updated]" : "")}.");
            return n;
        }

        // One AtlasRegion covering a whole single page (our packed suit atlas). RegionUVs are then
        // page-normalized 0..1 and map directly to it.
        public AtlasRegion MakeFullPageRegion(Material template, Texture tex, int w, int h)
        {
            var mat = new Material(template) { mainTexture = tex };
            var page = new AtlasPage { name = "racer", width = w, height = h, pma = true, rendererObject = mat };
            return new AtlasRegion
            {
                page = page, name = "racer", index = -1, rotate = false, degrees = 0,
                u = 0f, v = 0f, u2 = 1f, v2 = 1f,
                width = w, height = h, offsetX = 0, offsetY = 0, originalWidth = w, originalHeight = h
            };
        }

        // Build a mesh attachment: reuse src's structure (hull/edges), swap in our single-page region,
        // explicit page UVs, and the dilated weighted geometry.
        public MeshAttachment MakeMesh(Attachment src, AtlasRegion region, float[] regionUVs,
                                       int[] bones, float[] verts, int worldVerticesLength, int[] triangles)
        {
            if (!(src.Copy() is MeshAttachment m)) return null;
            m.Region = region;
            m.RegionUVs = regionUVs;
            if (triangles != null) m.Triangles = triangles;
            m.Bones = bones;
            m.Vertices = verts;
            m.WorldVerticesLength = worldVerticesLength;
            // Copy() inherits the source's TimelineAttachment, which would link our custom topology to
            // the original's deform timelines and explode it. Point it at itself so none match.
            m.TimelineAttachment = m;
            m.UpdateRegion();
            return m;
        }

        private AtlasPage GetOrCreateModPage(AtlasPage origPage, Texture editedTex)
        {
            string key = origPage.name + "#" + editedTex.GetInstanceID();
            if (_pageCache.TryGetValue(key, out var cached)) return cached;

            var origMat = origPage.rendererObject as Material;
            if (origMat == null) { _log.LogWarning($"Atlas page '{origPage.name}' has no Material."); return null; }

            var modMat = new Material(origMat) { mainTexture = editedTex };
            var modPage = origPage.Clone();
            modPage.rendererObject = modMat;

            _pageCache[key] = modPage;
            return modPage;
        }
    }
}
