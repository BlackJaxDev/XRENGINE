using System.Diagnostics;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Supervisor-owned process identity and side effects for one immutable launch generation.
/// </summary>
internal sealed class WorkerExecution
{
    public required ManagedWorkerLaunch Launch { get; init; }
    public required string Directory { get; init; }
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    public Process? Process { get; set; }
    public WindowsWorkerJob? Job { get; set; }
    public Task? LaunchTask { get; set; }
    public Task? StandardOutputTask { get; set; }
    public Task? StandardErrorTask { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public DateTimeOffset? StopRequestedUtc { get; set; }
    public DateTimeOffset? DrainRequestedUtc { get; set; }
    public DateTimeOffset? LastReportUtc { get; set; }
    public ManagedWorkerReport? LastReport { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public bool ExitObserved { get; set; }
}
