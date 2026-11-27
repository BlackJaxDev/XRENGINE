namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class Rvc
                {
                    private static readonly object Sync = new();

                    private static RvcFrameCounters _frameCounters;
                    private static RvcFrameCounters _lastFrameCounters;
                    private static RvcFrameProfileSnapshot _frameProfile;
                    private static RvcFrameProfileSnapshot _lastFrameProfile;
                    private static RvcShadeletTelemetryCounters _shadeletTelemetry;
                    private static RvcShadeletCacheCounters _shadeletCache;
                    private static long _shadeletTelemetryRequests;
                    private static long _shadeletTelemetryDispatches;
                    private static long _shadeletTelemetryReadbacks;
                    private static long _shadeletTelemetryDecoded;
                    private static long _shadeletTelemetrySkips;
                    private static string? _shadeletTelemetrySkipReason;
                    private static long _shadeletCacheDispatches;
                    private static long _shadeletCacheDecoded;

                    public static RvcFrameCounters FrameCounters
                    {
                        get
                        {
                            lock (Sync)
                                return _lastFrameCounters;
                        }
                    }

                    public static RvcFrameProfileSnapshot FrameProfile
                    {
                        get
                        {
                            lock (Sync)
                                return _lastFrameProfile;
                        }
                    }

                    public static ulong TotalProfiledPixels => FrameProfile.ViewSet.TotalPixelCount;
                    public static int ProfiledViewCount => FrameProfile.ViewCount;
                    public static bool ProfiledQuadViewSet => FrameProfile.ViewSet.IsQuadViewSet;
                    public static ERvcFallbackReason FallbackReason => FrameProfile.FallbackReason;

                    internal static void SnapshotAndReset()
                    {
                        lock (Sync)
                        {
                            _lastFrameCounters = _frameCounters;
                            _lastFrameProfile = _frameProfile;
                            _frameCounters = RvcFrameCounters.Empty;
                            _frameProfile = RvcFrameProfileSnapshot.Empty;
                        }
                    }

                    public static void RecordFrameCounters(RvcFrameCounters counters)
                    {
                        if (!EnableTracking)
                            return;

                        lock (Sync)
                            _frameCounters = counters;
                    }

                    /// <summary>
                    /// Latest completed GPU shadelet telemetry. The value arrives through a delayed
                    /// readback, so it is kept across frame resets until a newer record replaces it.
                    /// </summary>
                    public static RvcShadeletTelemetryCounters ShadeletTelemetry
                    {
                        get
                        {
                            lock (Sync)
                                return _shadeletTelemetry;
                        }
                    }

                    /// <summary>
                    /// Stores a completed shadelet telemetry record. Older frames do not replace newer ones.
                    /// </summary>
                    public static void RecordShadeletTelemetry(in RvcShadeletTelemetryCounters counters)
                    {
                        Interlocked.Increment(ref _shadeletTelemetryDecoded);
                        lock (Sync)
                        {
                            if (counters.FrameId >= _shadeletTelemetry.FrameId)
                                _shadeletTelemetry = counters;
                        }
                    }

                    /// <summary>Stage requests that asked for shadelet telemetry.</summary>
                    public static long ShadeletTelemetryRequests => Interlocked.Read(ref _shadeletTelemetryRequests);

                    /// <summary>Telemetry kernel dispatches that the backend recorded.</summary>
                    public static long ShadeletTelemetryDispatches => Interlocked.Read(ref _shadeletTelemetryDispatches);

                    /// <summary>Recorded dispatches whose counter readback was attached.</summary>
                    public static long ShadeletTelemetryReadbacks => Interlocked.Read(ref _shadeletTelemetryReadbacks);

                    /// <summary>Completed readbacks that decoded into a telemetry record.</summary>
                    public static long ShadeletTelemetryDecoded => Interlocked.Read(ref _shadeletTelemetryDecoded);

                    /// <summary>Requests that the backend could not run, and the latest reason.</summary>
                    public static long ShadeletTelemetrySkips => Interlocked.Read(ref _shadeletTelemetrySkips);

                    public static string? ShadeletTelemetrySkipReason => Volatile.Read(ref _shadeletTelemetrySkipReason);

                    public static void RecordShadeletTelemetryRequest()
                        => Interlocked.Increment(ref _shadeletTelemetryRequests);

                    public static void RecordShadeletTelemetryDispatch(bool readbackAttached)
                    {
                        Interlocked.Increment(ref _shadeletTelemetryDispatches);
                        if (readbackAttached)
                            Interlocked.Increment(ref _shadeletTelemetryReadbacks);
                    }

                    /// <summary>Counts a request that the backend could not run. The reason must be a cached string.</summary>
                    public static void RecordShadeletTelemetrySkipped(string reason)
                    {
                        Interlocked.Increment(ref _shadeletTelemetrySkips);
                        Volatile.Write(ref _shadeletTelemetrySkipReason, reason);
                    }

                    /// <summary>Latest completed GPU shadelet-cache counters from delayed diagnostic readback.</summary>
                    public static RvcShadeletCacheCounters ShadeletCache
                    {
                        get
                        {
                            lock (Sync)
                                return _shadeletCache;
                        }
                    }

                    /// <summary>Number of frames on which the RVC shadelet-cache GPU passes dispatched.</summary>
                    public static long ShadeletCacheDispatches => Interlocked.Read(ref _shadeletCacheDispatches);

                    /// <summary>Number of completed GPU shadelet-cache counter readbacks.</summary>
                    public static long ShadeletCacheDecoded => Interlocked.Read(ref _shadeletCacheDecoded);

                    /// <summary>Records one RVC shadelet-cache GPU frame dispatch.</summary>
                    public static void RecordShadeletCacheDispatch()
                        => Interlocked.Increment(ref _shadeletCacheDispatches);

                    /// <summary>Stores decoded GPU shadelet-cache counters when newer than the retained frame.</summary>
                    public static void RecordShadeletCache(in RvcShadeletCacheCounters counters)
                    {
                        Interlocked.Increment(ref _shadeletCacheDecoded);
                        lock (Sync)
                        {
                            if (counters.FrameId >= _shadeletCache.FrameId)
                                _shadeletCache = counters;
                        }
                    }

                    public static void RecordFrameProfile(RvcFrameProfileSnapshot profile)
                    {
                        if (!EnableTracking)
                            return;

                        lock (Sync)
                            _frameProfile = profile;
                    }
                }
            }
        }
    }
}
