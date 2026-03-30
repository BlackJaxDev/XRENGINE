namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class AssetExplorerDirectorySnapshot(AssetExplorerEntry[] entries, string[] childDirectories)
    {
        public AssetExplorerEntry[] Entries { get; } = entries;
        public string[] ChildDirectories { get; } = childDirectories;
    }
}