using XREngine.Components;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Lease over a backend-owned staging buffer. The dispatcher retains the buffer;
/// disposing this object only ends the service slot's lease. Mapping and required
/// noncoherent invalidation happen after the completion fence in the backend.
/// </summary>
internal sealed class PhysicsChainMappedReadbackStagingSource : IPhysicsChainReadbackStagingSource
{
    private IPhysicsChainComputeBackend? _backend;
    private XRDataBuffer? _buffer;
    private int _byteCount;

    public int ByteCount => IsValid ? _byteCount : 0;
    public bool IsValid => _backend is not null && _buffer is not null && _byteCount > 0;

    public void Reset(IPhysicsChainComputeBackend backend, XRDataBuffer buffer, int byteCount)
    {
        _backend = backend;
        _buffer = buffer;
        _byteCount = byteCount;
    }

    public unsafe bool TryCopyTo(Span<byte> destination)
    {
        if (_backend is null || _buffer is null || destination.Length != _byteCount)
            return false;

        return _backend.TryReadBuffer(_buffer, destination);
    }

    public void Dispose()
    {
        _backend = null;
        _buffer = null;
        _byteCount = 0;
    }
}
