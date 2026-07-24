namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Pins a published frame slot while a consumer copies it.
/// </summary>
public readonly struct PhysicsDebugFrameLease : IDisposable
{
    private readonly PhysicsDebugFramePublisher? _publisher;
    private readonly PhysicsDebugFrameStorage? _storage;

    internal PhysicsDebugFrameLease(
        PhysicsDebugFramePublisher publisher,
        PhysicsDebugFrameStorage storage)
    {
        _publisher = publisher;
        _storage = storage;
        Frame = new PhysicsDebugFrame(storage);
    }

    public PhysicsDebugFrame Frame { get; }

    public void Dispose()
    {
        if (_publisher is not null && _storage is not null)
            _publisher.Release(_storage);
    }
}
