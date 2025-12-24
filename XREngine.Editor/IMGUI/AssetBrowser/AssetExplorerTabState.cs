namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class AssetExplorerTabState(string id, string displayName)
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public string RootPath { get; set; } = string.Empty;
        public string CurrentDirectory { get; set; } = string.Empty;
        public string? SelectedPath { get; set; }
        public bool UseTileView { get; set; }
        public float TileViewScale { get; set; } = 1.0f;
        public string? RenamingPath { get; set; }
        public bool RenamingIsDirectory { get; set; }
        public bool RenameFocusRequested { get; set; }
        public Dictionary<string, AssetExplorerPreviewCacheEntry> PreviewCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
