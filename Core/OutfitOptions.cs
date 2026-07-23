namespace TCNNOutfits.Core
{
    // Item-level metadata for a created outfit. All optional.
    public sealed class OutfitOptions
    {
        public string Title;        // Item.Name  (what the player sees)
        public string Description;  // Item.Description
        public string Icon;         // inventory sprite; absolute, or relative to the asset folder
    }
}
