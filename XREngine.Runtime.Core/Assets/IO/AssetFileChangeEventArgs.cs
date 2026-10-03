namespace XREngine.Core.Files;

/// <summary>Reports a file change without retaining a native watcher event.</summary>
public class AssetFileChangeEventArgs(AssetFileChangeKind changeType, string fullPath, string? name) : EventArgs
{
    public AssetFileChangeKind ChangeType { get; } = changeType;
    public string FullPath { get; } = fullPath;
    public string? Name { get; } = name;
}
