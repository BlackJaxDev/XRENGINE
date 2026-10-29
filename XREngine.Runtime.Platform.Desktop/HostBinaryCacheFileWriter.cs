namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Replaces binary cache files using a unique sibling temporary file.</summary>
internal static class HostBinaryCacheFileWriter
{
    internal static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        bool created = false;
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                created = true;
                stream.Write(bytes);
            }
            File.Move(temporaryPath, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }
}
