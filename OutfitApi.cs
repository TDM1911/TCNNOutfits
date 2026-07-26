using System;
using System.Collections.Generic;
using Asuna.Items;
using TCNNOutfits.Core;

namespace TCNNOutfits
{
    // Public surface for mods that depend on tcnn.outfits.
    // This mod builds and registers outfits; it never decides how the player obtains them.
    // Consumers (shop, NPC, quest, command, ...) take it from here.
    public static class OutfitApi
    {
        public static bool Ready => OutfitFramework.Instance != null;

        private static readonly List<Action> _readyCallbacks = new List<Action>();

        // Load order between mods is not guaranteed, so a consumer may run before this mod
        // has booted. Register work here and it runs immediately if ready, or as soon as it is.
        public static void WhenReady(Action action)
        {
            if (action == null) return;
            if (Ready) { action(); return; }
            _readyCallbacks.Add(action);
        }

        internal static void FlushReady()
        {
            var pending = _readyCallbacks.ToArray();
            _readyCallbacks.Clear();
            foreach (var a in pending)
            {
                try { a(); } catch (Exception e) { UnityEngine.Debug.LogError("[TCNNOutfits] WhenReady handler threw: " + e); }
            }
        }


        // Fired whenever an outfit item is created and registered.
        public static event Action<string, Equipment> OnOutfitCreated
        {
            add { OutfitFramework.OnOutfitCreated += value; }
            remove { OutfitFramework.OnOutfitCreated -= value; }
        }

        // Build a new outfit from an existing one. Returns the registered item, or null.
        // assetFolder: folder (relative to the mod dir) holding page PNG(s); null = exports/ convention.
        public static Equipment Create(string baseSkin, string id, string assetFolder = null, OutfitOptions options = null)
            => OutfitFramework.Instance?.CreateOutfitItem(baseSkin, id, assetFolder, options);

        public static Equipment Get(string id) => OutfitFramework.Instance?.GetCreated(id);

        public static IEnumerable<KeyValuePair<string, Equipment>> All
            => OutfitFramework.Instance?.Created ?? new Dictionary<string, Equipment>();

        // Convenience for consumers that just want it in the bag.
        public static bool GiveToPlayer(string id, bool equip = false)
            => OutfitFramework.Instance?.GiveToPlayer(id, equip) ?? false;

        // --- custom-MESH outfits (packages exported by the TCNN Mesh Editor) ---

        // Register a mesh-outfit package folder (single.json + page + hide.json + outfit.json). The
        // outfit's id comes from outfit.json. Call from your mod, e.g.:
        //   OutfitApi.RegisterMeshOutfit(Path.Combine(manifest.ModPath, "assets", "my_outfit"));
        public static void RegisterMeshOutfit(string packageFolder)
            => OutfitFramework.Instance?.RegisterMeshOutfit(packageFolder);

        // Wear a registered mesh outfit by id (registers the item, builds it, gives + equips).
        public static bool WearMeshOutfit(string id)
            => OutfitFramework.Instance?.WearMeshOutfit(id) ?? false;

        public static IEnumerable<string> MeshOutfits
            => OutfitFramework.Instance?.MeshOutfitIds ?? new List<string>();
    }
}
