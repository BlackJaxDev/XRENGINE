namespace XREngine.Execution;

/// <summary>
/// Owns the process-wide renderer-neutral work scheduler and its general job domain.
/// Application composition resolves the topology and supplies diagnostic hooks.
/// </summary>
public static class RuntimeWorkScheduler
{
    private static readonly object Sync = new();
    private static JobManager? _jobs;
    private static EngineWorkScheduler? _scheduler;
    private static bool _configured;
    private static bool _createdImplicitly;
    private static int _configurationState;
    private static Action? _configureHooks;

    public static EngineExecutionTopology? Topology { get; private set; }
    public static EngineWorkScheduler? Scheduler
    {
        get
        {
            lock (Sync)
                return _scheduler;
        }
    }

    public static JobManager Jobs
    {
        get
        {
            lock (Sync)
            {
                while (_jobs is null && _configurationState == 1)
                    Monitor.Wait(Sync);

                if (_jobs is not null)
                    return _jobs;

                _configureHooks?.Invoke();
                _createdImplicitly = true;
                _configured = false;
                return _jobs = new JobManager();
            }
        }
    }

    public static void Configure(
        EngineExecutionTopology topology,
        int? generalQueueLimit,
        int? generalQueueWarningThreshold,
        Action? configureHooks = null)
    {
        ArgumentNullException.ThrowIfNull(topology);

        JobManager? implicitManager;
        lock (Sync)
        {
            while (_configurationState == 1)
                Monitor.Wait(Sync);

            if (_configured)
            {
                if (!Equals(Topology, topology))
                    throw new InvalidOperationException(
                        "The runtime work scheduler is already configured with a different execution topology.");
                return;
            }

            _configurationState = 1;
            _configureHooks = configureHooks;
            _configureHooks?.Invoke();
            implicitManager = _createdImplicitly ? _jobs : null;
        }

        try
        {
            if (implicitManager is not null && !implicitManager.Shutdown(waitForWorkers: true))
            {
                throw new InvalidOperationException(
                    "The implicit JobManager did not quiesce within the fatal lifecycle bound; " +
                    "installing the process scheduler would create a second worker domain.");
            }

            var scheduler = new EngineWorkScheduler(
                topology,
                generalQueueLimit,
                generalQueueWarningThreshold);

            lock (Sync)
            {
                Topology = topology;
                _scheduler = scheduler;
                _jobs = scheduler.GeneralJobs;
                _createdImplicitly = false;
                _configured = true;
                _configurationState = 2;
                Monitor.PulseAll(Sync);
            }
        }
        catch
        {
            lock (Sync)
            {
                _configurationState = 0;
                Monitor.PulseAll(Sync);
            }
            throw;
        }
    }

    public static bool Shutdown(bool waitForWorkers = true)
    {
        EngineWorkScheduler? scheduler;
        JobManager? jobs;
        lock (Sync)
        {
            scheduler = _scheduler;
            jobs = _jobs;
        }

        bool stopped = scheduler?.Shutdown(waitForWorkers)
            ?? (jobs?.Shutdown(waitForWorkers) ?? true);
        if (!stopped)
            return false;

        lock (Sync)
        {
            _scheduler = null;
            _jobs = null;
            Topology = null;
            _configured = false;
            _createdImplicitly = false;
            _configurationState = 0;
            _configureHooks = null;
            Monitor.PulseAll(Sync);
        }

        return true;
    }
}
