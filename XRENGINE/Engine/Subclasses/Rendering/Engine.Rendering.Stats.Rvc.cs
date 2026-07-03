namespace XREngine
{
    public static partial class Engine
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
