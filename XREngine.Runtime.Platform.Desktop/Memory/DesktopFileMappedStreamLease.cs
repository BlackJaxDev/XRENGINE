using System.IO.MemoryMappedFiles;
using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Owns the desktop file mapping and its whole-file read-write view.</summary>
internal sealed class DesktopFileMappedStreamLease : IFileMappedStreamLease
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewStream _stream;
    private int _disposed;

    internal DesktopFileMappedStreamLease(string path)
    {
        _mapping = MemoryMappedFile.CreateFromFile(path);
        try
        {
            _stream = _mapping.CreateViewStream();
        }
        catch
        {
            _mapping.Dispose();
            throw;
        }
    }

    public MemoryMappedViewStream Stream => _stream;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _stream.Dispose(); }
        finally { _mapping.Dispose(); }
    }
}
