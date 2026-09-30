namespace XREngine.Core.Files;

/// <summary>Identifies a file change reported by an asset-source monitor.</summary>
[Flags]
public enum AssetFileChangeKind { Created = 1, Deleted = 2, Changed = 4, Renamed = 8 }
