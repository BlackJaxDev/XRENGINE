namespace XREngine.ControlPlane;

public sealed class ManagedWorkerStartupConfiguration
{
    public int StartupTimeoutMilliseconds { get; set; } = 60_000;
    public int ShutdownTimeoutMilliseconds { get; set; } = 30_000;
    public int TickRate { get; set; } = 60;
    public int PhysicsSubsteps { get; set; } = 1;
    public int FixedDeltaMilliseconds { get; set; } = 16;
    public int ManagementPollMilliseconds { get; set; } = 1_000;
    public int ManagementLeaseMilliseconds { get; set; } = 15_000;
}
