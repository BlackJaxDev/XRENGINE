using System.Diagnostics;

namespace XREngine.Timers;

/// <summary>
/// Supplies the host-independent timing signals consumed by runtime systems.
/// Rendering cadence and window-pump policy remain composition concerns.
/// </summary>
public interface IRuntimeTimingServices
{
    long ElapsedTicks { get; }
    long UpdateDeltaTicks { get; }
    long FixedDeltaTicks { get; }
    float UpdateDeltaSeconds { get; }
    float FixedDeltaSeconds { get; }
    event Action? Update;
}

/// <summary>
/// Process timing boundary used by Core networking, physics, and world services.
/// A host installs the active timer for exactly the lifetime of its runtime session.
/// </summary>
public static class RuntimeTimingServices
{
    private static readonly object Sync = new();
    private static readonly IRuntimeTimingServices Default = new StopwatchTimingServices();
    private static IRuntimeTimingServices _current = Default;
    private static long _generation;

    public static IRuntimeTimingServices Current
    {
        get
        {
            lock (Sync)
                return _current;
        }
    }

    public static IDisposable Install(IRuntimeTimingServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (Sync)
        {
            long generation = ++_generation;
            _current = services;
            return new InstallationLease(generation);
        }
    }

    private sealed class InstallationLease(long generation) : IDisposable
    {
        private long _generation = generation;

        public void Dispose()
        {
            long installedGeneration = Interlocked.Exchange(ref _generation, 0L);
            if (installedGeneration == 0L)
                return;

            lock (Sync)
            {
                if (RuntimeTimingServices._generation != installedGeneration)
                    return;

                _current = Default;
                ++RuntimeTimingServices._generation;
            }
        }
    }

    private sealed class StopwatchTimingServices : IRuntimeTimingServices
    {
        private static readonly long DefaultFixedDeltaTicks =
            Math.Max(1L, (long)Math.Round(Stopwatch.Frequency / 60.0));

        public long ElapsedTicks => Stopwatch.GetTimestamp();
        public long UpdateDeltaTicks => 0L;
        public long FixedDeltaTicks => DefaultFixedDeltaTicks;
        public float UpdateDeltaSeconds => 0.0f;
        public float FixedDeltaSeconds => 1.0f / 60.0f;

        public event Action? Update
        {
            add { }
            remove { }
        }
    }
}
