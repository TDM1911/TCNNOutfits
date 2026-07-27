using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using Spine;
using Spine.Unity;
using UnityEngine;
using TCNNOutfits.Game;
using TCNNOutfits.Spine;

namespace TCNNOutfits.Core
{
    // Data-driven custom-mesh outfit loaded from a package folder (single.json + page + hide.json +
    // outfit.json). Injects the meshes into a named skin "tcnn/<id>" the game composes from, and
    // registers a real equippable item. No per-outfit code.
    public sealed class MeshOutfit
    {
        // Live skeletons run at import scale 0.01, so the exporter's raw bone-local coords are 100x too big.
        private const float BoneLocalScale = 0.01f;

        private sealed class Meta
        {
            public string id; public string name; public string @base;
            public string cloneBase; public string icon; public string description;
            public string[] slots;
        }
        private sealed class MeshRec
        {
            public string slot; public int slotIndex; public string name; public string skin;
            public int[] bones; public float[] vertices; public int worldVerticesLength;
            public int[] triangles; public float[] regionUVs;
        }
        private sealed class Pkg { public string page; public int pageW; public int pageH; public List<MeshRec> meshes; }

        public string Id { get; }
        public string Title { get; }
        public bool Active { get; private set; }
        private string SkinName => "tcnn/" + Id;

        private readonly string _dir, _cloneBase, _icon, _desc;
        private readonly SkinResolver _resolver;
        private readonly GameIntegration _game;
        private readonly OutfitFramework _framework;
        private readonly ManualLogSource _log;

        private Pkg _pkg;
        private List<string> _hide;
        private Texture2D _pageTex;
        private AtlasRegion _region;
        private bool _itemGiven;

        private readonly HashSet<SkeletonData> _injected = new HashSet<SkeletonData>();
        private readonly HashSet<SkeletonGraphic> _hooked = new HashSet<SkeletonGraphic>();

        public MeshOutfit(string folder, SkinResolver resolver, GameIntegration game, OutfitFramework framework, ManualLogSource log)
        {
            _dir = folder; _resolver = resolver; _game = game; _framework = framework; _log = log;

            Meta meta = null;
            var mp = Path.Combine(folder, "outfit.json");
            if (File.Exists(mp)) { try { meta = JsonConvert.DeserializeObject<Meta>(File.ReadAllText(mp)); } catch { } }
            Id = !string.IsNullOrEmpty(meta?.id) ? meta.id : Path.GetFileName(folder.TrimEnd('/', '\\'));
            Title = !string.IsNullOrEmpty(meta?.name) ? meta.name : Id;
            _cloneBase = !string.IsNullOrEmpty(meta?.cloneBase) ? meta.cloneBase : "outfits/default/coat";
            _icon = !string.IsNullOrEmpty(meta?.icon) ? meta.icon : "icon.png";
            _desc = !string.IsNullOrEmpty(meta?.description) ? meta.description : "Custom outfit: " + Title;
            _slots = meta?.slots;
        }
        private readonly string[] _slots;

        public static bool IsPackage(string folder) => File.Exists(Path.Combine(folder, "single.json"));

        // Register + build + give + equip. Idempotent.
        public void Setup()
        {
            if (!Load()) { _log.LogWarning($"[{Id}] package missing at " + _dir); return; }

            // Clone a base outfit item so this occupies a real equip slot; the framework repoints its
            // DialogueSpineSkins to SkinName.
            var item = _framework.CreateOutfitItem(_cloneBase, Id, _dir,
                new OutfitOptions { Title = Title, Description = _desc, Icon = _icon, Slots = _slots });
            if (item == null)
            {
                _log.LogWarning($"[{Id}] item registration failed — base '{_cloneBase}' not available yet. " +
                                "Load a save / open dialogue first, then retry.");
                return;
            }

            Active = true;
            if (EnsureSkins(force: true) == 0)
            {
                _log.LogWarning($"[{Id}] no character on screen — item registered; skin builds when a portrait appears.");
                return;
            }

            if (!_itemGiven && _framework.GiveToPlayer(Id, equip: true)) _itemGiven = true;
            else _game.RefreshLivePortraits();

            _log.LogInfo($"[{Id}] '{Title}' ready: registered, built, given & equipped.");
        }

        public void Tick() { if (Active) EnsureSkins(force: false); }

        public int EnsureSkins(bool force)
        {
            if (_pkg == null) return 0;
            int touched = 0;
            foreach (var kv in _game.GetLiveGraphics())
            {
                var sg = kv.Value; var skel = sg?.Skeleton; var data = skel?.Data;
                if (data == null) continue;

                if (_hooked.Add(sg)) sg.UpdateComplete += OnUpdateComplete;
                sg.allowMultipleCanvasRenderers = true;   // our page is a 2nd texture

                if (!force && _injected.Contains(data)) continue;

                BuildSkinInto(data, skel, sg.material);
                sg.SetAllDirty();
                _injected.Add(data);
                touched++;
            }
            return touched;
        }

        private void BuildSkinInto(SkeletonData data, Skeleton skel, Material template)
        {
            if (_region == null) _region = _resolver.MakeFullPageRegion(template, PageTex(), _pkg.pageW, _pkg.pageH);

            var skin = data.FindSkin(SkinName);
            if (skin == null) { skin = new Skin(SkinName); data.Skins.Add(skin); }
            else skin.Clear();

            int n = 0;
            foreach (var m in _pkg.meshes)
            {
                var src = data.FindSkin(m.skin)?.GetAttachment(m.slotIndex, m.name);
                if (src == null) { _log.LogWarning($"[{Id}] source {m.skin}/{m.name} not found on '{data.Name}'"); continue; }

                var mesh = _resolver.MakeMesh(src, _region, m.regionUVs, m.bones, m.vertices, m.worldVerticesLength, m.triangles);
                if (mesh == null) continue;

                // Register under every name the game might select for this slot (setup name, the live
                // name, our source name) so SetSlotsToSetupPose lands on our mesh whichever it picks.
                var slotData = data.Slots.Items[m.slotIndex];
                var names = new HashSet<string>();
                if (!string.IsNullOrEmpty(slotData.AttachmentName)) names.Add(slotData.AttachmentName);
                var live = skel.Slots.Items[m.slotIndex].Attachment;
                if (live != null) names.Add(live.Name);
                names.Add(m.name);
                foreach (var nm in names) skin.SetAttachment(m.slotIndex, nm, mesh);
                n++;
            }

            // Hide nude slots by putting a fully-transparent copy of their attachment(s) in OUR skin,
            // under the same name(s) the base uses. Because it lives in the composed skin, the game's
            // Populate re-applies it every time (dialogue included) — unlike a per-frame null that a
            // static dialogue portrait would revert. Only composes while our outfit is worn, so it's
            // reversible: switch outfits and the real nude parts come back.
            var baseSkin = data.FindSkin("naked");
            foreach (var sn in _hide ?? new List<string>())
            {
                var slot = data.FindSlot(sn);
                if (slot == null) continue;
                int si = slot.Index;
                var names = new HashSet<string>();
                var setup = data.Slots.Items[si].AttachmentName;
                if (!string.IsNullOrEmpty(setup)) names.Add(setup);
                if (baseSkin != null)
                    foreach (var e in baseSkin.Attachments)
                        if (e.SlotIndex == si && e.Name != null) names.Add(e.Name);
                foreach (var nm in names)
                {
                    var src2 = baseSkin?.GetAttachment(si, nm);
                    if (src2 == null) continue;
                    var clear = src2.Copy();
                    if (clear is MeshAttachment cm) cm.A = 0f;
                    else if (clear is RegionAttachment cr) cr.A = 0f;
                    skin.SetAttachment(si, nm, clear);
                }
            }
            _log.LogInfo($"[{Id}] skin '{SkinName}' built with {n} mesh(es) + {(_hide?.Count ?? 0)} hidden on '{data.Name}'.");
        }

        // Clear deform on our slots (see MakeMesh) — only while our outfit is composed, so we never
        // touch another outfit's deform. try/catch so a bad frame can't break other mods.
        private void OnUpdateComplete(global::Spine.Unity.ISkeletonAnimation animated)
        {
            try
            {
                var skel = animated?.Skeleton;
                if (skel == null || !Active || _region == null || !IsWorn(skel)) return;
                foreach (var m in _pkg.meshes)
                {
                    if (m.slotIndex < 0 || m.slotIndex >= skel.Slots.Count) continue;
                    var d = skel.Slots.Items[m.slotIndex].Deform;
                    if (d != null && d.Count > 0) d.Clear();
                }
            }
            catch { }
        }

        // Our meshes only compose when this outfit is equipped, so their presence is a reliable
        // "is it worn right now" signal.
        private bool IsWorn(Skeleton skel)
        {
            foreach (var m in _pkg.meshes)
            {
                if (m.slotIndex < 0 || m.slotIndex >= skel.Slots.Count) continue;
                if (skel.Slots.Items[m.slotIndex].Attachment is MeshAttachment att && att.Region == _region) return true;
            }
            return false;
        }

        private bool Load()
        {
            if (_pkg != null) return true;
            if (!Directory.Exists(_dir)) return false;
            _pkg = JsonConvert.DeserializeObject<Pkg>(File.ReadAllText(Path.Combine(_dir, "single.json")));
            var hp = Path.Combine(_dir, "hide.json");
            _hide = File.Exists(hp) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(hp)) : new List<string>();

            foreach (var m in _pkg.meshes)   // [localX, localY, weight] per influence — scale the coords only
            {
                var v = m.vertices;
                if (v == null) continue;
                for (int i = 0; i + 2 < v.Length; i += 3) { v[i] *= BoneLocalScale; v[i + 1] *= BoneLocalScale; }
            }
            _log.LogInfo($"[{Id}] {_pkg.meshes.Count} meshes, page {_pkg.pageW}x{_pkg.pageH}, {_hide.Count} hidden.");
            return true;
        }

        private Texture2D PageTex()
        {
            if (_pageTex != null) return _pageTex;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(Path.Combine(_dir, _pkg.page)));
            tex.wrapMode = TextureWrapMode.Clamp; tex.Apply(false, false);
            _pageTex = tex;
            return tex;
        }
    }
}
