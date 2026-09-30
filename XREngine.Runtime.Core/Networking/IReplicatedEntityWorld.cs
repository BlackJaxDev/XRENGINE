namespace XREngine.Networking;

/// <summary>
/// Host-owned bridge between the transport-neutral replication protocol and the scene graph.
/// Every member is called only through <see cref="IRuntimeNetworkingHostServices.EnqueueSimulation"/>.
/// </summary>
public interface IReplicatedEntityWorld
{
    ReplicatedWorldSnapshot CaptureSnapshot(Guid sessionId, long tickId);
    bool ValidateSnapshot(ReplicatedWorldSnapshot snapshot, out string? error);
    bool ValidateDelta(ReplicatedWorldDelta delta, out string? error);
    bool ApplySnapshot(ReplicatedWorldSnapshot snapshot, out string? error);
    bool ApplyDelta(ReplicatedWorldDelta delta, out string? error);
    void Reset();
}
