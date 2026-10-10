namespace XREngine.Rendering.Compute;

public sealed partial class GPUPhysicsChainDispatcher
{
    private readonly object _fenceObservationGate = new();
    private PhysicsChainFenceFailureObservation? _lastFenceFailureObservation;

    /// <summary>Copies the last failed page fence observation.</summary>
    public PhysicsChainFenceFailureObservation? LastFenceFailureObservation
    {
        get
        {
            lock (_fenceObservationGate)
                return _lastFenceFailureObservation;
        }
    }

    private void RecordFenceFailureObservation(bool input, int pageIndex,
        ulong ordinalOrEpoch, string stage, XRGpuFence? fence)
    {
        PhysicsChainFenceFailureObservation observation = new(
            input, pageIndex, ordinalOrEpoch,
            RuntimeEngine.Rendering.State.RenderFrameId, stage,
            fence?.DiagnosticRentalId ?? 0,
            fence?.FirstFailureDiagnostic);
        lock (_fenceObservationGate)
            _lastFenceFailureObservation = observation;
    }
}
