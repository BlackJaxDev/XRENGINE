using XREngine.Execution;

namespace XREngine.Rendering;

/// <summary>
/// Host-installed access to the one process execution scheduler. Runtime
/// rendering uses this capability instead of constructing worker pools.
/// </summary>
public interface IRuntimeRenderWorkServices
{
    EngineExecutionTopology ExecutionTopology
        => throw CreateNotInstalledException();

    JobManager GeneralJobs
        => throw CreateNotInstalledException();

    RenderWorkDomain RenderWork
        => throw CreateNotInstalledException();

    /// <summary>
    /// Schedules decoding only after diagnostic words are CPU-visible. The
    /// payload type cannot represent pending GPU synchronization.
    /// </summary>
    CompletedDiagnosticDecodeJob ScheduleCompletedDiagnosticDecode(
        in CompletedDiagnosticPayload payload,
        JobPriority priority = JobPriority.Low)
        => throw CreateNotInstalledException();

    private static InvalidOperationException CreateNotInstalledException()
        => new(
            "Runtime render work services are unavailable until the engine execution scheduler is installed.");
}
