using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using ANToolkit.Debugging;  // ConCommand, Console
using TCNNOutfits.Core;

namespace TCNNOutfits.Game
{
    public sealed class OutfitConsole
    {
        private readonly OutfitFramework _framework;
        private readonly ManualLogSource _log;
        private bool _registered;
        private bool _loggedError;

        public OutfitConsole(OutfitFramework framework, ManualLogSource log)
        {
            _framework = framework;
            _log = log;
        }

        public void TryRegister()
        {
            if (_registered) return;
            try
            {
                var dict = ANToolkit.Debugging.Console.ConCommands;
                int before = dict?.Count ?? -1;

                ConCommand.Add("outfit.skins", CmdSkins);  // list skins on the visible character
                ConCommand.Add("outfit.export", CmdExport);  // export a skin's art + mapping
                ConCommand.Add("outfit.import", CmdImport);  // reimport edited page art (per-attachment)
                ConCommand.Add("outfit.create", CmdCreate);  // derive a NEW outfit item (additive)
                ConCommand.Add("outfit.clearimports", CmdClearImports);
                ConCommand.Add("outfit.list", CmdList);
                ConCommand.Add("outfit.toggle", CmdToggle);
                ConCommand.Add("outfit.apply", CmdApply);
                ConCommand.Add("outfit.clear", CmdClear);
                ConCommand.Add("outfit.reload", CmdReload);
                ConCommand.Add("outfit.created", CmdCreated);
                ConCommand.Add("outfit.give", CmdGive);
                ConCommand.Add("outfit.meshes", CmdMeshes);   // list registered custom-mesh outfits
                ConCommand.Add("outfit.wear", CmdWear);       // wear a custom-mesh outfit by id
                ConCommand.Add("outfit.dumpskel", CmdDumpSkel);  // dump live skeleton(s) for the mesh editor

                dict = ANToolkit.Debugging.Console.ConCommands;
                bool present = dict != null && dict.ContainsKey("outfit.skins");
                _registered = present;
                _log.LogInfo($"Console registration: outfit.skins present={present}, ConCommands {before} -> {(dict?.Count ?? -1)}.");
            }
            catch (Exception e)
            {
                if (!_loggedError) { _loggedError = true; _log.LogError("Console registration threw: " + e); }
            }
        }


        private void CmdMeshes(List<string> args)
        {
            var ids = new List<string>(_framework.MeshOutfitIds);
            Echo(ids.Count == 0 ? "No custom-mesh outfits found (drop a package folder in assets/)."
                                : "Custom-mesh outfits: " + string.Join(", ", ids) + "  —  wear with 'outfit.wear <id>'");
        }

        private void CmdWear(List<string> args)
        {
            if (args == null || args.Count == 0) { Echo("usage: outfit.wear <id>   (list ids with 'outfit.meshes')"); return; }
            string id = args[0].Trim();
            try { Echo(_framework.WearMeshOutfit(id) ? $"Wearing '{id}' (if a character is on screen)." : $"No mesh outfit '{id}'."); }
            catch (Exception e) { Echo("Wear failed: " + e.Message); _log.LogError(e.ToString()); }
        }

        private void CmdDumpSkel(List<string> args)
        {
            try
            {
                var dirs = _framework.DumpSkeletons();
                if (dirs.Count == 0) { Echo("Dump found no live skeletons — open a dialogue / be on the overworld first."); return; }
                Echo($"Dumped {dirs.Count} skeleton(s) for the mesh editor:");
                foreach (var d in dirs) Echo("   " + d);
            }
            catch (Exception e) { Echo("Dump failed: " + e.Message); _log.LogError(e.ToString()); }
        }

        private void Echo(string msg)
        {
            _log.LogInfo(msg);
            try { ANToolkit.Debugging.Console.WriteMessage(msg); } catch { /* console not ready */ }
        }

        private void CmdSkins(List<string> args)
        {
            string filter = args != null && args.Count > 0 ? args[0].Trim() : null;
            var results = _framework.QuerySkins(filter);
            if (results.Count == 0)
            {
                Echo("No visible Spine character. Open a dialogue so a portrait is on screen, then run 'outfit.skins'.");
                return;
            }
            foreach (var kv in results)
            {
                Echo($"Skins on '{kv.Key}' ({kv.Value.Count}{(string.IsNullOrEmpty(filter) ? "" : " matching '" + filter + "'")}):");
                foreach (var n in kv.Value) Echo("   " + n);
            }
        }

        private void CmdExport(List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                Echo("usage: outfit.export <skin>   e.g. outfit.export outfits/default/coat");
                return;
            }
            var dir = _framework.ExportSkin(args[0].Trim());
            Echo(dir != null
                ? "Exported to: " + dir
                : "Export failed — check the skin name with 'outfit.skins' and see LogOutput.log.");
        }

        private void CmdImport(List<string> args)
        {
            if (args == null || args.Count == 0)
            {
                Echo("usage: outfit.import <skin>   e.g. outfit.import outfits/default/coat");
                return;
            }
            int n = _framework.ImportSkin(args[0].Trim());
            if (n > 0) Echo($"Imported: overrode {n} page(s). If you don't see it, reopen the dialogue.");
            else if (n == 0) Echo("Import did nothing — check the skin name and that you exported+edited it. See LogOutput.log.");
        }

        private void CmdCreate(List<string> args)
        {
            if (args == null || args.Count < 2)
            {
                Echo("usage: outfit.create <baseSkin>, <newId> [, <assetFolder>]");
                Echo("  e.g. outfit.create outfits/default/coat, cyber_coat, assets/cyber_coat");
                return;
            }
            string baseSkin = args[0].Trim();
            string newId = args[1].Trim();
            string asset = args.Count > 2 ? args[2].Trim() : null;

            var item = _framework.CreateOutfitItem(baseSkin, newId, asset);
            Echo(item != null
                ? $"Created outfit '{newId}' -> item '{item.Name}'. Registered (not in inventory). Use 'outfit.give {newId}'."
                : "Create failed — see LogOutput.log (check the skin name with outfit.skins).");
        }

        private void CmdCreated(List<string> args)
        {
            int n = 0;
            foreach (var kv in _framework.Created) { Echo($"  {kv.Key} -> {kv.Value.Name}"); n++; }
            if (n == 0) Echo("No outfits created yet.");
        }

        private void CmdGive(List<string> args)
        {
            if (args == null || args.Count == 0) { Echo("usage: outfit.give <id> [, equip]"); return; }
            string id = args[0].Trim();
            bool equip = args.Count > 1 && args[1].Trim().ToLowerInvariant() == "equip";
            Echo(_framework.GiveToPlayer(id, equip)
                ? $"Gave '{id}'{(equip ? " and equipped it" : " — check your inventory")}."
                : $"No created outfit '{id}' (see 'outfit.created').");
        }

        private void CmdClearImports(List<string> args)
        {
            _framework.ClearImports();
            Echo("Cleared imported page overrides.");
        }

        private void CmdList(List<string> args)
        {
            foreach (var o in _framework.Outfits)
                Echo($"  {(_framework.IsActive(o.Id) ? "[x]" : "[ ]")} {o.Id} — {o.DisplayName} (base: {o.BaseSkin})");
        }

        private void CmdToggle(List<string> args)
        {
            if (args == null || args.Count == 0) { _log.LogWarning("usage: outfit.toggle <id>"); return; }
            _framework.Toggle(args[0].Trim());
        }

        private void CmdApply(List<string> args)
        {
            if (args == null || args.Count == 0) { _log.LogWarning("usage: outfit.apply <id>"); return; }
            _framework.Apply(args[0].Trim());
        }

        private void CmdClear(List<string> args)
        {
            if (args == null || args.Count == 0) { _log.LogWarning("usage: outfit.clear <id>"); return; }
            _framework.Clear(args[0].Trim());
        }

        private void CmdReload(List<string> args)
        {
            _framework.ReloadSources();
            _log.LogInfo("Outfit sources reloaded.");
        }
    }
}
