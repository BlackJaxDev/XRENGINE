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

    internal static void WriteAndReplace(string cachePath, ReadOnlySpan<byte> payload, DateTime sourceLastWriteTimeUtc)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        string stagedPath = Path.Combine(directory ?? string.Empty, $".xre-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (FileStream stream = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                created = true;
                stream.Write(payload);
            }
            try { File.SetLastWriteTimeUtc(stagedPath, sourceLastWriteTimeUtc); }
            catch { /* The timestamp is best effort. */ }
            File.Move(stagedPath, cachePath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(stagedPath); }
                catch { /* Preserve the original write failure. */ }
            }
        }
    }
}
