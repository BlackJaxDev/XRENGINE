using XREngine.Execution;

namespace XREngine.Rendering;

/// <summary>Adapts the existing process scheduler without creating a second worker domain.</summary>
internal sealed class RuntimePresentationlessWorkServices(EngineWorkScheduler scheduler) : IRuntimeRenderWorkServices
{
    public EngineExecutionTopology ExecutionTopology => scheduler.Topology;
    public JobManager GeneralJobs => scheduler.GeneralJobs;
    public RenderWorkDomain RenderWork => scheduler.Render;

    public CompletedDiagnosticDecodeJob ScheduleCompletedDiagnosticDecode(
        in CompletedDiagnosticPayload payload, JobPriority priority = JobPriority.Low)
    {
        var job = new CompletedDiagnosticDecodeJob(payload);
        GeneralJobs.Schedule(job, priority, JobAffinity.Any);
        return job;
    }
}
