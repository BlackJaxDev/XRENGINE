namespace XREngine.Execution;

/// <summary>Creates the built-in general, auxiliary, and dedicated engine loop domains.</summary>
internal sealed class ThreadedWorkerDomainFactory : IEngineWorkerDomainFactory, IEngineLoopDomainFactory, IEngineRenderWorkDomainFactory
{
    public IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount)
        => new EngineGeneralWorkDomain(jobs, workerCount);

    public IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs)
        => new EngineJobAuxiliaryWorkDomain(jobs);

    public IRenderWorkDomainOwner CreateRenderDomain(int backgroundWorkerCount,
        XREngine.Data.Rendering.ERenderWorkerQos qos, int frameSlotCount,
        int queueCapacityPerLane, int batchPoolCapacity, int initialItemCapacity,
        int initialDependencyCapacity, int inlineItemThreshold)
        => new ThreadedRenderWorkDomain(backgroundWorkerCount, qos, frameSlotCount,
            queueCapacityPerLane, batchPoolCapacity, initialItemCapacity,
            initialDependencyCapacity, inlineItemThreshold);

    public IEngineLoopDomain CreateLoopDomain(Action update, Action collectVisible, Action fixedUpdate)
        => new EngineLoopDomain(update, collectVisible, fixedUpdate);
}
