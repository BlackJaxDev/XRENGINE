using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Long-lived, renderer-composed owner for Vulkan CPU telemetry. Frame-specific lifecycle state
/// belongs exclusively to the <see cref="VulkanFrameTrace"/> returned by <see cref="BeginFrame"/>.
/// </summary>
internal sealed class VulkanFrameTelemetry
{
    internal const uint GpuProfilerMaxScopesPerFrame = 512;
    internal const uint GpuProfilerQueryCount = GpuProfilerMaxScopesPerFrame * 2;
    internal const string GpuProfilerBackendName = "Vulkan";
    internal const string GpuProfilerQuarantinedMessage =
        "Vulkan GPU pipeline command timing is disabled; set XRE_GPU_TIMESTAMP_DENSE=1 for dense diagnostic command timestamps. Coarse Vulkan command-buffer GPU timing remains available.";
    internal static bool IsGpuProfilerCommandBufferInstrumentationEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.GpuTimestampDense);
    internal static string GpuProfilerCommandTimingStatusMessage
        => IsGpuProfilerCommandBufferInstrumentationEnabled
            ? "Vulkan GPU timings are collected from recorded command buffers."
            : GpuProfilerQuarantinedMessage;

    internal ConcurrentDictionary<string, string> ComputeDispatchOperationNames { get; } =
        new(StringComparer.Ordinal);
    private const int PublicationCapacity = 64;
    private const int VulkanCrashBreadcrumbCapacity = 64;
    private const int VulkanDeviceAddressRangeCapacity = 512;
    private const int VulkanDeviceAddressBindingEventCapacity = 128;
    private const int VulkanNvCheckpointMarkerCapacity = 256;
    private const int VulkanCommandDiagnosticMarkerCapacity = 512;
    private const int VulkanImageLayoutTransitionCapacity = 128;
    private static long s_nextAuthorityId;

    internal readonly VulkanDiagnosticOptions _diagnosticOptions = VulkanDiagnosticOptions.Resolve();
    internal QueryPool[]? _frameTimingQueryPools;
    internal bool[]? _frameTimingQueryReady;
    internal bool _frameTimingGpuEnabled;
    internal double _frameTimingTimestampPeriodNanoseconds = 1.0;
    internal QueryPool[]? _vulkanGpuProfilerQueryPools;
    internal bool[]? _vulkanGpuProfilerQueryReady;
    internal List<VulkanGpuProfilerPendingScope>[]? _vulkanGpuProfilerPendingScopes;
    internal int[]? _vulkanGpuProfilerPendingQueryCounts;
    internal ulong[]? _vulkanGpuProfilerSubmittedFrameIds;
    internal bool _vulkanGpuProfilerEnabled;
    internal bool _vulkanGpuProfilerRecordingActive;
    internal bool _vulkanGpuProfilerBudgetWarningIssued;
    internal int _vulkanGpuProfilerRecordingFrameSlot = -1;
    internal uint _vulkanGpuProfilerNextQuery;
    internal readonly Dictionary<string, ulong> _gpuRenderStatsTraceHashes = [];
    internal readonly VulkanFinalPresentationLedgerState _finalPresentationLedger =
        new(XREnvironment.IsEnabled(XREngineEnvironmentVariables.VulkanFinalPresentationLedger));
    internal readonly object _deviceLostTransitionLock = new();
    internal readonly object _vulkanSubmissionDiagnosticsLock = new();
    internal readonly VulkanCrashBreadcrumb[] _vulkanCrashBreadcrumbs =
        new VulkanCrashBreadcrumb[VulkanCrashBreadcrumbCapacity];
    internal long _vulkanSubmissionSerial;
    internal long _vulkanCrashBreadcrumbSerial;
    internal long _vulkanCommandDiagnosticMarkerSerial;
    internal readonly VulkanCommandDiagnosticMarker[] _vulkanCommandDiagnosticMarkers =
        new VulkanCommandDiagnosticMarker[VulkanCommandDiagnosticMarkerCapacity];
    internal long _vulkanImageLayoutTransitionSerial;
    internal readonly VulkanImageLayoutTransitionBreadcrumb[] _vulkanImageLayoutTransitions =
        new VulkanImageLayoutTransitionBreadcrumb[VulkanImageLayoutTransitionCapacity];
    internal long _vulkanDescriptorTableGeneration;

    /// <summary>
    /// Publishes the resource-owned descriptor generation at the frame boundary.
    /// </summary>
    internal void PublishDescriptorTableGeneration(ulong generation)
        => Volatile.Write(
            ref _vulkanDescriptorTableGeneration,
            unchecked((long)generation));
    internal string? _firstFailingVulkanApi;
    internal VulkanDeviceLossRecord? _firstDeviceLossRecord;
    internal readonly object _vulkanDeviceAddressDiagnosticsLock = new();
    internal readonly VulkanDeviceAddressRange[] _vulkanDeviceAddressRanges =
        new VulkanDeviceAddressRange[VulkanDeviceAddressRangeCapacity];

    internal void UnregisterDeviceAddressRange(Silk.NET.Vulkan.Buffer buffer)
    {
        if (buffer.Handle == 0)
            return;

        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            for (int index = 0;
                 index < _vulkanDeviceAddressRanges.Length;
                 index++)
            {
                VulkanDeviceAddressRange existing =
                    _vulkanDeviceAddressRanges[index];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                {
                    _vulkanDeviceAddressRanges[index] =
                        existing with { Active = false };
                }
            }
        }
    }

    internal void RegisterDeviceAddressRange(Silk.NET.Vulkan.Buffer buffer, ulong baseAddress, ulong size, string label)
    {
        if (buffer.Handle == 0 || baseAddress == 0 || size == 0)
            return;

        lock (_vulkanDeviceAddressDiagnosticsLock)
        {
            int firstInactive = -1;
            for (int index = 0; index < _vulkanDeviceAddressRanges.Length; index++)
            {
                VulkanDeviceAddressRange existing = _vulkanDeviceAddressRanges[index];
                if (existing.Active && existing.Buffer.Handle == buffer.Handle)
                {
                    _vulkanDeviceAddressRanges[index] = new(buffer, baseAddress, size, label, Active: true);
                    return;
                }

                if (!existing.Active && firstInactive < 0)
                    firstInactive = index;
            }

            int replacement = firstInactive >= 0 ? firstInactive : unchecked((int)(buffer.Handle % (ulong)_vulkanDeviceAddressRanges.Length));
            _vulkanDeviceAddressRanges[replacement] = new(buffer, baseAddress, size, label, Active: true);
        }
    }
    internal readonly VulkanDeviceAddressBindingEvent[] _vulkanDeviceAddressBindingEvents =
        new VulkanDeviceAddressBindingEvent[VulkanDeviceAddressBindingEventCapacity];
    internal long _vulkanDeviceAddressBindingEventSerial;
    internal readonly object _vulkanNvCheckpointMarkerLock = new();
    internal readonly VulkanNvCheckpointMarker[] _vulkanNvCheckpointMarkers =
        new VulkanNvCheckpointMarker[VulkanNvCheckpointMarkerCapacity];

    // CPU scopes are authority-lifetime aggregates because many call sites do not yet carry a
    // frame root. They are intentionally absent from per-frame publications. Settlement merely
    // flushes completed aggregate chunks; it never attributes them to the settling frame.
    private readonly long[] _cpuStageTicks = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStageAllocatedBytes = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStageBoundaryAllocatedBytes = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStageInvocationCounts = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStagePeakTicks = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStageAllocationHighWaterBytes = new long[(int)EVulkanCpuStage.Count];
    private readonly long[] _cpuStageBoundaryAllocationHighWaterBytes = new long[(int)EVulkanCpuStage.Count];
    private readonly VulkanFrameTelemetryPublication[] _publications =
        new VulkanFrameTelemetryPublication[PublicationCapacity];
    private readonly long[] _publishedSequences = new long[PublicationCapacity];
    private readonly long[] _publicationVersions = new long[PublicationCapacity];
    private readonly int[] _publicationWriterGates = new int[PublicationCapacity];
    private readonly long _authorityId = Interlocked.Increment(ref s_nextAuthorityId);
    private long _nextPublicationSequence;

    public VulkanFrameTrace BeginFrame(in DesktopFrameIdentity identity) => new(this, identity);

    public VulkanFrameTrace BeginFrame(VulkanFrameRootIdentity identity) => new(this, identity);

    internal void MarkFrameTimingSubmitted(int frameSlot)
    {
        if (_frameTimingQueryReady is not null &&
            (uint)frameSlot < (uint)_frameTimingQueryReady.Length)
        {
            _frameTimingQueryReady[frameSlot] = true;
        }

        if (_vulkanGpuProfilerQueryReady is null ||
            _vulkanGpuProfilerPendingScopes is null ||
            _vulkanGpuProfilerSubmittedFrameIds is null ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerQueryReady.Length ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerPendingScopes.Length)
        {
            return;
        }

        _vulkanGpuProfilerSubmittedFrameIds[frameSlot] =
            RuntimeEngine.Rendering.State.RenderFrameId;
        _vulkanGpuProfilerQueryReady[frameSlot] =
            _vulkanGpuProfilerPendingScopes[frameSlot].Count > 0;
    }

    internal unsafe VulkanCompletedTimingQueryPools SampleFrameTimingQueries(
        Vk api,
        Device device,
        int frameSlot)
    {
        QueryPool completedGpuProfiler = SampleGpuProfilerQueries(api, device, frameSlot);

        if (!_frameTimingGpuEnabled ||
            _frameTimingQueryPools is null ||
            _frameTimingQueryReady is null ||
            (uint)frameSlot >= (uint)_frameTimingQueryPools.Length ||
            !_frameTimingQueryReady[frameSlot])
        {
            return new(default, completedGpuProfiler);
        }

        QueryPool queryPool = _frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return new(default, completedGpuProfiler);

        const uint queryCount = 2;
        ulong* timestamps = stackalloc ulong[(int)queryCount];
        Result result = api.GetQueryPoolResults(
            device,
            queryPool,
            0,
            queryCount,
            (nuint)(sizeof(ulong) * queryCount),
            timestamps,
            (ulong)sizeof(ulong),
            QueryResultFlags.Result64Bit);
        if (result != Result.Success)
            return new(default, completedGpuProfiler);

        ulong start = timestamps[0];
        ulong end = timestamps[1];
        if (end < start)
            return new(queryPool, completedGpuProfiler);

        double gpuMilliseconds =
            (end - start) * _frameTimingTimestampPeriodNanoseconds /
            1_000_000.0;
        RuntimeEngine.Rendering.Stats.Vulkan
            .RecordVulkanFrameGpuCommandBufferTime(
                TimeSpan.FromMilliseconds(gpuMilliseconds));
        return new(queryPool, completedGpuProfiler);
    }

    private unsafe QueryPool SampleGpuProfilerQueries(
        Vk api,
        Device device,
        int frameSlot)
    {
        if (!_vulkanGpuProfilerEnabled ||
            _vulkanGpuProfilerQueryPools is null ||
            _vulkanGpuProfilerQueryReady is null ||
            _vulkanGpuProfilerPendingScopes is null ||
            _vulkanGpuProfilerPendingQueryCounts is null ||
            _vulkanGpuProfilerSubmittedFrameIds is null ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerQueryPools.Length ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerQueryReady.Length ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerPendingScopes.Length ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerPendingQueryCounts.Length ||
            (uint)frameSlot >= (uint)_vulkanGpuProfilerSubmittedFrameIds.Length ||
            !_vulkanGpuProfilerQueryReady[frameSlot])
        {
            return default;
        }

        QueryPool queryPool = _vulkanGpuProfilerQueryPools[frameSlot];
        int queryCount = _vulkanGpuProfilerPendingQueryCounts[frameSlot];
        List<VulkanGpuProfilerPendingScope> samples =
            _vulkanGpuProfilerPendingScopes[frameSlot];
        ulong frameId = _vulkanGpuProfilerSubmittedFrameIds[frameSlot];
        if (queryPool.Handle == 0 ||
            queryCount <= 0 ||
            samples.Count == 0 ||
            frameId == 0)
        {
            return default;
        }

        ulong[] rented = ArrayPool<ulong>.Shared.Rent(queryCount);
        try
        {
            fixed (ulong* timestamps = rented)
            {
                Result result = api.GetQueryPoolResults(
                    device,
                    queryPool,
                    0,
                    (uint)queryCount,
                    (nuint)(sizeof(ulong) * queryCount),
                    timestamps,
                    (ulong)sizeof(ulong),
                    QueryResultFlags.Result64Bit);
                if (result != Result.Success)
                    return default;

                for (int index = 0; index < samples.Count; index++)
                {
                    VulkanGpuProfilerPendingScope sample = samples[index];
                    if (sample.EndQuery >= queryCount ||
                        sample.StartQuery >= queryCount)
                    {
                        continue;
                    }

                    ulong start = timestamps[sample.StartQuery];
                    ulong end = timestamps[sample.EndQuery];
                    if (end <= start)
                        continue;

                    ulong nanoseconds = (ulong)Math.Round(
                        (end - start) *
                        _frameTimingTimestampPeriodNanoseconds);
                    RenderPipelineGpuProfiler.Instance
                        .RecordBackendGpuTimingSample(
                            frameId,
                            "Vulkan",
                            sample.Path,
                            nanoseconds);
                }

                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(
                    ERendererProfilerCounter.TimestampQueryReadbackBytes,
                    queryCount * sizeof(ulong));
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
            samples.Clear();
            _vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
            _vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0;
            _vulkanGpuProfilerQueryReady[frameSlot] = false;
        }

        return queryPool;
    }

    public void RecordCpuStage(EVulkanCpuStage stage, TimeSpan elapsed, long allocatedBytes, long boundaryAllocatedBytes)
    {
        int index = (int)stage;
        if ((uint)index >= (uint)_cpuStageTicks.Length)
            return;

        Interlocked.Add(ref _cpuStageTicks[index], elapsed.Ticks);
        Interlocked.Add(ref _cpuStageAllocatedBytes[index], allocatedBytes);
        Interlocked.Add(ref _cpuStageBoundaryAllocatedBytes[index], boundaryAllocatedBytes);
        Interlocked.Increment(ref _cpuStageInvocationCounts[index]);
        UpdateHighWater(ref _cpuStagePeakTicks[index], elapsed.Ticks);
        UpdateHighWater(ref _cpuStageAllocationHighWaterBytes[index], allocatedBytes);
        UpdateHighWater(ref _cpuStageBoundaryAllocationHighWaterBytes[index], boundaryAllocatedBytes);
    }

    internal void PublishAfterFrame(
        in VulkanFrameTrace trace,
        TimeSpan totalFrameTime,
        EVulkanFrameOutcome outcome)
    {
        long sequence = Interlocked.Increment(ref _nextPublicationSequence);
        int slot = (int)((ulong)sequence % PublicationCapacity);
        VulkanFrameTelemetryPublication publication = trace.CreatePublication(_authorityId, sequence, totalFrameTime, outcome);
        SpinWait spinner = default;
        while (Interlocked.CompareExchange(ref _publicationWriterGates[slot], 1, 0) != 0)
            spinner.SpinOnce();

        try
        {
            // Sequence assignment can race ahead of publication. Never let a delayed wrapped-slot
            // writer replace a newer root that already claimed the same ring slot.
            if (Volatile.Read(ref _publishedSequences[slot]) > sequence)
                return;

            long writingVersion = Volatile.Read(ref _publicationVersions[slot]) + 1;
            if ((writingVersion & 1) == 0)
                writingVersion++;
            Volatile.Write(ref _publicationVersions[slot], writingVersion);
            Volatile.Write(ref _publishedSequences[slot], 0);
            _publications[slot] = publication;
            Volatile.Write(ref _publishedSequences[slot], sequence);
            Volatile.Write(ref _publicationVersions[slot], writingVersion + 1);
        }
        finally
        {
            Volatile.Write(ref _publicationWriterGates[slot], 0);
        }

        Span<VulkanCpuStageTelemetry> cpuStages = stackalloc VulkanCpuStageTelemetry[(int)EVulkanCpuStage.Count];
        SnapshotAuthorityCpuAggregates(cpuStages);
        RuntimeRenderingHostServices.Statistics.PublishRenderVulkanFrameTelemetry(publication, cpuStages);
    }

    /// <summary>Reads the latest complete per-root publication without allocating or taking a lock.</summary>
    public bool TryGetLatestPublication(out VulkanFrameTelemetryPublication publication)
    {
        long sequence = Volatile.Read(ref _nextPublicationSequence);
        if (sequence <= 0)
        {
            publication = default;
            return false;
        }

        int slot = (int)((ulong)sequence % PublicationCapacity);
        long version = Volatile.Read(ref _publicationVersions[slot]);
        if ((version & 1) != 0)
        {
            publication = default;
            return false;
        }

        if (Volatile.Read(ref _publishedSequences[slot]) != sequence)
        {
            publication = default;
            return false;
        }

        publication = _publications[slot];
        long verifiedVersion = Volatile.Read(ref _publicationVersions[slot]);
        return verifiedVersion == version &&
               (verifiedVersion & 1) == 0 &&
               Volatile.Read(ref _publishedSequences[slot]) == sequence;
    }

    private void SnapshotAuthorityCpuAggregates(Span<VulkanCpuStageTelemetry> destination)
    {
        for (int index = 0; index < (int)EVulkanCpuStage.Count; index++)
        {
            long ticks = Interlocked.Exchange(ref _cpuStageTicks[index], 0);
            long allocatedBytes = Interlocked.Exchange(ref _cpuStageAllocatedBytes[index], 0);
            long boundaryAllocatedBytes = Interlocked.Exchange(ref _cpuStageBoundaryAllocatedBytes[index], 0);
            long invocationCount = Interlocked.Exchange(ref _cpuStageInvocationCounts[index], 0);
            long peakTicks = Interlocked.Exchange(ref _cpuStagePeakTicks[index], 0);
            long allocationHighWaterBytes = Interlocked.Exchange(ref _cpuStageAllocationHighWaterBytes[index], 0);
            long boundaryAllocationHighWaterBytes = Interlocked.Exchange(ref _cpuStageBoundaryAllocationHighWaterBytes[index], 0);
            destination[index] = new VulkanCpuStageTelemetry(
                (EVulkanCpuStage)index,
                TimeSpan.FromTicks(ticks),
                allocatedBytes,
                allocationHighWaterBytes,
                boundaryAllocatedBytes,
                boundaryAllocationHighWaterBytes,
                invocationCount,
                TimeSpan.Zero,
                TimeSpan.FromTicks(peakTicks));
        }
    }

    private static void UpdateHighWater(ref long target, long candidate)
    {
        if (candidate <= 0)
            return;

        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;

            current = observed;
        }
    }
}
