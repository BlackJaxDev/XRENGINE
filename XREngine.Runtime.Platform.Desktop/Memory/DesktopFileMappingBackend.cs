using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Creates native mappings while the portable caller owns the supplied file stream.</summary>
internal sealed class DesktopFileMappingBackend : IFileMappingBackend
{
    public IFileMappingLease Map(FileStream stream, bool writable, long offset, long length)
    {
        FileMapProtect protection = writable ? FileMapProtect.ReadWrite : FileMapProtect.Read;
        return OperatingSystem.IsWindows()
            ? new Data.WFileMap(stream.SafeFileHandle.DangerousGetHandle(), protection, offset, length)
            : new CFileMap(stream, protection, offset, length);
    }
}
