using System;
using Asuna.Dialogues;
using BepInEx.Logging;
using Modding;
using TCNNOutfits.Core;

namespace TCNNOutfits
{
    public class TCNNOutfitsMod : ITCMod
    {
        private OutfitFramework _framework;
        private ManualLogSource _log;

        public void OnModLoaded(ModManifest manifest)
        {
            _log = BepInEx.Logging.Logger.CreateLogSource("TCNNOutfits");
            _log.LogInfo($"TCNN Outfits loading (modPath: {manifest?.ModPath}).");
            try
            {
                _framework = new OutfitFramework(_log, manifest?.ModPath ?? ".");
                _framework.Boot();
            }
            catch (Exception e) { _log.LogError("Boot failed: " + e); }
        }

        public void OnModUnLoaded()
        {
            try { _framework?.Shutdown(); } catch (Exception e) { _log?.LogError("Shutdown failed: " + e); }
        }

        private int _frames;
        public void OnFrame()
        {
            if (_frames == 0) _log?.LogInfo("OnFrame is running.");
            _frames++;
            _framework?.Tick();
        }

        public void OnDialogueStarted(Dialogue dialogue) { }

        public void OnLineStarted(DialogueLine line) { }
    }
}
