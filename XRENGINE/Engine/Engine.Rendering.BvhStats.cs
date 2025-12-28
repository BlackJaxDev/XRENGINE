using XREngine.Rendering.Compute;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Thread-safe snapshot of the latest GPU BVH metrics.
            /// </summary>
            public static class BvhStats
            {
                private static BvhGpuProfiler.Metrics _latest = BvhGpuProfiler.Metrics.Empty;
                private static readonly object _lock = new();

                public static BvhGpuProfiler.Metrics Latest
                {
                    get
                    {
                        lock (_lock)
                            return _latest;
                    }
                }

                internal static void Publish(BvhGpuProfiler.Metrics metrics)
                {
                    // Simple lock to ensure consistent reads; metrics is a small struct.
                    lock (_lock)
                        _latest = metrics;
                }
            }
        }
    }
}
