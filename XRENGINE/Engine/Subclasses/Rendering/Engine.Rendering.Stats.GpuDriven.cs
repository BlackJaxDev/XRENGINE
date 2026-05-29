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
                public static class GpuDriven
                {
                    private static long _culledCommandCount;
                    private static int _activeBucketCount;
                    private static int _emptyBucketSkips;
                    private static int _fullBucketScans;
                    private static int _materialScatterDispatches;
                    private static long _indirectCommandGenerationTicks;
                    private static long _gpuCullTicks;
                    private static long _gpuSortCompactTicks;
                    private static long _delayedDrawCountBufferValue;
                    private static long _delayedDiagnosticReadbackBytes;
                    private static int _delayedDiagnosticReadbackCount;
                    private static long _gpuCompactionOverflow;
                    private static long _activeListOverflow;
                    private static long _bucketOverflow;
                    private static long _meshletOverflow;
                    private static int _hiZOnePhaseFrames;
                    private static int _hiZTwoPhaseFrames;
                    private static long _hiZPhaseOneDraws;
                    private static long _hiZPhaseTwoDraws;
                    private static int _visibilityPassDraws;
                    private static long _visibilityClassifiedPixels;
                    private static int _visibilityActiveMaterialTiles;
                    private static int _visibilityClassificationOverflow;
                    private static long _visibilityReconstructionTicks;
                    private static long _visibilityMaterialShadingTicks;

                    private static long _lastFrameCulledCommandCount;
                    private static int _lastFrameActiveBucketCount;
                    private static int _lastFrameEmptyBucketSkips;
                    private static int _lastFrameFullBucketScans;
                    private static int _lastFrameMaterialScatterDispatches;
                    private static long _lastFrameIndirectCommandGenerationTicks;
                    private static long _lastFrameGpuCullTicks;
                    private static long _lastFrameGpuSortCompactTicks;
                    private static long _lastFrameDelayedDrawCountBufferValue;
                    private static long _lastFrameDelayedDiagnosticReadbackBytes;
                    private static int _lastFrameDelayedDiagnosticReadbackCount;
                    private static long _lastFrameGpuCompactionOverflow;
                    private static long _lastFrameActiveListOverflow;
                    private static long _lastFrameBucketOverflow;
                    private static long _lastFrameMeshletOverflow;
                    private static int _lastFrameHiZOnePhaseFrames;
                    private static int _lastFrameHiZTwoPhaseFrames;
                    private static long _lastFrameHiZPhaseOneDraws;
                    private static long _lastFrameHiZPhaseTwoDraws;
                    private static int _lastFrameVisibilityPassDraws;
                    private static long _lastFrameVisibilityClassifiedPixels;
                    private static int _lastFrameVisibilityActiveMaterialTiles;
                    private static int _lastFrameVisibilityClassificationOverflow;
                    private static long _lastFrameVisibilityReconstructionTicks;
                    private static long _lastFrameVisibilityMaterialShadingTicks;

                    private static readonly object _modeLock = new();
                    private static string _hiZMode = "unknown";
                    private static string _lastFrameHiZMode = "unknown";

                    public static long CulledCommandCount => _lastFrameCulledCommandCount;
                    public static int ActiveBucketCount => _lastFrameActiveBucketCount;
                    public static int EmptyBucketSkips => _lastFrameEmptyBucketSkips;
                    public static int FullBucketScans => _lastFrameFullBucketScans;
                    public static int MaterialScatterDispatches => _lastFrameMaterialScatterDispatches;
                    public static double IndirectCommandGenerationMs => TimeSpan.FromTicks(_lastFrameIndirectCommandGenerationTicks).TotalMilliseconds;
                    public static double GpuCullMs => TimeSpan.FromTicks(_lastFrameGpuCullTicks).TotalMilliseconds;
                    public static double GpuSortCompactMs => TimeSpan.FromTicks(_lastFrameGpuSortCompactTicks).TotalMilliseconds;
                    public static long DelayedDrawCountBufferValue => _lastFrameDelayedDrawCountBufferValue;
                    public static long DelayedDiagnosticReadbackBytes => _lastFrameDelayedDiagnosticReadbackBytes;
                    public static int DelayedDiagnosticReadbackCount => _lastFrameDelayedDiagnosticReadbackCount;
                    public static long GpuCompactionOverflow => _lastFrameGpuCompactionOverflow;
                    public static long ActiveListOverflow => _lastFrameActiveListOverflow;
                    public static long BucketOverflow => _lastFrameBucketOverflow;
                    public static long MeshletOverflow => _lastFrameMeshletOverflow;
                    public static string HiZMode => _lastFrameHiZMode;
                    public static int HiZOnePhaseFrames => _lastFrameHiZOnePhaseFrames;
                    public static int HiZTwoPhaseFrames => _lastFrameHiZTwoPhaseFrames;
                    public static long HiZPhaseOneDraws => _lastFrameHiZPhaseOneDraws;
                    public static long HiZPhaseTwoDraws => _lastFrameHiZPhaseTwoDraws;
                    public static int VisibilityPassDraws => _lastFrameVisibilityPassDraws;
                    public static long VisibilityClassifiedPixels => _lastFrameVisibilityClassifiedPixels;
                    public static int VisibilityActiveMaterialTiles => _lastFrameVisibilityActiveMaterialTiles;
                    public static int VisibilityClassificationOverflow => _lastFrameVisibilityClassificationOverflow;
                    public static double VisibilityReconstructionMs => TimeSpan.FromTicks(_lastFrameVisibilityReconstructionTicks).TotalMilliseconds;
                    public static double VisibilityMaterialShadingMs => TimeSpan.FromTicks(_lastFrameVisibilityMaterialShadingTicks).TotalMilliseconds;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameCulledCommandCount = Interlocked.Exchange(ref _culledCommandCount, 0);
                        _lastFrameActiveBucketCount = Interlocked.Exchange(ref _activeBucketCount, 0);
                        _lastFrameEmptyBucketSkips = Interlocked.Exchange(ref _emptyBucketSkips, 0);
                        _lastFrameFullBucketScans = Interlocked.Exchange(ref _fullBucketScans, 0);
                        _lastFrameMaterialScatterDispatches = Interlocked.Exchange(ref _materialScatterDispatches, 0);
                        _lastFrameIndirectCommandGenerationTicks = Interlocked.Exchange(ref _indirectCommandGenerationTicks, 0);
                        _lastFrameGpuCullTicks = Interlocked.Exchange(ref _gpuCullTicks, 0);
                        _lastFrameGpuSortCompactTicks = Interlocked.Exchange(ref _gpuSortCompactTicks, 0);
                        _lastFrameDelayedDrawCountBufferValue = Interlocked.Exchange(ref _delayedDrawCountBufferValue, 0);
                        _lastFrameDelayedDiagnosticReadbackBytes = Interlocked.Exchange(ref _delayedDiagnosticReadbackBytes, 0);
                        _lastFrameDelayedDiagnosticReadbackCount = Interlocked.Exchange(ref _delayedDiagnosticReadbackCount, 0);
                        _lastFrameGpuCompactionOverflow = Interlocked.Exchange(ref _gpuCompactionOverflow, 0);
                        _lastFrameActiveListOverflow = Interlocked.Exchange(ref _activeListOverflow, 0);
                        _lastFrameBucketOverflow = Interlocked.Exchange(ref _bucketOverflow, 0);
                        _lastFrameMeshletOverflow = Interlocked.Exchange(ref _meshletOverflow, 0);
                        _lastFrameHiZOnePhaseFrames = Interlocked.Exchange(ref _hiZOnePhaseFrames, 0);
                        _lastFrameHiZTwoPhaseFrames = Interlocked.Exchange(ref _hiZTwoPhaseFrames, 0);
                        _lastFrameHiZPhaseOneDraws = Interlocked.Exchange(ref _hiZPhaseOneDraws, 0);
                        _lastFrameHiZPhaseTwoDraws = Interlocked.Exchange(ref _hiZPhaseTwoDraws, 0);
                        _lastFrameVisibilityPassDraws = Interlocked.Exchange(ref _visibilityPassDraws, 0);
                        _lastFrameVisibilityClassifiedPixels = Interlocked.Exchange(ref _visibilityClassifiedPixels, 0);
                        _lastFrameVisibilityActiveMaterialTiles = Interlocked.Exchange(ref _visibilityActiveMaterialTiles, 0);
                        _lastFrameVisibilityClassificationOverflow = Interlocked.Exchange(ref _visibilityClassificationOverflow, 0);
                        _lastFrameVisibilityReconstructionTicks = Interlocked.Exchange(ref _visibilityReconstructionTicks, 0);
                        _lastFrameVisibilityMaterialShadingTicks = Interlocked.Exchange(ref _visibilityMaterialShadingTicks, 0);

                        lock (_modeLock)
                            _lastFrameHiZMode = _hiZMode;
                    }

                    public static void UpdateHiZMode(string? mode)
                    {
                        lock (_modeLock)
                            _hiZMode = string.IsNullOrWhiteSpace(mode) ? "unknown" : mode!;
                    }

                    public static void RecordBucketWork(int activeBuckets = 0, int emptyBucketSkips = 0, int fullBucketScans = 0, int materialScatterDispatches = 0)
                    {
                        if (!EnableTracking)
                            return;

                        if (activeBuckets > 0)
                            Interlocked.Add(ref _activeBucketCount, activeBuckets);
                        if (emptyBucketSkips > 0)
                            Interlocked.Add(ref _emptyBucketSkips, emptyBucketSkips);
                        if (fullBucketScans > 0)
                            Interlocked.Add(ref _fullBucketScans, fullBucketScans);
                        if (materialScatterDispatches > 0)
                            Interlocked.Add(ref _materialScatterDispatches, materialScatterDispatches);
                    }

                    public static void RecordCommandCompaction(long culledCommands, long delayedDrawCountValue = 0, long gpuCompactionOverflow = 0, long activeListOverflow = 0, long bucketOverflow = 0, long meshletOverflow = 0)
                    {
                        if (!EnableTracking)
                            return;

                        if (culledCommands > 0)
                            Interlocked.Add(ref _culledCommandCount, culledCommands);
                        if (delayedDrawCountValue > 0)
                            Interlocked.Exchange(ref _delayedDrawCountBufferValue, delayedDrawCountValue);
                        if (gpuCompactionOverflow > 0)
                            Interlocked.Add(ref _gpuCompactionOverflow, gpuCompactionOverflow);
                        if (activeListOverflow > 0)
                            Interlocked.Add(ref _activeListOverflow, activeListOverflow);
                        if (bucketOverflow > 0)
                            Interlocked.Add(ref _bucketOverflow, bucketOverflow);
                        if (meshletOverflow > 0)
                            Interlocked.Add(ref _meshletOverflow, meshletOverflow);
                    }

                    public static void RecordGpuDrivenStageTiming(TimeSpan indirectGeneration, TimeSpan gpuCull, TimeSpan sortCompact)
                    {
                        if (!EnableTracking)
                            return;

                        if (indirectGeneration.Ticks > 0)
                            Interlocked.Add(ref _indirectCommandGenerationTicks, indirectGeneration.Ticks);
                        if (gpuCull.Ticks > 0)
                            Interlocked.Add(ref _gpuCullTicks, gpuCull.Ticks);
                        if (sortCompact.Ticks > 0)
                            Interlocked.Add(ref _gpuSortCompactTicks, sortCompact.Ticks);
                    }

                    public static void RecordDelayedDiagnosticReadback(long bytes)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _delayedDiagnosticReadbackCount);
                        if (bytes > 0)
                            Interlocked.Add(ref _delayedDiagnosticReadbackBytes, bytes);
                    }

                    public static void RecordHiZPhase(bool twoPhase, long phaseOneDraws, long phaseTwoDraws)
                    {
                        if (!EnableTracking)
                            return;

                        if (twoPhase)
                            Interlocked.Increment(ref _hiZTwoPhaseFrames);
                        else
                            Interlocked.Increment(ref _hiZOnePhaseFrames);
                        if (phaseOneDraws > 0)
                            Interlocked.Add(ref _hiZPhaseOneDraws, phaseOneDraws);
                        if (phaseTwoDraws > 0)
                            Interlocked.Add(ref _hiZPhaseTwoDraws, phaseTwoDraws);
                    }

                    public static void RecordVisibilityBuffer(int passDraws, long classifiedPixels, int activeMaterialTiles, int classificationOverflow, TimeSpan reconstruction, TimeSpan materialShading)
                    {
                        if (!EnableTracking)
                            return;

                        if (passDraws > 0)
                            Interlocked.Add(ref _visibilityPassDraws, passDraws);
                        if (classifiedPixels > 0)
                            Interlocked.Add(ref _visibilityClassifiedPixels, classifiedPixels);
                        if (activeMaterialTiles > 0)
                            Interlocked.Add(ref _visibilityActiveMaterialTiles, activeMaterialTiles);
                        if (classificationOverflow > 0)
                            Interlocked.Add(ref _visibilityClassificationOverflow, classificationOverflow);
                        if (reconstruction.Ticks > 0)
                            Interlocked.Add(ref _visibilityReconstructionTicks, reconstruction.Ticks);
                        if (materialShading.Ticks > 0)
                            Interlocked.Add(ref _visibilityMaterialShadingTicks, materialShading.Ticks);
                    }
                }
            }
        }
    }
}
