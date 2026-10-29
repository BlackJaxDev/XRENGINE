namespace XREngine.Execution;

/// <summary>Creates the built-in general, auxiliary, and dedicated engine loop domains.</summary>
internal sealed class ThreadedWorkerDomainFactory : IEngineWorkerDomainFactory, IEngineLoopDomainFactory
{
    public IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount)
        => new EngineGeneralWorkDomain(jobs, workerCount);

    public IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs)
        => new EngineJobAuxiliaryWorkDomain(jobs);

    public IEngineLoopDomain CreateLoopDomain(Action update, Action collectVisible, Action fixedUpdate)
        => new EngineLoopDomain(update, collectVisible, fixedUpdate);
}
