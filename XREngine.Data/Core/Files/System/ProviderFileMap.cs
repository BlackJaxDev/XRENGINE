using XREngine.Data;

namespace System;

/// <summary>Adapts a platform mapping while preserving the legacy System.FileMap API.</summary>
internal sealed class ProviderFileMap : FileMap
{
    private IFileMappingLease? _lease;
    public ProviderFileMap(IFileMappingLease lease, FileStream stream, bool ownsStream)
    {
        _lease = lease;
        _addr = lease.Address;
        _length = lease.Length;
        _path = stream.Name;
        _baseStream = ownsStream ? stream : null;
    }
    public override void Dispose()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
        _addr = null;
        base.Dispose();
    }
}
