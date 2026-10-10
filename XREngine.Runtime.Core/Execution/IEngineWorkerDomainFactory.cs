namespace XREngine.Execution;

/// <summary>Creates physical general and auxiliary worker domains for a job manager.</summary>
/// <remarks>
/// Each returned domain is idle until Start. The factory must clean up a failed
/// creation that returns no domain. The manager owns every returned domain and
/// starts the general domain before the auxiliary domain.
/// </remarks>
public interface IEngineWorkerDomainFactory
{
    IEngineGeneralWorkDomain CreateGeneralDomain(JobManager jobs, int workerCount);
    IEngineJobAuxiliaryWorkDomain CreateAuxiliaryDomain(JobManager jobs);
}
