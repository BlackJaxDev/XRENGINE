namespace XREngine.Components;

/// <summary>
/// Managed-memory staging source for CPU backends, tests, and tooling.
/// The supplied memory must remain immutable until this source is disposed.
/// </summary>
public sealed class PhysicsChainReadbackMemorySource(ReadOnlyMemory<byte> data)
    : IPhysicsChainReadbackStagingSource
{
    private ReadOnlyMemory<byte> _data = data;
    private bool _disposed;

    public int ByteCount => _disposed ? 0 : _data.Length;

    public bool TryCopyTo(Span<byte> destination)
    {
        if (_disposed || destination.Length != _data.Length)
            return false;

        _data.Span.CopyTo(destination);
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _data = ReadOnlyMemory<byte>.Empty;
    }
}
