namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Writes staged texture streaming cache files on the host filesystem.</summary>
internal static class TextureStreamingCacheFileWriter
{
    internal static void PrepareDirectory(string cachePath)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    internal static void WriteStaged(string cachePath, ReadOnlySpan<byte> payload)
    {
        using FileStream stream = new(cachePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        stream.Write(payload);
    }

    internal static void PublishStaged(string cachePath, DateTime sourceLastWriteTimeUtc)
    {
        if (File.Exists(cachePath))
            File.Delete(cachePath);
        File.Move(cachePath + ".tmp", cachePath);
        try { File.SetLastWriteTimeUtc(cachePath, sourceLastWriteTimeUtc); }
        catch { /* The timestamp is best effort. */ }
    }
}
