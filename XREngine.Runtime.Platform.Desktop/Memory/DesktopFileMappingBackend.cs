using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Opens desktop files and creates native mappings. The caller owns each stream.</summary>
internal sealed class DesktopFileMappingBackend : IFileMappingBackend
{
    public bool FileExists(string path) => File.Exists(path);

    public bool TryGetFileLength(string path, out long length)
    {
        FileInfo info = new(path);
        if (!info.Exists)
        {
            length = 0;
            return false;
        }

        length = info.Length;
        return true;
    }

    public FileStream OpenFile(string path, bool writable, FileOptions options, Action<string, string> reportFallback)
    {
        try
        {
            return !File.Exists(path)
                ? File.Create(path, 8, options)
                : new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, 8, options);
        }
        catch
        {
            string tempPath = Path.GetTempFileName();
            reportFallback(path, tempPath);
            File.Copy(path, tempPath, true);
            return new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, options | FileOptions.DeleteOnClose);
        }
    }

    public FileStream OpenTemporaryFile(out string path)
        => new(path = Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.RandomAccess | FileOptions.DeleteOnClose);

    public IFileMappingLease Map(FileStream stream, bool writable, long offset, long length)
    {
        FileMapProtect protection = writable ? FileMapProtect.ReadWrite : FileMapProtect.Read;
        return OperatingSystem.IsWindows()
            ? new Data.WFileMap(stream.SafeFileHandle.DangerousGetHandle(), protection, offset, length)
            : new CFileMap(stream, protection, offset, length);
    }
}
