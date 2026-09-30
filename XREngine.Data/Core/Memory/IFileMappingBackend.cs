namespace XREngine.Data;

/// <summary>Provides native file mapping as an explicitly installed platform capability.</summary>
public interface IFileMappingBackend
{
    IFileMappingLease Map(FileStream stream, bool writable, long offset, long length);
}
