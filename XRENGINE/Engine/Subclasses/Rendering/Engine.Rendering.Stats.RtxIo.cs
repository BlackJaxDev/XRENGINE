using System;
using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class RtxIo
                {
                    private static int _rtxIoDecompressCalls;
                    private static int _rtxIoCopyIndirectCalls;
                    private static long _rtxIoCompressedBytes;
                    private static long _rtxIoDecompressedBytes;
                    private static long _rtxIoCopyBytes;
                    private static long _rtxIoSubmissionTimeTicks;
                    private static int _lastFrameRtxIoDecompressCalls;
                    private static int _lastFrameRtxIoCopyIndirectCalls;
                    private static long _lastFrameRtxIoCompressedBytes;
                    private static long _lastFrameRtxIoDecompressedBytes;
                    private static long _lastFrameRtxIoCopyBytes;
                    private static long _lastFrameRtxIoSubmissionTimeTicks;

                    public static int RtxIoDecompressCalls => _lastFrameRtxIoDecompressCalls;
                    public static int RtxIoCopyIndirectCalls => _lastFrameRtxIoCopyIndirectCalls;
                    public static long RtxIoCompressedBytes => _lastFrameRtxIoCompressedBytes;
                    public static long RtxIoDecompressedBytes => _lastFrameRtxIoDecompressedBytes;
                    public static long RtxIoCopyBytes => _lastFrameRtxIoCopyBytes;
                    public static double RtxIoSubmissionTimeMs => TimeSpan.FromTicks(_lastFrameRtxIoSubmissionTimeTicks).TotalMilliseconds;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameRtxIoDecompressCalls = Interlocked.Exchange(ref _rtxIoDecompressCalls, 0);
                        _lastFrameRtxIoCopyIndirectCalls = Interlocked.Exchange(ref _rtxIoCopyIndirectCalls, 0);
                        _lastFrameRtxIoCompressedBytes = Interlocked.Exchange(ref _rtxIoCompressedBytes, 0);
                        _lastFrameRtxIoDecompressedBytes = Interlocked.Exchange(ref _rtxIoDecompressedBytes, 0);
                        _lastFrameRtxIoCopyBytes = Interlocked.Exchange(ref _rtxIoCopyBytes, 0);
                        _lastFrameRtxIoSubmissionTimeTicks = Interlocked.Exchange(ref _rtxIoSubmissionTimeTicks, 0);
                    }

                    public static void RecordRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _rtxIoDecompressCalls);

                        if (compressedBytes > 0)
                            Interlocked.Add(ref _rtxIoCompressedBytes, compressedBytes);

                        if (decompressedBytes > 0)
                            Interlocked.Add(ref _rtxIoDecompressedBytes, decompressedBytes);

                        if (submissionTime.Ticks > 0)
                            Interlocked.Add(ref _rtxIoSubmissionTimeTicks, submissionTime.Ticks);
                    }

                    public static void RecordRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _rtxIoCopyIndirectCalls);

                        if (copiedBytes > 0)
                            Interlocked.Add(ref _rtxIoCopyBytes, copiedBytes);

                        if (submissionTime.Ticks > 0)
                            Interlocked.Add(ref _rtxIoSubmissionTimeTicks, submissionTime.Ticks);
                    }
                }
            }
        }
    }
}
