namespace XREngine.Core.Files;

/// <summary>Reports both sides of a rename through engine-owned values.</summary>
public sealed class AssetFileRenameEventArgs(string fullPath, string? name, string oldFullPath, string? oldName)
    : AssetFileChangeEventArgs(AssetFileChangeKind.Renamed, fullPath, name)
{
    public string OldFullPath { get; } = oldFullPath;
    public string? OldName { get; } = oldName;
}
