namespace XREngine.Data;

/// <summary>Owns a mapped byte range until consumers have finished accessing its address.</summary>
public interface IFileMappingLease : IDisposable
{
    VoidPtr Address { get; }
    long Length { get; }
}
