using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using Spine;
using Spine.Unity;
using Asuna.CharManagement;  // CharacterSprite_Spine
using TCNNOutfits.Core;

namespace TCNNOutfits.Game
{
    // A live skeleton we can inject meshes into, whether it renders through a UI SkeletonGraphic
    // (dialogue portrait) or a MeshRenderer SkeletonAnimation (overworld chibi).
    public sealed class LiveSkeleton
    {
        public string Name;
        public Skeleton Skeleton;
        public SkeletonData Data;
        public global::Spine.Unity.ISkeletonAnimation Animated;   // UpdateComplete source (both renderers)
        public SkeletonGraphic Graphic;                           // non-null only for UI portraits
        public Material Material;                                 // preferred region template (null => derive from source)
        public System.Action MarkDirty;                           // force a re-mesh where needed
    }

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

        // Every live skeleton a mesh outfit can inject into: the dialogue portraits (UI SkeletonGraphic)
        // AND the overworld chibi (MeshRenderer SkeletonAnimation). GetLiveGraphics only sees portraits,
        // so custom meshes never reached the chibi — this covers both behind one shape.
        public List<LiveSkeleton> GetLiveSkeletons()
        {
            var result = new List<LiveSkeleton>();

            foreach (var p in UnityEngine.Object.FindObjectsOfType<CharacterSprite_Spine>())
            {
                SkeletonGraphic sg;
                try { sg = p.SkeletonGraphic; } catch { continue; }
                if (sg == null || sg.Skeleton?.Data == null) continue;
                var g = sg;
                result.Add(new LiveSkeleton
                {
                    Name = sg.skeletonDataAsset != null ? sg.skeletonDataAsset.name : p.name,
                    Skeleton = sg.Skeleton, Data = sg.Skeleton.Data,
                    Animated = sg, Graphic = sg, Material = sg.material, MarkDirty = () => g.SetAllDirty()
                });
            }

            // World renderers (SkeletonAnimation / MeshRenderer). Include ALL of them and let injection
            // decide by fit — BuildSkinInto is non-destructive, so a skeleton our meshes don't match is
            // left untouched. This also stops us guessing which one is "the overworld".
            var player = Asuna.CharManagement.Character.Player;
            var overworld = player?.OverworldSpineSkeleton != null ? player.OverworldSpineSkeleton.GetSkeletonData(true) : null;
            foreach (var sa in UnityEngine.Object.FindObjectsOfType<global::Spine.Unity.SkeletonAnimation>())
            {
                var data = sa?.Skeleton?.Data;
                if (data == null) continue;
                string nm = sa.skeletonDataAsset != null ? sa.skeletonDataAsset.name : sa.name;
                string key = nm + "#" + data.Slots.Count;
                if (_loggedWorld.Add(key))
                    _log.LogInfo($"[world skeleton] '{nm}': slots={data.Slots.Count}" +
                                 $"{(data == overworld ? "  <- player.OverworldSpineSkeleton" : "")}");
                result.Add(new LiveSkeleton
                {
                    Name = nm, Skeleton = sa.Skeleton, Data = data,
                    Animated = sa, Graphic = null, MarkDirty = null   // SkeletonAnimation re-meshes every LateUpdate
                });
            }
            return result;
        }
        private readonly HashSet<string> _loggedWorld = new HashSet<string>();

        // Overworld characters compose separately (CharacterHandler_Spine + SkeletonAnimation).
        // Without this, a skin built after they composed leaves the chibi invisible until
        // something re-composes it (e.g. unequip/equip).
        public void RefreshLiveOverworld()
        {
            foreach (var h in UnityEngine.Object.FindObjectsOfType<CharacterHandler_Spine>())
            {
                try { h.RefreshSkin(); }
                catch (Exception e) { _log.LogWarning("RefreshSkin failed on " + h.name + ": " + e.Message); }
            }
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
