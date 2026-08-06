using System;
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
    internal ConcurrentDictionary<string, string> ComputeDispatchOperationNames { get; } =
        new(StringComparer.Ordinal);
    private const int PublicationCapacity = 64;
    private const int GpuRenderStatsReadbackRingSize = 32;
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
    internal bool[]? _vulkanGpuProfilerCommandBufferInstrumented;
    internal int[]? _vulkanGpuProfilerCommandBufferFrameSlots;
    internal readonly GpuRenderStatsReadbackSlot?[] _gpuRenderStatsReadbackSlots =
        new GpuRenderStatsReadbackSlot?[GpuRenderStatsReadbackRingSize];
    internal readonly Dictionary<string, ulong> _gpuRenderStatsTraceHashes = [];
    internal int _gpuRenderStatsReadbackCursor;
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
    internal string? _firstFailingVulkanApi;
    internal VulkanDeviceLossRecord? _firstDeviceLossRecord;
    internal readonly object _vulkanDeviceAddressDiagnosticsLock = new();
    internal readonly VulkanDeviceAddressRange[] _vulkanDeviceAddressRanges =
        new VulkanDeviceAddressRange[VulkanDeviceAddressRangeCapacity];
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
