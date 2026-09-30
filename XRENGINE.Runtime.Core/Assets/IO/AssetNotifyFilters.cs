namespace XREngine.Core.Files;

/// <summary>Identifies file metadata changes requested from a capable asset source.</summary>
[Flags]
public enum AssetNotifyFilters
{
    FileName = 1, DirectoryName = 2, Attributes = 4, Size = 8,
    LastWrite = 16, LastAccess = 32, CreationTime = 64, Security = 256,
}
