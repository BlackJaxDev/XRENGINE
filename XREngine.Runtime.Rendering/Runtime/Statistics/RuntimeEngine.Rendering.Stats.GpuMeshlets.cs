using System;
using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine
{
    public static partial class RuntimeEngine
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
                    private static long _latestGpuMeshletTaskRecordsEmitted;
                    private static long _latestGpuMeshletTaskRecordsFrustumCulled;
                    private static long _latestGpuMeshletTaskRecordsConeCulled;
                    private static long _latestGpuMeshletTaskRecordsHiZCulled;
                    private static long _gpuMeshletExpansionOverflowCount;
                    private static long _gpuMeshletBufferBytesResident;
                    private static long _gpuMeshletLastVisibleMeshletCount;
                    private static long _gpuMeshletLastDispatchedMeshletCount;
                    private static long _gpuMeshletLastTaskRecordOverflowCount;
                    private static long _gpuMeshletLastDispatchTicks;
                    private static long _gpuMeshletLastReadbackBytes;
                    private static int _gpuMeshletCacheHits;
                    private static int _gpuMeshletCacheMisses;
                    private static int _gpuMeshletCacheStale;
                    // Import and cache telemetry is intentionally lifetime-scoped. Import may finish before
                    // a performance capture begins, so resetting this evidence every frame would hide it.
                    private static long _meshletColdImportBuilderCalls;
                    private static long _meshletColdImportBuildTicks;
                    private static long _meshletColdImportAllocatedBytes;
                    private static long _meshletGeneratedLodCount;
                    private static long _meshletCookedPayloadCount;
                    private static long _meshletCookedMeshletCount;
                    private static long _meshletSourceParserCalls;
                    private static long _meshletWarmPayloadHydrations;
                    private static long _meshletRenderPathSourceHashCalls;
                    private static long _meshletRenderPathDiskCalls;
                    private static long _meshletRenderPathCookerCalls;
                    private static long _meshletBufferLiveBytes;
                    private static long _meshletBufferRetiredBytes;
                    private static long _meshletBufferRebuildCount;
                    private static long _meshletBufferRetireCount;
                    private static long _meshletDispatchCallCount;
                    private static long _meshletDispatchGroupCount;
                    private static long _meshletDelayedDispatchGroupCount;
                    private static long _meshletDiagnosticReadbackBytes;
                    private static long _meshletMappedBytes;
                    private static long _meshletResolvedMeshletRows;
                    private static long _meshletResolvedTaskGroups;
                    private static string _meshletRequestedSubmission = string.Empty;
                    private static string _meshletPrimitivePreference = string.Empty;
                    private static string _meshletResolvedPass = string.Empty;
                    private static string _meshletResolvedRoute = string.Empty;
                    private static string _meshletPrimaryRouteReason = string.Empty;
                    private static string _meshletLastPostSealFailurePass = string.Empty;
                    private static string _meshletLastPostSealFailureReason = string.Empty;
                    private static string _meshletEligiblePassPreSealReason = string.Empty;
                    private static string _meshletVulkanCapabilityLadder = string.Empty;
                    private static string _meshletVulkanCapabilityFailedRung = string.Empty;
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
                    private static long _lastFrameGpuMeshletLastVisibleMeshletCount;
                    private static long _lastFrameGpuMeshletLastDispatchedMeshletCount;
                    private static long _lastFrameGpuMeshletLastTaskRecordOverflowCount;
                    private static long _lastFrameGpuMeshletLastDispatchTicks;
                    private static long _lastFrameGpuMeshletLastReadbackBytes;
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
                    /// <summary>Latest delayed GPU task sample containing at least one emitted task record.</summary>
                    public static long LatestGpuMeshletTaskRecordsEmitted => Volatile.Read(ref _latestGpuMeshletTaskRecordsEmitted);
                    public static long LatestGpuMeshletTaskRecordsFrustumCulled => Volatile.Read(ref _latestGpuMeshletTaskRecordsFrustumCulled);
                    public static long LatestGpuMeshletTaskRecordsConeCulled => Volatile.Read(ref _latestGpuMeshletTaskRecordsConeCulled);
                    public static long LatestGpuMeshletTaskRecordsHiZCulled => Volatile.Read(ref _latestGpuMeshletTaskRecordsHiZCulled);
                    public static long GpuMeshletExpansionOverflowCount => _lastFrameGpuMeshletExpansionOverflowCount;
                    public static long GpuMeshletBufferBytesResident => _lastFrameGpuMeshletBufferBytesResident;
                    public static long LastVisibleMeshletCount => _lastFrameGpuMeshletLastVisibleMeshletCount;
                    public static long LastDispatchedMeshletCount => _lastFrameGpuMeshletLastDispatchedMeshletCount;
                    public static long LastTaskRecordOverflowCount => _lastFrameGpuMeshletLastTaskRecordOverflowCount;
                    public static TimeSpan LastDispatchTime => TimeSpan.FromTicks(_lastFrameGpuMeshletLastDispatchTicks);
                    public static long LastReadbackBytes => _lastFrameGpuMeshletLastReadbackBytes;
                    public static int GpuMeshletCacheHits => _lastFrameGpuMeshletCacheHits;
                    public static int GpuMeshletCacheMisses => _lastFrameGpuMeshletCacheMisses;
                    public static int GpuMeshletCacheStale => _lastFrameGpuMeshletCacheStale;
                    public static long ColdImportBuilderCalls => Volatile.Read(ref _meshletColdImportBuilderCalls);
                    public static TimeSpan ColdImportBuildTime => TimeSpan.FromTicks(Volatile.Read(ref _meshletColdImportBuildTicks));
                    public static long ColdImportAllocatedBytes => Volatile.Read(ref _meshletColdImportAllocatedBytes);
                    public static long GeneratedLodCount => Volatile.Read(ref _meshletGeneratedLodCount);
                    public static long CookedPayloadCount => Volatile.Read(ref _meshletCookedPayloadCount);
                    public static long CookedMeshletCount => Volatile.Read(ref _meshletCookedMeshletCount);
                    /// <summary>Number of source-model parser entries observed during this process.</summary>
                    public static long SourceParserCalls => Volatile.Read(ref _meshletSourceParserCalls);
                    public static long WarmPayloadHydrations => Volatile.Read(ref _meshletWarmPayloadHydrations);

                    /// <summary>Captures the monotonic import/cache counters without allocations.</summary>
                    public static MeshletImportTelemetrySnapshot CaptureImportTelemetry()
                        => new(SourceParserCalls, ColdImportBuilderCalls, WarmPayloadHydrations);
                    public static long RenderPathSourceHashCalls => Volatile.Read(ref _meshletRenderPathSourceHashCalls);
                    public static long RenderPathDiskCalls => Volatile.Read(ref _meshletRenderPathDiskCalls);
                    public static long RenderPathCookerCalls => Volatile.Read(ref _meshletRenderPathCookerCalls);
                    public static long BufferLiveBytes => Volatile.Read(ref _meshletBufferLiveBytes);
                    public static long BufferRetiredBytes => Volatile.Read(ref _meshletBufferRetiredBytes);
                    public static long BufferRebuildCount => Volatile.Read(ref _meshletBufferRebuildCount);
                    public static long BufferRetireCount => Volatile.Read(ref _meshletBufferRetireCount);
                    public static long DispatchCallCount => Volatile.Read(ref _meshletDispatchCallCount);
                    public static long DispatchGroupCount => Volatile.Read(ref _meshletDispatchGroupCount);
                    public static long MappedBytes => Volatile.Read(ref _meshletMappedBytes);
                    /// <summary>GPU-written mesh-task indirect X observed only after a diagnostics fence completed.</summary>
                    public static long DelayedDispatchGroupCount => Volatile.Read(ref _meshletDelayedDispatchGroupCount);
                    /// <summary>Bytes copied by delayed diagnostics; excluded from zero-readback production accounting.</summary>
                    public static long DiagnosticReadbackBytes => Volatile.Read(ref _meshletDiagnosticReadbackBytes);
                    public static long ResolvedMeshletRows => Volatile.Read(ref _meshletResolvedMeshletRows);
                    public static long ResolvedTaskGroups => Volatile.Read(ref _meshletResolvedTaskGroups);
                    public static string RequestedSubmission => Volatile.Read(ref _meshletRequestedSubmission);
                    public static string PrimitivePreference => Volatile.Read(ref _meshletPrimitivePreference);
                    public static string ResolvedPass => Volatile.Read(ref _meshletResolvedPass);
                    public static string ResolvedRoute => Volatile.Read(ref _meshletResolvedRoute);
                    public static string PrimaryRouteReason => Volatile.Read(ref _meshletPrimaryRouteReason);
                    public static string LastPostSealFailurePass => Volatile.Read(ref _meshletLastPostSealFailurePass);
                    public static string LastPostSealFailureReason => Volatile.Read(ref _meshletLastPostSealFailureReason);
                    public static string EligiblePassPreSealReason => Volatile.Read(ref _meshletEligiblePassPreSealReason);
                    public static string VulkanCapabilityLadder => Volatile.Read(ref _meshletVulkanCapabilityLadder);
                    public static string VulkanCapabilityFailedRung => Volatile.Read(ref _meshletVulkanCapabilityFailedRung);

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
                        _lastFrameGpuMeshletLastVisibleMeshletCount = Interlocked.Exchange(ref _gpuMeshletLastVisibleMeshletCount, 0);
                        _lastFrameGpuMeshletLastDispatchedMeshletCount = Interlocked.Exchange(ref _gpuMeshletLastDispatchedMeshletCount, 0);
                        _lastFrameGpuMeshletLastTaskRecordOverflowCount = Interlocked.Exchange(ref _gpuMeshletLastTaskRecordOverflowCount, 0);
                        _lastFrameGpuMeshletLastDispatchTicks = Interlocked.Exchange(ref _gpuMeshletLastDispatchTicks, 0);
                        _lastFrameGpuMeshletLastReadbackBytes = Interlocked.Exchange(ref _gpuMeshletLastReadbackBytes, 0);
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

                    public static void RecordGpuMeshletStrategyRequested(
                        int renderPass,
                        EMeshSubmissionStrategy requestedStrategy,
                        EMeshSubmissionStrategy selectedStrategy,
                        EMeshShaderDialect dialect,
                        uint commandCount,
                        uint taskCapacity)
                        => RecordGpuMeshletStrategyRequested();

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
                        {
                            Interlocked.Add(ref _gpuMeshletTaskRecordsEmitted, emitted);
                            // Delayed evidence can land between profiler-frame
                            // snapshots. Keep the latest positive GPU sample
                            // stable for diagnostics without changing the
                            // per-frame counters or scheduling another readback.
                            Interlocked.Exchange(ref _latestGpuMeshletTaskRecordsEmitted, emitted);
                            Interlocked.Exchange(ref _latestGpuMeshletTaskRecordsFrustumCulled, frustumCulled);
                            Interlocked.Exchange(ref _latestGpuMeshletTaskRecordsConeCulled, coneCulled);
                            Interlocked.Exchange(ref _latestGpuMeshletTaskRecordsHiZCulled, hiZCulled);
                            // The GPU stats buffer is the authoritative source
                            // for eligible task rows; never substitute the
                            // scene-wide command count captured at enqueue.
                            Interlocked.Exchange(ref _meshletResolvedMeshletRows, emitted);
                        }
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

                    public static void RecordGpuMeshletInstrumentation(
                        uint visibleMeshletCount,
                        uint dispatchedMeshletCount,
                        uint taskRecordOverflowCount,
                        TimeSpan dispatchTime,
                        uint readbackBytes)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Exchange(ref _gpuMeshletLastVisibleMeshletCount, visibleMeshletCount);
                        Interlocked.Exchange(ref _gpuMeshletLastDispatchedMeshletCount, dispatchedMeshletCount);
                        Interlocked.Exchange(ref _gpuMeshletLastTaskRecordOverflowCount, taskRecordOverflowCount);
                        Interlocked.Exchange(ref _gpuMeshletLastDispatchTicks, dispatchTime.Ticks);
                        if (readbackBytes > 0u)
                            Interlocked.Add(ref _gpuMeshletLastReadbackBytes, readbackBytes);
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

                    /// <summary>Records import-level cooking work. Native builder entries have their own exact counter.</summary>
                    public static void RecordMeshletColdImport(TimeSpan buildTime, long allocatedBytes, long generatedLods, long payloads, long meshlets)
                    {
                        if (!EnableTracking)
                            return;

                        AddPositive(ref _meshletColdImportBuildTicks, buildTime.Ticks);
                        AddPositive(ref _meshletColdImportAllocatedBytes, allocatedBytes);
                        AddPositive(ref _meshletGeneratedLodCount, generatedLods);
                        AddPositive(ref _meshletCookedPayloadCount, payloads);
                        AddPositive(ref _meshletCookedMeshletCount, meshlets);
                    }

                    /// <summary>
                    /// Records entry into a third-party model source parser. This deliberately lives
                    /// at the importer boundary rather than beside meshlet construction so a warm
                    /// cooked-mesh load can prove it did not reopen or parse the source model.
                    /// </summary>
                    public static void RecordMeshletSourceParserEntry()
                    {
                        if (EnableTracking)
                            Interlocked.Increment(ref _meshletSourceParserCalls);
                    }

                    /// <summary>Records the actual entry to meshoptimizer's meshlet builder.</summary>
                    public static void RecordMeshletNativeBuilderEntry()
                    {
                        if (EnableTracking)
                            Interlocked.Increment(ref _meshletColdImportBuilderCalls);
                    }

                    /// <summary>Records loading an already-cooked meshlet payload without invoking the cooker.</summary>
                    public static void RecordMeshletWarmPayloadHydration()
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _meshletWarmPayloadHydrations);
                    }

                    /// <summary>Records prohibited source work observed from the frame-critical render path.</summary>
                    public static void RecordGpuMeshletRenderPathProhibitedWork(long sourceHashCalls = 0, long diskCalls = 0, long cookerCalls = 0)
                    {
                        if (!EnableTracking)
                            return;

                        AddPositive(ref _meshletRenderPathSourceHashCalls, sourceHashCalls);
                        AddPositive(ref _meshletRenderPathDiskCalls, diskCalls);
                        AddPositive(ref _meshletRenderPathCookerCalls, cookerCalls);
                    }

                    public static void RecordGpuMeshletRequestedSubmission(string requestedStrategy, string primitivePreference)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _meshletRequestedSubmission, requestedStrategy ?? string.Empty);
                        Volatile.Write(ref _meshletPrimitivePreference, primitivePreference ?? string.Empty);
                    }

                    public static void RecordGpuMeshletResolvedRoute(string renderPass, bool meshlet, uint rows, uint taskGroups, string primaryReason)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _meshletResolvedPass, renderPass ?? string.Empty);
                        Volatile.Write(ref _meshletResolvedRoute, meshlet ? "Meshlet" : "TraditionalGpu");
                        Volatile.Write(ref _meshletPrimaryRouteReason, primaryReason ?? string.Empty);
                        Interlocked.Exchange(ref _meshletResolvedMeshletRows, meshlet ? rows : 0L);
                        Interlocked.Exchange(ref _meshletResolvedTaskGroups, meshlet ? taskGroups : 0L);
                    }

                    /// <summary>
                    /// Preserves the last unsafe failure discovered after a pass sealed for direct
                    /// meshlet submission. Later planned traditional passes must not overwrite it.
                    /// </summary>
                    public static void RecordGpuMeshletPostSealFailure(int renderPass, string reason)
                    {
                        if (!EnableTracking)
                            return;

                        Volatile.Write(ref _meshletLastPostSealFailurePass, renderPass.ToString());
                        Volatile.Write(ref _meshletLastPostSealFailureReason, reason ?? string.Empty);
                    }

                    public static void RecordGpuMeshletEligiblePassPreSealReason(string reason)
                    {
                        if (EnableTracking)
                            Volatile.Write(ref _meshletEligiblePassPreSealReason, reason ?? string.Empty);
                    }

                    public static void RecordGpuMeshletBufferGeneration(long liveBytes, long retiredBytes, long rebuilds = 0, long retires = 0)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Exchange(ref _meshletBufferLiveBytes, Math.Max(0L, liveBytes));
                        Interlocked.Exchange(ref _meshletBufferRetiredBytes, Math.Max(0L, retiredBytes));
                        AddPositive(ref _meshletBufferRebuildCount, rebuilds);
                        AddPositive(ref _meshletBufferRetireCount, retires);
                    }

                    public static void RecordGpuMeshletDispatch(uint groups, long mappedBytes = 0)
                    {
                        if (!EnableTracking)
                            return;

                        Interlocked.Increment(ref _meshletDispatchCallCount);
                        AddPositive(ref _meshletDispatchGroupCount, groups);
                        AddPositive(ref _meshletMappedBytes, mappedBytes);
                    }

                    /// <summary>Publishes completed, diagnostics-only mesh-task indirect evidence.</summary>
                    public static void RecordGpuMeshletDelayedDiagnostics(uint dispatchGroupX, uint readbackBytes)
                    {
                        if (!EnableTracking)
                            return;

                        if (dispatchGroupX > 0u)
                        {
                            Interlocked.Add(ref _meshletDelayedDispatchGroupCount, dispatchGroupX);
                            Interlocked.Exchange(ref _meshletResolvedTaskGroups, dispatchGroupX);
                        }

                        if (readbackBytes > 0u)
                            Interlocked.Add(ref _meshletDiagnosticReadbackBytes, readbackBytes);
                    }

                    /// <summary>Publishes the Vulkan mesh-shader readiness ladder for capture and MCP diagnostics.</summary>
                    public static void PublishVulkanMeshletCapability(string ladder, string failedRung)
                    {
                        Volatile.Write(ref _meshletVulkanCapabilityLadder, ladder ?? string.Empty);
                        Volatile.Write(ref _meshletVulkanCapabilityFailedRung, failedRung ?? string.Empty);
                    }

                    private static void AddPositive(ref long target, long value)
                    {
                        if (value > 0L)
                            Interlocked.Add(ref target, value);
                    }
                }
            }
        }
    }
}
