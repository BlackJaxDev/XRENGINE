using System.IO.MemoryMappedFiles;

namespace XREngine.Data;

/// <summary>Owns a mapped view and its file mapping until streaming is closed.</summary>
public interface IFileMappedStreamLease : IDisposable
{
    /// <summary>The read-write view. Disposing this lease closes the view and mapping.</summary>
    MemoryMappedViewStream Stream { get; }
}
