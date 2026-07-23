using System.Collections.Generic;
using UnityEngine;

namespace TCNNOutfits.Core
{
    public sealed class OutfitDefinition
    {
        public string Id;

        public string DisplayName = "";
        public string Author = "";
        public string Version = "";

        public List<string> TargetSkeletons;

        public string BaseSkin;

        public IOutfitArt Art;

        public bool AppliesTo(string skeletonName)
        {
            if (TargetSkeletons == null || TargetSkeletons.Count == 0) return true;
            return TargetSkeletons.Contains(skeletonName);
        }
    }

    public interface IOutfitArt
    {
        Sprite GetSprite();
    }

    public sealed class SolidColorArt : IOutfitArt
    {
        private readonly Color32 _color;
        private Sprite _sprite;

        public SolidColorArt(Color32 color) { _color = color; }

        public Sprite GetSprite()
        {
            if (_sprite != null) return _sprite;
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var fill = new Color32[64];
            for (int i = 0; i < fill.Length; i++) fill[i] = _color;
            tex.SetPixels32(fill);
            tex.Apply(false, false);
            _sprite = Sprite.Create(tex, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 100f);
            _sprite.name = "SolidColorArt";
            return _sprite;
        }
    }

    public sealed class TextureFileArt : IOutfitArt
    {
        private readonly string _path;
        private Sprite _sprite;

        public TextureFileArt(string path) { _path = path; }

        public Sprite GetSprite()
        {
            if (_sprite != null) return _sprite;
            if (!System.IO.File.Exists(_path)) return null;
            var bytes = System.IO.File.ReadAllBytes(_path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) return null;  // resizes to the image
            tex.filterMode = FilterMode.Bilinear;
            _sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _sprite.name = "TextureFileArt:" + System.IO.Path.GetFileName(_path);
            return _sprite;
        }
    }
}
