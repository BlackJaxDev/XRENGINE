namespace XREngine.Rendering;

/// <summary>Creates host-owned copy-on-write spill mappings for buffer client bytes.</summary>
public interface IXRBufferSpillStorage
{
    /// <summary>Writes bytes to a session spill file and maps them copy-on-write.</summary>
    IXRBufferSpillLease WriteAndMap(ReadOnlySpan<byte> bytes);

    /// <summary>Maps a caller-owned file copy-on-write and takes ownership of it.</summary>
    IXRBufferSpillLease Map(FileStream file, uint length);
}
