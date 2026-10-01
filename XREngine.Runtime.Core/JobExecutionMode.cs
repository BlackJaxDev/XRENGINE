namespace XREngine;

/// <summary>
/// Selects whether the job manager owns worker threads or is advanced explicitly
/// by its creating thread. Caller-thread execution never installs worker domains.
/// </summary>
public enum JobExecutionMode
{
    WorkerThreads,
    CallerThread,
}
