using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Newtonsoft.Json;
using Spine;
using UnityEngine;

namespace TCNNOutfits.Spine
{
    // Dumps a live SkeletonData into the exact folder format the Outfit Mesh Editor consumes
    // (meta.json + positions.json + weights.json + atlas.atlas + pages/), straight from the running
    // game. This removes any dependence on an external extraction matching the game build — the data
    // is, by definition, the skeleton the game is running.
    public sealed class SkeletonDumper
    {
        private readonly ManualLogSource _log;
        private readonly SkinExporter _exporter;
        public SkeletonDumper(ManualLogSource log, SkinExporter exporter) { _log = log; _exporter = exporter; }

        // Live skeletons run at import-scale (~0.01); the editor/exporter work in the raw .skel space the
        // mod later multiplies back down by BoneLocalScale. So all world POSITIONS are scaled up by
        // 1/importScale here. Rotation (a,b,c,d) and page-pixel UVs are scale-independent.
        private float _pos = 100f;

        public string Dump(SkeletonData data, string outDir, float importScale)
        {
            if (data == null) { _log.LogWarning("Dump: null SkeletonData."); return null; }
            _pos = importScale > 1e-6f ? 1f / importScale : 100f;
            Directory.CreateDirectory(outDir);
            var pagesDir = Path.Combine(outDir, "pages");
            Directory.CreateDirectory(pagesDir);

            // Pose a throwaway skeleton at setup so we read setup-pose world transforms/verts.
            var skel = new Skeleton(data);
            skel.SetToSetupPose();
            skel.UpdateWorldTransform(Skeleton.Physics.None);

            WriteMeta(data, skel, outDir);
            var pageTex = new Dictionary<string, Texture2D>();
            var pageRegions = new Dictionary<string, HashSet<string>>();
            var pageSize = new Dictionary<string, Vector2Int>();
            WritePositionsAndWeights(data, skel, outDir, pageTex, pageRegions, pageSize);
            WriteAtlasAndPages(outDir, pagesDir, pageTex, pageRegions, pageSize);
            int animCount = WriteAnims(data, outDir);

            _log.LogInfo($"[dump] '{data.Name}' -> {outDir} ({data.Bones.Count} bones, {data.Slots.Count} slots, " +
                         $"{pageTex.Count} page(s), {animCount} anim(s), posScale x{_pos:0.##}).");
            return outDir;
        }

        // Sample every animation into per-frame bone world transforms — the editor re-poses meshes with
        // these (LBS) to preview how an outfit deforms across poses. Only bones are needed (a,b,c,d,x,y).
        private int WriteAnims(SkeletonData data, string outDir)
        {
            var skel = new Skeleton(data);
            var anims = new Dictionary<string, object>();
            foreach (var anim in data.Animations)
            {
                float dur = anim.Duration;
                int frames = dur <= 0f ? 1 : Mathf.Clamp(Mathf.RoundToInt(dur * 9f), 2, 30);   // ~9fps, capped
                var flist = new List<object>(frames);
                for (int f = 0; f < frames; f++)
                {
                    float t = frames > 1 ? dur * f / (frames - 1) : 0f;
                    skel.SetToSetupPose();
                    anim.Apply(skel, 0f, t, false, null, 1f, MixBlend.First, MixDirection.In);
                    skel.UpdateWorldTransform(Skeleton.Physics.None);
                    var bones = new List<object>(skel.Bones.Count);
                    foreach (var bn in skel.Bones)
                        bones.Add(new
                        {
                            a = R(bn.A, 4), b = R(bn.B, 4), c = R(bn.C, 4), d = R(bn.D, 4),
                            x = R(bn.WorldX * _pos, 1), y = R(bn.WorldY * _pos, 1)
                        });
                    flist.Add(new { bones });
                }
                anims[anim.Name] = new { dur, frames = flist };
            }
            File.WriteAllText(Path.Combine(outDir, "anims.json"), JsonConvert.SerializeObject(anims));
            return anims.Count;
        }

        private static double R(float v, int dp) => System.Math.Round(v, dp);

        private void WriteMeta(SkeletonData data, Skeleton skel, string outDir)
        {
            var bones = new List<object>();
            for (int i = 0; i < skel.Bones.Count; i++)
            {
                var b = skel.Bones.Items[i];
                float len = b.Data.Length;
                bones.Add(new
                {
                    name = b.Data.Name,
                    x = b.WorldX * _pos, y = b.WorldY * _pos,
                    tipx = (b.WorldX + b.A * len) * _pos, tipy = (b.WorldY + b.C * len) * _pos,
                    len = len * _pos,
                    parent = b.Parent != null ? b.Parent.Data.Index : 0,
                    a = b.A, b = b.B, c = b.C, d = b.D
                });
            }
            var slots = new List<object>();
            for (int i = 0; i < data.Slots.Count; i++)
            {
                var s = data.Slots.Items[i];
                slots.Add(new { index = i, name = s.Name, bone = s.BoneData.Name });
            }
            File.WriteAllText(Path.Combine(outDir, "meta.json"),
                JsonConvert.SerializeObject(new { bones, slots }));
        }

        private void WritePositionsAndWeights(SkeletonData data, Skeleton skel, string outDir,
            Dictionary<string, Texture2D> pageTex, Dictionary<string, HashSet<string>> pageRegions,
            Dictionary<string, Vector2Int> pageSize)
        {
            var positions = new List<object>();
            var weights = new List<object>();

            foreach (var skin in data.Skins)
            {
                foreach (var e in skin.Attachments)
                {
                    var region = (e.Attachment as IHasTextureRegion)?.Region as AtlasRegion;
                    if (region == null) continue;
                    var page = region.page;
                    var tex = (page?.rendererObject as Material)?.mainTexture as Texture2D;
                    if (page == null || tex == null) continue;

                    int slotIndex = e.SlotIndex;
                    var slot = skel.Slots.Items[slotIndex];

                    string pf = SkinExporter.Sanitize(page.name);
                    if (!pageTex.ContainsKey(pf)) { pageTex[pf] = tex; pageSize[pf] = new Vector2Int(tex.width, tex.height); }
                    if (!pageRegions.TryGetValue(pf, out var set)) pageRegions[pf] = set = new HashSet<string>();
                    set.Add(region.name);

                    int pw = tex.width, ph = tex.height;

                    try
                    {
                        if (e.Attachment is MeshAttachment mesh)
                        {
                            int n = mesh.WorldVerticesLength;
                            var wv = new float[n];
                            mesh.ComputeWorldVertices(slot, 0, n, wv, 0, 2);

                            var verts = new List<float>(n);
                            for (int k = 0; k < n; k++) verts.Add(wv[k] * _pos);

                            var uvs = new List<float>(n);
                            for (int k = 0; k + 1 < mesh.UVs.Length; k += 2)   // page pixels, V flipped to top-origin
                            { uvs.Add(mesh.UVs[k] * pw); uvs.Add((1f - mesh.UVs[k + 1]) * ph); }

                            positions.Add(new { skin = skin.Name, name = e.Name, region = region.name, type = "mesh",
                                                slot = slotIndex, verts, uvs, tris = new List<int>(mesh.Triangles) });
                            weights.Add(new { skin = skin.Name, name = e.Name, slot = slotIndex, vw = MeshVW(mesh, slot) });
                        }
                        else if (e.Attachment is RegionAttachment ra)
                        {
                            var wv = new float[8];
                            ra.ComputeWorldVertices(slot, wv, 0, 2);
                            var verts = new List<float>(8); for (int k = 0; k < 8; k++) verts.Add(wv[k] * _pos);
                            var uvs = new List<float>(8);
                            for (int k = 0; k + 1 < ra.UVs.Length; k += 2) { uvs.Add(ra.UVs[k] * pw); uvs.Add((1f - ra.UVs[k + 1]) * ph); }
                            positions.Add(new { skin = skin.Name, name = e.Name, region = region.name, type = "region",
                                                slot = slotIndex, verts, uvs, tris = new List<int> { 0, 1, 2, 2, 3, 0 } });
                        }
                    }
                    catch (System.Exception ex) { _log.LogWarning($"[dump] skip {skin.Name}/{e.Name}: {ex.Message}"); }
                }
            }

            File.WriteAllText(Path.Combine(outDir, "positions.json"), JsonConvert.SerializeObject(positions));
            File.WriteAllText(Path.Combine(outDir, "weights.json"), JsonConvert.SerializeObject(new { meshes = weights }));
        }

        // Per-vertex bone influences: [[boneIndex, weight], ...] per vertex. Non-weighted meshes bind
        // every vertex to the slot's single bone.
        private static List<List<List<float>>> MeshVW(MeshAttachment mesh, Slot slot)
        {
            var vw = new List<List<List<float>>>();
            if (mesh.Bones != null)
            {
                int bi = 0, vi = 0;
                int verts = mesh.WorldVerticesLength / 2;
                for (int v = 0; v < verts; v++)
                {
                    int cnt = mesh.Bones[bi++];
                    var infl = new List<List<float>>(cnt);
                    for (int k = 0; k < cnt; k++)
                    {
                        int b = mesh.Bones[bi++];
                        float w = mesh.Vertices[vi + 2]; vi += 3;
                        infl.Add(new List<float> { b, w });
                    }
                    vw.Add(infl);
                }
            }
            else
            {
                int b = slot.Bone.Data.Index;
                int verts = mesh.WorldVerticesLength / 2;
                for (int v = 0; v < verts; v++) vw.Add(new List<List<float>> { new List<float> { b, 1f } });
            }
            return vw;
        }

        private void WriteAtlasAndPages(string outDir, string pagesDir, Dictionary<string, Texture2D> pageTex,
            Dictionary<string, HashSet<string>> pageRegions, Dictionary<string, Vector2Int> pageSize)
        {
            var sb = new StringBuilder();
            foreach (var kv in pageTex)
            {
                string pf = kv.Key;
                var sz = pageSize[pf];
                sb.Append(pf).Append(".png\n");
                sb.Append("size:").Append(sz.x).Append(',').Append(sz.y).Append('\n');
                sb.Append("filter:Linear,Linear\n");
                sb.Append("pma:true\n");
                if (pageRegions.TryGetValue(pf, out var set))
                    foreach (var r in set) sb.Append(r).Append('\n');
                sb.Append('\n');
                _exporter.ExportTexture(kv.Value, Path.Combine(pagesDir, pf + ".png"));
            }
            File.WriteAllText(Path.Combine(outDir, "atlas.atlas"), sb.ToString());
        }
    }
}
