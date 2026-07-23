using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using Spine.Unity;
using Asuna.CharManagement;  // CharacterSprite_Spine
using TCNNOutfits.Core;

namespace TCNNOutfits.Game
{
    public sealed class GameIntegration
    {
        private readonly ManualLogSource _log;

        public GameIntegration(OutfitFramework framework, ManualLogSource log)
        {
            _log = log;
        }

        public List<KeyValuePair<string, SkeletonGraphic>> GetLiveGraphics()
        {
            var result = new List<KeyValuePair<string, SkeletonGraphic>>();
            foreach (var p in UnityEngine.Object.FindObjectsOfType<CharacterSprite_Spine>())
            {
                SkeletonGraphic sg;
                try { sg = p.SkeletonGraphic; } catch { continue; }  // render stage may not be ready
                if (sg == null || sg.Skeleton == null || sg.Skeleton.Data == null) continue;
                string name = sg.skeletonDataAsset != null ? sg.skeletonDataAsset.name : p.name;
                result.Add(new KeyValuePair<string, SkeletonGraphic>(name, sg));
            }
            return result;
        }

        public void RefreshLivePortraits()
        {
            foreach (var p in UnityEngine.Object.FindObjectsOfType<CharacterSprite_Spine>())
            {
                try
                {
                    p.Populate();
                    var sg = p.SkeletonGraphic;
                    if (sg != null) sg.SetAllDirty();
                }
                catch (Exception e) { _log.LogWarning("Refresh failed on " + p.name + ": " + e.Message); }
            }
        }
    }
}
