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
        public string FilteredDirectory { get; set; } = string.Empty;
        public int FilteredRevision { get; set; } = -1;
        public AssetExplorerDirectorySnapshot? FilteredSnapshot { get; set; }
        public List<AssetExplorerEntry> FilteredEntries { get; } = [];
        public Dictionary<string, AssetExplorerPreviewCacheEntry> PreviewCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AssetExplorerDirectorySnapshot> DirectorySnapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public FileSystemWatcher? Watcher { get; set; }
        public string WatcherRootPath { get; set; } = string.Empty;
        public object SnapshotInvalidationLock { get; } = new();
        public HashSet<string> PendingInvalidatedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ClearAllSnapshotsPending { get; set; }
        public object SnapshotBuildLock { get; } = new();
        public HashSet<string> PendingDirectorySnapshotBuilds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AssetExplorerDirectorySnapshot> CompletedDirectorySnapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
