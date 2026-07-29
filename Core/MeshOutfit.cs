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
    // Data-driven custom-mesh outfit loaded from a package folder. Injects meshes into a named skin
    // "tcnn/<id>" the game composes from, and registers a real equippable item. No per-outfit code.
    //
    // A package holds outfit.json + icon.png at the root, then one subfolder per surface (skeleton),
    // each with single.json + its page + hide.json — e.g. portrait/ and overworld/. Whichever surface's
    // meshes resolve on a given live skeleton is the one that injects there. A flat package (single.json
    // at the root, single surface) is still accepted.
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

        // One drawable surface (a skeleton's worth of meshes + its own page/region + hide list).
        private sealed class Surface
        {
            public string Dir;
            public Pkg Pkg;
            public List<string> Hide;
            public Texture2D PageTex;
            public AtlasRegion Region;
        }

        public string Id { get; }
        public string Title { get; }
        public bool Active { get; private set; }
        private string SkinName => "tcnn/" + Id;

        private readonly string _dir, _cloneBase, _icon, _desc;
        private readonly string[] _slots;
        private readonly SkinResolver _resolver;
        private readonly GameIntegration _game;
        private readonly OutfitFramework _framework;
        private readonly ManualLogSource _log;

        private readonly List<Surface> _surfaces = new List<Surface>();
        private bool _itemGiven;

        private readonly HashSet<SkeletonData> _injected = new HashSet<SkeletonData>();
        private readonly HashSet<global::Spine.Unity.ISkeletonAnimation> _hooked = new HashSet<global::Spine.Unity.ISkeletonAnimation>();

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

        // A package is a folder with single.json at its root (flat) or in any immediate subfolder (surfaces).
        public static bool IsPackage(string folder)
        {
            if (File.Exists(Path.Combine(folder, "single.json"))) return true;
            if (!Directory.Exists(folder)) return false;
            foreach (var sub in Directory.GetDirectories(folder))
                if (File.Exists(Path.Combine(sub, "single.json"))) return true;
            return false;
        }

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
            if (_surfaces.Count == 0) return 0;
            int touched = 0;
            bool overworldTouched = false;
            foreach (var ls in _game.GetLiveSkeletons())
            {
                var data = ls.Data;
                if (data == null) continue;

                if (ls.Animated != null && _hooked.Add(ls.Animated)) ls.Animated.UpdateComplete += OnUpdateComplete;
                if (ls.Graphic != null) ls.Graphic.allowMultipleCanvasRenderers = true;   // UI page is a 2nd texture

                if (!force && _injected.Contains(data)) continue;

                BuildSkinInto(data, ls.Skeleton, ls.Material);
                ls.MarkDirty?.Invoke();
                if (ls.Graphic == null) overworldTouched = true;   // MeshRenderer chibi must re-compose its skin
                _injected.Add(data);
                touched++;
            }
            if (overworldTouched) _game.RefreshLiveOverworld();
            return touched;
        }

        private void BuildSkinInto(SkeletonData data, Skeleton skel, Material preferred)
        {
            // Stage into a temp skin; only overwrite the live one if something actually matched. A
            // wrong/stale skeleton (indices from another version) must leave the skin untouched, never
            // strip it down to a nude body.
            var built = new Skin(SkinName);
            int total = 0;
            foreach (var s in _surfaces)
            {
                int matched = 0;
                foreach (var m in s.Pkg.meshes)
                {
                    var src = data.FindSkin(m.skin)?.GetAttachment(m.slotIndex, m.name);
                    if (src == null) continue;   // this surface's mesh doesn't exist on this skeleton

                    // Build the surface region lazily from THIS skeleton's own atlas material, so the page
                    // carries the right shader for whichever renderer it is (UI portrait vs MeshRenderer chibi).
                    if (s.Region == null)
                    {
                        var template = preferred ?? ((src as IHasTextureRegion)?.Region as AtlasRegion)?.page?.rendererObject as Material;
                        if (template == null) { _log.LogWarning($"[{Id}] no atlas material from {m.skin}/{m.name} on '{data.Name}'."); continue; }
                        s.Region = _resolver.MakeFullPageRegion(template, PageTex(s), s.Pkg.pageW, s.Pkg.pageH);
                    }

                    var mesh = _resolver.MakeMesh(src, s.Region, m.regionUVs, m.bones, m.vertices, m.worldVerticesLength, m.triangles);
                    if (mesh == null) continue;

                    // Register under every name the game might select for this slot (setup name, the live
                    // name, our source name) so SetSlotsToSetupPose lands on our mesh whichever it picks.
                    var slotData = data.Slots.Items[m.slotIndex];
                    var names = new HashSet<string>();
                    if (!string.IsNullOrEmpty(slotData.AttachmentName)) names.Add(slotData.AttachmentName);
                    var live = skel?.Slots.Items[m.slotIndex].Attachment;
                    if (live != null) names.Add(live.Name);
                    names.Add(m.name);
                    foreach (var nm in names) built.SetAttachment(m.slotIndex, nm, mesh);
                    matched++;
                }

                if (matched == 0)   // wrong skeleton for this surface — skip its hide too
                {
                    if (s.Pkg.meshes.Count > 0) DiagnoseNoMatch(data, s);
                    continue;
                }
                total += matched;
                ApplyHide(data, built, s.Hide);
            }

            if (total == 0)   // nothing for this skeleton — don't touch its skin
            {
                _log.LogInfo($"[{Id}] no matching meshes on '{data.Name}' — skin left as-is.");
                return;
            }

            var skin = data.FindSkin(SkinName);
            if (skin == null) { data.Skins.Add(built); }
            else { skin.Clear(); foreach (var e in built.Attachments) skin.SetAttachment(e.SlotIndex, e.Name, e.Attachment); }
            _log.LogInfo($"[{Id}] skin '{SkinName}' built with {total} mesh(es) on '{data.Name}'.");
        }

        // Why did none of a surface's meshes resolve on this skeleton? Log enough to tell whether it's
        // the wrong skeleton (expected — a surface only matches its own) or a real name/index mismatch.
        private void DiagnoseNoMatch(SkeletonData data, Surface s)
        {
            _log.LogWarning($"[{Id}] surface '{Path.GetFileName(s.Dir)}' matched 0 on '{data.Name}' (live slots={data.Slots.Count}). " +
                            "Likely the package was exported against a different skeleton version — re-dump and re-export.");
            int shown = 0;
            foreach (var m in s.Pkg.meshes)
            {
                if (shown++ >= 3) break;
                if (data.FindSkin(m.skin) == null) { _log.LogWarning($"    want {m.skin}/{m.name}@{m.slotIndex}: that skin isn't on this skeleton"); continue; }
                if (m.slotIndex >= data.Slots.Count) { _log.LogWarning($"    want {m.skin}/{m.name}@{m.slotIndex}: slot past end (max {data.Slots.Count - 1})"); continue; }
                _log.LogWarning($"    want {m.skin}/{m.name}@{m.slotIndex}: but live slot {m.slotIndex} is '{data.Slots.Items[m.slotIndex].Name}'");
            }
        }

        // Hide nude slots by putting a fully-transparent copy of their attachment(s) in OUR skin, under
        // the same name(s) the base uses. Because it lives in the composed skin, the game's Populate
        // re-applies it every time (dialogue included) — unlike a per-frame null that a static dialogue
        // portrait would revert. Only composes while our outfit is worn, so switching outfits brings the
        // real nude parts back.
        private void ApplyHide(SkeletonData data, Skin skin, List<string> hide)
        {
            var baseSkin = data.FindSkin("naked");
            foreach (var sn in hide ?? new List<string>())
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
        }

        // Clear deform on any slot currently showing one of our meshes (see MakeMesh). Region-gated so we
        // only touch slots that hold our art — never another outfit's. try/catch so a bad frame can't
        // break other mods.
        private void OnUpdateComplete(global::Spine.Unity.ISkeletonAnimation animated)
        {
            try
            {
                var skel = animated?.Skeleton;
                if (skel == null || !Active) return;
                for (int si = 0; si < skel.Slots.Count; si++)
                {
                    var slot = skel.Slots.Items[si];
                    if (!(slot.Attachment is MeshAttachment att) || !IsOurs(att)) continue;
                    var d = slot.Deform;
                    if (d != null && d.Count > 0) d.Clear();
                }
            }
            catch { }
        }

        private bool IsOurs(MeshAttachment att)
        {
            foreach (var s in _surfaces)
                if (s.Region != null && att.Region == s.Region) return true;
            return false;
        }

        private bool Load()
        {
            if (_surfaces.Count > 0) return true;
            if (!Directory.Exists(_dir)) return false;

            var dirs = new List<string>();
            if (File.Exists(Path.Combine(_dir, "single.json"))) dirs.Add(_dir);   // flat (single surface)
            else foreach (var sub in Directory.GetDirectories(_dir))              // one subfolder per surface
                if (File.Exists(Path.Combine(sub, "single.json"))) dirs.Add(sub);

            foreach (var d in dirs)
            {
                var pkg = JsonConvert.DeserializeObject<Pkg>(File.ReadAllText(Path.Combine(d, "single.json")));
                var hp = Path.Combine(d, "hide.json");
                var hide = File.Exists(hp) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(hp)) : new List<string>();

                foreach (var m in pkg.meshes)   // [localX, localY, weight] per influence — scale the coords only
                {
                    var v = m.vertices;
                    if (v == null) continue;
                    for (int i = 0; i + 2 < v.Length; i += 3) { v[i] *= BoneLocalScale; v[i + 1] *= BoneLocalScale; }
                }

                _surfaces.Add(new Surface { Dir = d, Pkg = pkg, Hide = hide });
                _log.LogInfo($"[{Id}] surface '{Path.GetFileName(d)}': {pkg.meshes.Count} meshes, " +
                             $"page {pkg.pageW}x{pkg.pageH}, {hide.Count} hidden.");
            }
            return _surfaces.Count > 0;
        }

        private Texture2D PageTex(Surface s)
        {
            if (s.PageTex != null) return s.PageTex;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(Path.Combine(s.Dir, s.Pkg.page)));
            tex.wrapMode = TextureWrapMode.Clamp; tex.Apply(false, false);
            s.PageTex = tex;
            return tex;
        }
    }
}
