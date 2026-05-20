using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                private static int _gpuTransparencyOpaqueOrOtherVisible;
                private static int _gpuTransparencyMaskedVisible;
                private static int _gpuTransparencyApproximateVisible;
                private static int _gpuTransparencyExactVisible;
                private static int _lastFrameGpuTransparencyOpaqueOrOtherVisible;
                private static int _lastFrameGpuTransparencyMaskedVisible;
                private static int _lastFrameGpuTransparencyApproximateVisible;
                private static int _lastFrameGpuTransparencyExactVisible;

                public static int GpuTransparencyOpaqueOrOtherVisible => _lastFrameGpuTransparencyOpaqueOrOtherVisible;

                public static int GpuTransparencyMaskedVisible => _lastFrameGpuTransparencyMaskedVisible;

                public static int GpuTransparencyApproximateVisible => _lastFrameGpuTransparencyApproximateVisible;

                public static int GpuTransparencyExactVisible => _lastFrameGpuTransparencyExactVisible;

                public static void RecordGpuTransparencyDomainCounts(
                    uint opaqueOrOtherVisible,
                    uint maskedVisible,
                    uint approximateVisible,
                    uint exactVisible)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _gpuTransparencyOpaqueOrOtherVisible, checked((int)opaqueOrOtherVisible));
                    Interlocked.Exchange(ref _gpuTransparencyMaskedVisible, checked((int)maskedVisible));
                    Interlocked.Exchange(ref _gpuTransparencyApproximateVisible, checked((int)approximateVisible));
                    Interlocked.Exchange(ref _gpuTransparencyExactVisible, checked((int)exactVisible));
                }
            }
        }
    }
}
