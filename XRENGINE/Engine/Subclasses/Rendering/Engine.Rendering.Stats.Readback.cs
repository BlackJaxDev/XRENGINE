using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                private static int _gpuMappedBuffers;
                private static long _gpuReadbackBytes;
                private static int _lastFrameGpuMappedBuffers;
                private static long _lastFrameGpuReadbackBytes;

                /// <summary>
                /// Number of GPU buffers mapped for CPU access in the last completed frame.
                /// </summary>
                public static int GpuMappedBuffers => _lastFrameGpuMappedBuffers;

                /// <summary>
                /// Total bytes read back from GPU buffers in the last completed frame.
                /// </summary>
                public static long GpuReadbackBytes => _lastFrameGpuReadbackBytes;

                /// <summary>
                /// Records that a GPU buffer was mapped for CPU access.
                /// </summary>
                public static void RecordGpuBufferMapped(int count = 1)
                {
                    if (!EnableTracking || count <= 0)
                        return;

                    Interlocked.Add(ref _gpuMappedBuffers, count);
                }

                /// <summary>
                /// Records the number of bytes read back from GPU buffers.
                /// </summary>
                public static void RecordGpuReadbackBytes(long bytes)
                {
                    if (!EnableTracking || bytes <= 0)
                        return;

                    Interlocked.Add(ref _gpuReadbackBytes, bytes);
                }
            }
        }
    }
}
