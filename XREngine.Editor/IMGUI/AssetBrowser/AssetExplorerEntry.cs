namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private readonly record struct AssetExplorerEntry(string Name, string Path, bool IsDirectory, long Size, DateTime ModifiedUtc);
}
