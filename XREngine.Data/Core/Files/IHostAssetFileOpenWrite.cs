namespace XREngine.Data;

/// <summary>Opens an authoring file for a non-truncating write.</summary>
public interface IHostAssetFileOpenWrite
{
    /// <summary>Opens or creates a write-only file at offset zero without truncation, sharing, or parent directory creation. The caller owns the stream.</summary>
    Stream OpenWrite(string path);
}
