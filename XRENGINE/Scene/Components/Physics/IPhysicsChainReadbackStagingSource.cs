namespace XREngine.Components;

/// <summary>
/// Backend-owned persistently mapped or otherwise non-blocking staging source.
/// The readback service calls <see cref="TryCopyTo"/> only after the associated
/// fence signals and disposes the source exactly once when the slot is freed.
/// </summary>
public interface IPhysicsChainReadbackStagingSource : IDisposable
{
    int ByteCount { get; }
    bool TryCopyTo(Span<byte> destination);
}
