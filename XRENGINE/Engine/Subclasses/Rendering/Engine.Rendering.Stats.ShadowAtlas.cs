using XREngine.Rendering.Shadows;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class ShadowAtlas
                {
                    private static readonly object Sync = new();
                    private static ShadowAtlasSolveDiagnostics _lastSolveDiagnostics;

                    public static ShadowAtlasSolveDiagnostics LastSolveDiagnostics
                    {
                        get
                        {
                            lock (Sync)
                                return _lastSolveDiagnostics;
                        }
                    }

                    public static void RecordSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
                    {
                        if (!EnableTracking)
                            return;

                        lock (Sync)
                            _lastSolveDiagnostics = diagnostics;
                    }
                }
            }
        }
    }
}
