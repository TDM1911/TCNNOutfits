using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Spine;
using Spine.Unity;
using UnityEngine;
using TCNNOutfits.Content;
using TCNNOutfits.Game;
using TCNNOutfits.Spine;

namespace TCNNOutfits.Core
{
    public sealed class OutfitFramework
    {
        public static OutfitFramework Instance { get; private set; }

        private readonly ManualLogSource _log;
        private readonly string _baseDir;

        private readonly Dictionary<string, OutfitDefinition> _registry = new Dictionary<string, OutfitDefinition>();
        private readonly HashSet<string> _active = new HashSet<string>();
        private readonly Dictionary<string, Dictionary<string, Texture>> _imports = new Dictionary<string, Dictionary<string, Texture>>();
        private readonly List<IOutfitSource> _sources = new List<IOutfitSource>();
        private readonly SkinResolver _resolver;
        private readonly SkinExporter _exporter;
        private readonly SkinImporter _importer;

        private GameIntegration _game;
        private OutfitConsole _console;
        private readonly Dictionary<string, MeshOutfit> _meshOutfits = new Dictionary<string, MeshOutfit>();

        private readonly Dictionary<string, Asuna.Items.Equipment> _created = new Dictionary<string, Asuna.Items.Equipment>();
        public static event Action<string, Asuna.Items.Equipment> OnOutfitCreated;

        public OutfitFramework(ManualLogSource log, string baseDir)
        {
            _log = log;
            _baseDir = baseDir;
            _resolver = new SkinResolver(log);
            _exporter = new SkinExporter(log);
            _importer = new SkinImporter(log);
            Instance = this;
        }

        public void Boot()
        {
            _console = new OutfitConsole(this, _log);
            _console.TryRegister();

            _game = new GameIntegration(this, _log);
            DiscoverMeshOutfits(Path.Combine(_baseDir, "assets"));   // auto-register bundled packages
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            try
            {
                _sources.Add(new ContentPackSource(Path.Combine(_baseDir, "outfits"), _log));
                ReloadSources();
            }
            catch (System.Exception e) { _log.LogError("Source loading failed: " + e); }

            _log.LogInfo($"OutfitFramework booted. {_registry.Count} reskin outfit(s), {_meshOutfits.Count} mesh outfit(s). " +
                         "Console: outfit.skins, outfit.export, outfit.create, outfit.created, outfit.give, outfit.meshes, outfit.wear.");

            OutfitApi.FlushReady();
        }

        public void Tick()
        {
            _console?.TryRegister();
            foreach (var mo in _meshOutfits.Values) mo.Tick();   // keep custom-mesh outfits injected
        }

        // --- custom-mesh outfits: data-driven, no per-outfit code. A package folder holds
        // single.json + its page + hide.json + outfit.json (see MeshOutfit). ---
        public IEnumerable<string> MeshOutfitIds => _meshOutfits.Keys;

        public void DiscoverMeshOutfits(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var folder in Directory.GetDirectories(dir))
                if (MeshOutfit.IsPackage(folder)) RegisterMeshOutfit(folder);
        }

        public MeshOutfit RegisterMeshOutfit(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !MeshOutfit.IsPackage(folder))
            { _log.LogWarning("Not a mesh-outfit package: " + folder); return null; }
            var mo = new MeshOutfit(folder, _resolver, _game, this, _log);
            _meshOutfits[mo.Id] = mo;
            _log.LogInfo($"Registered mesh outfit '{mo.Id}' ({mo.Title}).");
            return mo;
        }

        // Dump every live skeleton (portrait + overworld) into the mesh editor's folder format, straight
        // from the running game — so the editor works against the exact skeleton the game loads.
        public List<string> DumpSkeletons()
        {
            var dumper = new global::TCNNOutfits.Spine.SkeletonDumper(_log, _exporter);
            string root = Path.Combine(_baseDir, "dumps");

            var scaleByName = new Dictionary<string, float>();
            var player = Asuna.CharManagement.Character.Player;
            if (player?.SpineSkeleton != null) scaleByName[player.SpineSkeleton.name] = player.SpineSkeleton.scale;
            if (player?.OverworldSpineSkeleton != null) scaleByName[player.OverworldSpineSkeleton.name] = player.OverworldSpineSkeleton.scale;

            var done = new List<string>();
            foreach (var kv in GetSkeletonSources())
            {
                var dir = Path.Combine(root, SkinExporter.Sanitize(kv.Key));
                float scale = scaleByName.TryGetValue(kv.Key, out var s) && s > 1e-6f ? s : 0.01f;
                try { if (dumper.Dump(kv.Value, dir, scale) != null) done.Add(dir); }
                catch (Exception e) { _log.LogError($"Dump '{kv.Key}' failed: " + e); }
            }
            return done;
        }

        public bool WearMeshOutfit(string id)
        {
            if (id == null || !_meshOutfits.TryGetValue(id, out var mo)) { _log.LogWarning($"No mesh outfit '{id}'."); return false; }
            mo.Setup();
            return true;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
            => RetryPending();

        public void Shutdown()
            => UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

        public IEnumerable<OutfitDefinition> Outfits => _registry.Values;
        public bool IsActive(string id) => _active.Contains(id);
        public bool Exists(string id) => _registry.ContainsKey(id);

        public void ReloadSources()
        {
            _registry.Clear();
            _resolver.ClearCache();
            foreach (var src in _sources)
            {
                try
                {
                    foreach (var def in src.Load())
                        Register(def, src.Name);
                }
                catch (System.Exception e)
                {
                    _log.LogError($"Outfit source '{src.Name}' failed to load: " + e);
                }
            }
        }

        private void Register(OutfitDefinition def, string sourceName)
        {
            if (def == null || string.IsNullOrEmpty(def.Id))
            {
                _log.LogWarning($"Ignored an outfit with no id from source '{sourceName}'.");
                return;
            }
            if (_registry.ContainsKey(def.Id))
            {
                _log.LogWarning($"Duplicate outfit id '{def.Id}' from source '{sourceName}' — keeping the first.");
                return;
            }
            _registry[def.Id] = def;
            _log.LogInfo($"Registered outfit '{def.Id}' ({def.DisplayName}) from '{sourceName}'.");
        }

        public bool Apply(string id)
        {
            if (!_registry.TryGetValue(id, out var def)) { _log.LogWarning($"No outfit '{id}'."); return false; }
            if (_active.Contains(id)) return true;

            int swapped = SwapOutfitInPlace(def, revert: false);
            if (swapped <= 0) { _log.LogWarning($"Outfit '{id}' could not be applied."); return false; }
            _active.Add(id);
            RefreshLive();
            return true;
        }

        public bool Clear(string id)
        {
            if (!_registry.TryGetValue(id, out var def)) return false;
            if (!_active.Contains(id)) return false;

            SwapOutfitInPlace(def, revert: true);
            _active.Remove(id);
            RefreshLive();
            return true;
        }

        public bool Toggle(string id)
        {
            if (!_registry.ContainsKey(id)) { _log.LogWarning($"No outfit '{id}'."); return false; }
            bool nowOn = !_active.Contains(id);
            if (nowOn) Apply(id); else Clear(id);
            return _active.Contains(id);
        }

        private int SwapOutfitInPlace(OutfitDefinition def, bool revert)
        {
            var graphics = _game?.GetLiveGraphics();
            if (graphics == null || graphics.Count == 0)
            {
                _log.LogWarning("No visible character. Open a dialogue so the character is on screen, then try again.");
                return 0;
            }

            int total = 0;
            foreach (var kv in graphics)
            {
                var data = kv.Value.Skeleton?.Data;
                if (data == null) continue;
                if (!def.AppliesTo(kv.Key)) continue;

                if (revert) { if (_resolver.RestoreInPlace(data, def)) total++; }
                else total += _resolver.SwapInPlace(data, def, kv.Value.material);

                kv.Value.SetAllDirty();
            }
            return total;
        }

        public IEnumerable<string> ActiveIds => _active.ToList();

        private string PortraitSkeletonName => Asuna.CharManagement.Character.Player?.SpineSkeleton?.name;
        private string OverworldSkeletonName => Asuna.CharManagement.Character.Player?.OverworldSpineSkeleton?.name;

        private List<KeyValuePair<string, SkeletonData>> GetSkeletonSources()
        {
            var list = new List<KeyValuePair<string, SkeletonData>>();

            var live = _game?.GetLiveGraphics();
            if (live != null)
                foreach (var kv in live)
                    if (kv.Value.Skeleton?.Data != null)
                        list.Add(new KeyValuePair<string, SkeletonData>(kv.Key, kv.Value.Skeleton.Data));

            var player = Asuna.CharManagement.Character.Player;
            if (player != null)
            {
                AddAsset(list, player.SpineSkeleton);
                AddAsset(list, player.OverworldSpineSkeleton);
            }
            return list;
        }

        private static void AddAsset(List<KeyValuePair<string, SkeletonData>> list, SkeletonDataAsset asset)
        {
            if (asset == null) return;
            foreach (var kv in list) if (kv.Key == asset.name) return;
            var data = asset.GetSkeletonData(true);
            if (data != null) list.Add(new KeyValuePair<string, SkeletonData>(asset.name, data));
        }

        private sealed class PendingOutfit
        {
            public string BaseSkin, Id, AssetFolder;
            public OutfitOptions Options;
            public bool ItemDone, SkinDone;
            public bool PortraitBuilt, OverworldBuilt;
            public bool Complete => ItemDone && SkinDone;
        }

        private readonly Dictionary<string, PendingOutfit> _outfits = new Dictionary<string, PendingOutfit>();

        // Item registration needs no skeleton; skin building does. They are materialised
        // independently so a consumer can register at startup and the skin appears later.
        public Asuna.Items.Equipment CreateOutfitItem(string baseSkin, string newId, string assetFolder = null, OutfitOptions options = null)
        {
            if (string.IsNullOrEmpty(baseSkin) || string.IsNullOrEmpty(newId)) return null;

            if (!_outfits.TryGetValue(newId, out var rec))
            {
                rec = new PendingOutfit { BaseSkin = baseSkin, Id = newId, AssetFolder = assetFolder, Options = options };
                _outfits[newId] = rec;
            }
            TryMaterialize(rec);
            return GetCreated(newId);
        }

        public void RetryPending()
        {
            foreach (var rec in _outfits.Values)
                if (!rec.Complete) TryMaterialize(rec);
        }

        private void TryMaterialize(PendingOutfit rec)
        {
            if (!rec.ItemDone) rec.ItemDone = BuildItem(rec);
            if (!rec.SkinDone) rec.SkinDone = BuildSkin(rec);
        }

        private string AssetFolderFor(PendingOutfit rec)
        {
            if (string.IsNullOrEmpty(rec.AssetFolder)) return _baseDir;
            return Path.IsPathRooted(rec.AssetFolder) ? rec.AssetFolder : Path.Combine(_baseDir, rec.AssetFolder);
        }

        // Also dump the inventory icon of the item that uses this skin, so it can be reskinned too.
        private void ExportItemIcon(string skinName, string dir)
        {
            foreach (var it in Asuna.Items.Item.All.Values)
            {
                var eq = it as Asuna.Items.Equipment;
                if (eq?.DialogueSpineSkins == null || !eq.DialogueSpineSkins.Contains(skinName)) continue;
                if (eq.DisplaySprite != null) _exporter.ExportSprite(eq.DisplaySprite, dir, "icon.png");
                else _log.LogInfo($"Item '{eq.Name}' has no DisplaySprite to export.");
                return;
            }
        }

        private bool BuildItem(PendingOutfit rec)
        {
            Asuna.Items.Equipment baseItem = null;
            foreach (var it in Asuna.Items.Item.All.Values)
            {
                var eq = it as Asuna.Items.Equipment;
                if (eq?.DialogueSpineSkins != null && eq.DialogueSpineSkins.Contains(rec.BaseSkin)) { baseItem = eq; break; }
            }
            if (baseItem == null) return false;   // registry not populated yet, or no such item

            var clone = baseItem.Clone() as Asuna.Items.Equipment;
            if (clone == null) { _log.LogWarning("Clone failed."); return false; }

            string newSkinName = "tcnn/" + rec.Id;
            clone.Name = !string.IsNullOrEmpty(rec.Options?.Title) ? rec.Options.Title : baseItem.Name + "_" + rec.Id;
            if (!string.IsNullOrEmpty(rec.Options?.Description)) clone.Description = rec.Options.Description;
            // Both surfaces are set here, not after the skin builds: a save can clone this
            // template before a skeleton is even available, and the clone keeps whatever the
            // template had at that instant.
            clone.DialogueSpineSkins = new List<string> { newSkinName };
            clone.OverworldSpineSkins = new List<string> { newSkinName };

            // Equipment slots this outfit occupies. Occupying a slot displaces whatever's in it, so a
            // full-body outfit blocks conflicting items. Unrecognised names are skipped.
            if (rec.Options?.Slots != null && rec.Options.Slots.Length > 0)
            {
                var slots = new List<Asuna.Items.EquipmentSlot>();
                foreach (var s in rec.Options.Slots)
                    if (System.Enum.TryParse<Asuna.Items.EquipmentSlot>(s, true, out var es)) slots.Add(es);
                    else _log.LogWarning($"[{rec.Id}] unknown equipment slot '{s}'.");
                if (slots.Count > 0) clone.Slots = slots;
            }

            var icon = LoadIcon(rec.Options?.Icon, AssetFolderFor(rec));
            if (icon != null) clone.DisplaySprite = icon;

            // saves store item.name and rebuild via Item.Create(name) -> All[name].Clone()
            clone.name = "tcnn_" + rec.Id;
            clone.Base = clone;
            Asuna.Items.Item.All[clone.name.ToLower()] = clone;
            if (!string.IsNullOrEmpty(clone.Name))
                Asuna.Items.Item.AllDisplayNames[clone.Name.ToLower()] = clone;

            _created[rec.Id] = clone;
            _log.LogInfo($"Registered item '{clone.Name}' (so:{clone.name}) for outfit '{rec.Id}'.");
            try { OnOutfitCreated?.Invoke(rec.Id, clone); } catch (Exception e) { _log.LogError("OnOutfitCreated handler threw: " + e); }
            return true;
        }

        private bool BuildSkin(PendingOutfit rec)
        {
            var sources = GetSkeletonSources();
            if (sources.Count == 0) return false;

            string newSkinName = "tcnn/" + rec.Id;
            int built = 0;
            foreach (var kv in sources)
            {
                var data = kv.Value;
                if (data == null || data.FindSkin(rec.BaseSkin) == null) continue;

                string folder = ResolveAssetFolder(rec.AssetFolder, kv.Key, rec.BaseSkin);
                var pages = _importer.LoadPagesForSkin(data, rec.BaseSkin, folder);
                if (_resolver.BuildDerivedSkin(data, rec.BaseSkin, newSkinName, pages, null) <= 0) continue;

                built++;
                if (kv.Key == PortraitSkeletonName) rec.PortraitBuilt = true;
                if (kv.Key == OverworldSkeletonName) rec.OverworldBuilt = true;
            }

            if (built == 0) return false;

            _log.LogInfo($"Skin '{newSkinName}' ready on {built} skeleton(s) (portrait={rec.PortraitBuilt}, overworld={rec.OverworldBuilt}).");
            RefreshLive();
            return true;
        }



        public Asuna.Items.Equipment GetCreated(string id)
        {
            Asuna.Items.Equipment eq;
            return _created.TryGetValue(id, out eq) ? eq : null;
        }

        public IEnumerable<KeyValuePair<string, Asuna.Items.Equipment>> Created => _created;

        public bool GiveToPlayer(string id, bool equip = false)
        {
            var template = GetCreated(id);
            if (template == null) { _log.LogWarning($"No created outfit '{id}'."); return false; }
            var player = Asuna.CharManagement.Character.Player;
            if (player == null) { _log.LogWarning("No player character."); return false; }

            // Hand out a CLONE, never the registered template: loading a save clears the
            // inventory with deleteItems:true, which would Destroy() the template and leave
            // Item.All pointing at a dead object (the item then vanishes on reload).
            var item = Asuna.Items.Item.Create(template.name) as Asuna.Items.Equipment;
            if (item == null) { _log.LogWarning($"Item.Create('{template.name}') failed — not registered?"); return false; }

            player.Inventory.Add(item);  // AllItems is a computed view; adding there is discarded
            if (equip) player.EquipItem(item);
            RefreshLive();
            _log.LogInfo($"Gave '{item.Name}' to player{(equip ? " and equipped it" : "")}.");
            return true;
        }

        private void RefreshLive()
        {
            _game?.RefreshLivePortraits();
            _game?.RefreshLiveOverworld();
        }

        public List<KeyValuePair<string, List<string>>> QuerySkins(string filter = null)
        {
            var output = new List<KeyValuePair<string, List<string>>>();
            var lives = _game?.GetLiveGraphics();
            if (lives == null) return output;

            foreach (var kv in lives)
            {
                var names = new List<string>();
                foreach (var skin in kv.Value.Skeleton.Data.Skins)
                {
                    if (skin?.Name == null) continue;
                    if (!string.IsNullOrEmpty(filter) && skin.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    names.Add(skin.Name);
                }
                names.Sort(System.StringComparer.OrdinalIgnoreCase);
                output.Add(new KeyValuePair<string, List<string>>(kv.Key, names));
            }
            return output;
        }

        public string ExportSkin(string skinName)
        {
            var lives = GetSkeletonSources();
            if (lives.Count == 0)
            {
                _log.LogWarning("No skeleton available to export from (is a save loaded?).");
                return null;
            }
            string outRoot = Path.Combine(_baseDir, "exports");
            string lastDir = null;
            foreach (var kv in lives)
            {
                if (kv.Value.FindSkin(skinName) == null) continue;
                var dir = _exporter.Export(kv.Value, skinName, outRoot, kv.Key);
                if (dir != null) lastDir = dir;
            }
            if (lastDir != null) { ExportItemIcon(skinName, lastDir); return lastDir; }
            _log.LogWarning($"Skin '{skinName}' not found on any visible character — check the name with outfit.skins.");
            return null;
        }

        private UnityEngine.Sprite LoadIcon(string icon, string assetFolder)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            string path = Path.IsPathRooted(icon) ? icon : Path.Combine(assetFolder ?? _baseDir, icon);
            if (!File.Exists(path)) { _log.LogWarning($"Icon not found: {path}"); return null; }
            try
            {
                var tex = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
                tex.filterMode = UnityEngine.FilterMode.Bilinear;
                return UnityEngine.Sprite.Create(tex, new UnityEngine.Rect(0, 0, tex.width, tex.height),
                                                 new UnityEngine.Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e) { _log.LogWarning("Icon load failed: " + e.Message); return null; }
        }

        private string ResolveAssetFolder(string assetFolder, string skeletonName, string skinName)
            => string.IsNullOrEmpty(assetFolder)
                ? Path.Combine(_baseDir, "exports", SkinExporter.Sanitize(skinName))
                : (Path.IsPathRooted(assetFolder) ? assetFolder : Path.Combine(_baseDir, assetFolder));

        public int ImportSkin(string skinName, string assetFolder = null)
        {
            var graphics = _game?.GetLiveGraphics();
            if (graphics == null || graphics.Count == 0)
            {
                _log.LogWarning("No visible character to import onto. Open a dialogue and try again.");
                return -1;
            }
            foreach (var kv in graphics)
            {
                if (kv.Value.Skeleton.Data.FindSkin(skinName) == null) continue;
                string folder = ResolveAssetFolder(assetFolder, kv.Key, skinName);
                var pageMap = _importer.LoadFiles(kv.Value.Skeleton, skinName, folder);
                if (pageMap.Count > 0)
                {
                    _imports[skinName] = pageMap;
                    _resolver.SwapPagesInPlace(kv.Value.Skeleton.Data, "import:" + skinName, skinName, pageMap);
                    kv.Value.SetAllDirty();
                }
                return pageMap.Count;
            }
            _log.LogWarning($"Skin '{skinName}' not found on any visible character.");
            return 0;
        }

        public void ClearImports()
        {
            var graphics0 = _game?.GetLiveGraphics();
            if (graphics0 != null)
                foreach (var g in graphics0)
                {
                    var data = g.Value.Skeleton?.Data;
                    if (data == null) continue;
                    foreach (var skinName in new List<string>(_imports.Keys))
                        _resolver.RestoreSkin(data, skinName);
                }
            _imports.Clear();
            var graphics = _game?.GetLiveGraphics();
            if (graphics != null)
                foreach (var kv in graphics) kv.Value.SetAllDirty();
            RefreshLive();
        }

    }
}
