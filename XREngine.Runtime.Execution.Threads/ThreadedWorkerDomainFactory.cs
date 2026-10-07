namespace XREngine.Execution;

/// <summary>Creates the built-in general and auxiliary worker domains.</summary>
internal sealed class ThreadedWorkerDomainFactory : IEngineWorkerDomainFactory
{
    public IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount)
        => new EngineGeneralWorkDomain(jobs, workerCount);

    public IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs)
        => new EngineJobAuxiliaryWorkDomain(jobs);
}
