namespace XREngine.Execution;

/// <summary>
/// Owns the process-wide renderer-neutral scheduler and its general,
/// job-auxiliary, and render-critical domains. Application composition resolves
/// the topology and supplies diagnostic hooks.
/// </summary>
public static class RuntimeWorkScheduler
{
    private static readonly object Sync = new();
    private static JobManager? _jobs;
    private static EngineWorkScheduler? _scheduler;
    private static bool _configured;
    private static bool _createdImplicitly;
    private static int _configurationState;
    private static bool _configuringCallerThread;
    private static Action? _configureHooks;

    public static EngineExecutionTopology? Topology { get; private set; }

    /// <summary>Observes the installed execution mode without creating a scheduler.</summary>
    public static bool IsCallerThread => Volatile.Read(ref _jobs)?.IsCallerThreadExecutor ?? false;

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
                {
                    if (_configuringCallerThread || OperatingSystem.IsBrowser())
                        throw new InvalidOperationException("Caller-thread scheduler configuration must finish before accessing runtime jobs.");
                    Monitor.Wait(Sync);
                }

                if (_jobs is not null)
                    return _jobs;

                if (OperatingSystem.IsBrowser())
                {
                    throw new InvalidOperationException(
                        "The browser host must configure caller-thread job execution before accessing runtime jobs.");
                }

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
        if (OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("The browser host must configure caller-thread job execution.");

        JobManager? implicitManager;
        lock (Sync)
        {
            if (_configuringCallerThread || _jobs?.IsCallerThreadExecutor == true)
                throw new InvalidOperationException("Caller-thread execution is already configured; shut it down before installing worker domains.");

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

    /// <summary>
    /// Installs explicit, threadless job execution before shared runtime startup.
    /// Existing worker domains must be shut down by their owning host first;
    /// this method never joins or silently replaces them.
    /// </summary>
    public static void ConfigureCallerThread(
        int? generalQueueLimit = null,
        int? generalQueueWarningThreshold = null,
        Action? configureHooks = null)
    {
        lock (Sync)
        {
            if (_configurationState == 1)
                throw new InvalidOperationException("Runtime scheduler configuration is already in progress.");
            if (_jobs is not null)
            {
                if (_configured && _jobs.IsCallerThreadExecutor)
                    return;

                throw new InvalidOperationException(
                    "A runtime job manager already exists. Shut down its owning host before configuring caller-thread execution.");
            }

            _configurationState = 1;
            _configuringCallerThread = true;
            try
            {
                configureHooks?.Invoke();
                _jobs = new JobManager(
                    maxQueueSize: generalQueueLimit,
                    queueWarningThreshold: generalQueueWarningThreshold,
                    executionMode: JobExecutionMode.CallerThread);
                _scheduler = null;
                Topology = null;
                _configureHooks = configureHooks;
                _createdImplicitly = false;
                _configured = true;
                _configurationState = 2;
            }
            catch
            {
                _configurationState = 0;
                throw;
            }
            finally
            {
                _configuringCallerThread = false;
                Monitor.PulseAll(Sync);
            }
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
