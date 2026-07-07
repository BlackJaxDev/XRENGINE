namespace XREngine.ControlPlane;

public sealed partial class InMemoryControlPlane
{
    private sealed class HostState
    {
        public ControlPlaneHostRegistration Registration { get; init; } = new();
    }
}
