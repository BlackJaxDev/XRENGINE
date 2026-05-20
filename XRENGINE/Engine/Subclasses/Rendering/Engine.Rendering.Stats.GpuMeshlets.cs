using System.Threading;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                public static class GpuMeshlets
                {
                    private static int _gpuMeshletRequestedFrames;
                    private static int _gpuMeshletProductionFrames;
                    private static int _gpuMeshletFallbackFrames;
                    private static int _gpuMeshletDispatchSkipped;
                    private static long _gpuMeshletTaskRecordsEmitted;
                    private static long _gpuMeshletTaskRecordsFrustumCulled;
                    private static long _gpuMeshletTaskRecordsConeCulled;
                    private static long _gpuMeshletTaskRecordsHiZCulled;
                    private static long _gpuMeshletExpansionOverflowCount;
                    private static long _gpuMeshletBufferBytesResident;
                    private static int _gpuMeshletCacheHits;
                    private static int _gpuMeshletCacheMisses;
                    private static int _gpuMeshletCacheStale;
                    private static int _lastFrameGpuMeshletRequestedFrames;
                    private static int _lastFrameGpuMeshletProductionFrames;
                    private static int _lastFrameGpuMeshletFallbackFrames;
                    private static int _lastFrameGpuMeshletDispatchSkipped;
                    private static long _lastFrameGpuMeshletTaskRecordsEmitted;
                    private static long _lastFrameGpuMeshletTaskRecordsFrustumCulled;
                    private static long _lastFrameGpuMeshletTaskRecordsConeCulled;
                    private static long _lastFrameGpuMeshletTaskRecordsHiZCulled;
                    private static long _lastFrameGpuMeshletExpansionOverflowCount;
                    private static long _lastFrameGpuMeshletBufferBytesResident;
                    private static int _lastFrameGpuMeshletCacheHits;
                    private static int _lastFrameGpuMeshletCacheMisses;
                    private static int _lastFrameGpuMeshletCacheStale;

                    public static int GpuMeshletRequestedFrames => _lastFrameGpuMeshletRequestedFrames;
                    public static int GpuMeshletProductionFrames => _lastFrameGpuMeshletProductionFrames;
                    public static int GpuMeshletFallbackFrames => _lastFrameGpuMeshletFallbackFrames;
                    public static int GpuMeshletDispatchSkipped => _lastFrameGpuMeshletDispatchSkipped;
                    public static long GpuMeshletTaskRecordsEmitted => _lastFrameGpuMeshletTaskRecordsEmitted;
                    public static long GpuMeshletTaskRecordsFrustumCulled => _lastFrameGpuMeshletTaskRecordsFrustumCulled;
                    public static long GpuMeshletTaskRecordsConeCulled => _lastFrameGpuMeshletTaskRecordsConeCulled;
                    public static long GpuMeshletTaskRecordsHiZCulled => _lastFrameGpuMeshletTaskRecordsHiZCulled;
                    public static long GpuMeshletExpansionOverflowCount => _lastFrameGpuMeshletExpansionOverflowCount;
                    public static long GpuMeshletBufferBytesResident => _lastFrameGpuMeshletBufferBytesResident;
                    public static int GpuMeshletCacheHits => _lastFrameGpuMeshletCacheHits;
                    public static int GpuMeshletCacheMisses => _lastFrameGpuMeshletCacheMisses;
                    public static int GpuMeshletCacheStale => _lastFrameGpuMeshletCacheStale;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameGpuMeshletRequestedFrames = Interlocked.Exchange(ref _gpuMeshletRequestedFrames, 0);
                        _lastFrameGpuMeshletProductionFrames = Interlocked.Exchange(ref _gpuMeshletProductionFrames, 0);
                        _lastFrameGpuMeshletFallbackFrames = Interlocked.Exchange(ref _gpuMeshletFallbackFrames, 0);
                        _lastFrameGpuMeshletDispatchSkipped = Interlocked.Exchange(ref _gpuMeshletDispatchSkipped, 0);
                        _lastFrameGpuMeshletTaskRecordsEmitted = Interlocked.Exchange(ref _gpuMeshletTaskRecordsEmitted, 0);
                        _lastFrameGpuMeshletTaskRecordsFrustumCulled = Interlocked.Exchange(ref _gpuMeshletTaskRecordsFrustumCulled, 0);
                        _lastFrameGpuMeshletTaskRecordsConeCulled = Interlocked.Exchange(ref _gpuMeshletTaskRecordsConeCulled, 0);
                        _lastFrameGpuMeshletTaskRecordsHiZCulled = Interlocked.Exchange(ref _gpuMeshletTaskRecordsHiZCulled, 0);
                        _lastFrameGpuMeshletExpansionOverflowCount = Interlocked.Exchange(ref _gpuMeshletExpansionOverflowCount, 0);
                        _lastFrameGpuMeshletBufferBytesResident = Interlocked.Exchange(ref _gpuMeshletBufferBytesResident, 0);
                        _lastFrameGpuMeshletCacheHits = Interlocked.Exchange(ref _gpuMeshletCacheHits, 0);
                        _lastFrameGpuMeshletCacheMisses = Interlocked.Exchange(ref _gpuMeshletCacheMisses, 0);
                        _lastFrameGpuMeshletCacheStale = Interlocked.Exchange(ref _gpuMeshletCacheStale, 0);
                    }

                    public static void RecordGpuMeshletStrategyRequested(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletRequestedFrames, eventCount);
                    }

                    public static void RecordGpuMeshletProductionFrame(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletProductionFrames, eventCount);
                    }

                    public static void RecordGpuMeshletFallback(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletFallbackFrames, eventCount);
                    }

                    public static void RecordGpuMeshletDispatchSkipped(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletDispatchSkipped, eventCount);
                    }

                    public static void RecordGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
                    {
                        if (!EnableTracking)
                            return;

                        if (emitted > 0u)
                            Interlocked.Add(ref _gpuMeshletTaskRecordsEmitted, emitted);
                        if (frustumCulled > 0u)
                            Interlocked.Add(ref _gpuMeshletTaskRecordsFrustumCulled, frustumCulled);
                        if (coneCulled > 0u)
                            Interlocked.Add(ref _gpuMeshletTaskRecordsConeCulled, coneCulled);
                        if (hiZCulled > 0u)
                            Interlocked.Add(ref _gpuMeshletTaskRecordsHiZCulled, hiZCulled);
                    }

                    public static void RecordGpuMeshletExpansionOverflow(uint overflowCount)
                    {
                        if (!EnableTracking || overflowCount == 0u)
                            return;

                        Interlocked.Add(ref _gpuMeshletExpansionOverflowCount, overflowCount);
                    }

                    public static void RecordGpuMeshletBufferBytesResident(ulong bytes)
                    {
                        if (!EnableTracking)
                            return;

                        long saturated = bytes > long.MaxValue ? long.MaxValue : (long)bytes;
                        long snapshot;
                        do
                        {
                            snapshot = Volatile.Read(ref _gpuMeshletBufferBytesResident);
                            if (saturated <= snapshot)
                                return;
                        } while (Interlocked.CompareExchange(ref _gpuMeshletBufferBytesResident, saturated, snapshot) != snapshot);
                    }

                    public static void RecordGpuMeshletCacheHit(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletCacheHits, eventCount);
                    }

                    public static void RecordGpuMeshletCacheMiss(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletCacheMisses, eventCount);
                    }

                    public static void RecordGpuMeshletCacheStale(int eventCount = 1)
                    {
                        if (!EnableTracking || eventCount <= 0)
                            return;

                        Interlocked.Add(ref _gpuMeshletCacheStale, eventCount);
                    }
                }
            }
        }
    }
}
