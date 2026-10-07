using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using XREngine.Rendering.Vulkan;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class Vulkan
            {
                private static VulkanFrameTelemetryPublication _latestVulkanFrameTelemetry;
                private static long _latestVulkanFrameTelemetryVersion;
                private static long _latestVulkanFrameStartTimestamp;
                private static long _latestVulkanFramePublicationSequence;
                private static long _latestVulkanFrameAuthorityId;
                private static int _vulkanFrameTelemetryWriterGate;
                private const int CompletedFrameTimestampCapacity = 65536;
                private static readonly long[] _completedFrameTimestamps = new long[CompletedFrameTimestampCapacity];
                private static long _completedFrameTimestampSequence;
                private static long _completedFrameFirstSequence = 1;
                private static long _completedFrameResetCount;
                private static long _completedFramePreviousTimestamp;
                // Cumulative per-outcome counts of published frame roots and of their
                // command-record stage. A frame whose recording is deferred can still
                // present (replaying the last complete scene), so presented-frame
                // counters alone overstate the freshly rendered scene rate.
                private static readonly long[] _vulkanFrameOutcomeCounts =
                    new long[(int)EVulkanFrameOutcome.Failed + 1];
                private static readonly long[] _vulkanCommandRecordOutcomeCounts =
                    new long[(int)EVulkanFrameOutcome.Failed + 1];

                /// <summary>
                /// Publishes the single shared Vulkan frame schema and folds its authority-owned
                /// CPU aggregates into the normal render-statistics snapshot cadence.
                /// </summary>
                public static void PublishVulkanFrameTelemetry(
                    in VulkanFrameTelemetryPublication publication,
                    ReadOnlySpan<VulkanCpuStageTelemetry> cpuStages)
                {
                    if (!EnableTracking)
                        return;

                    SpinWait spinner = default;
                    while (Interlocked.CompareExchange(ref _vulkanFrameTelemetryWriterGate, 1, 0) != 0)
                        spinner.SpinOnce();

                    try
                    {
                        long currentStartTimestamp = Volatile.Read(ref _latestVulkanFrameStartTimestamp);
                        long currentSequence = Volatile.Read(ref _latestVulkanFramePublicationSequence);
                        long currentAuthorityId = Volatile.Read(ref _latestVulkanFrameAuthorityId);
                        bool sameAuthority = publication.AuthorityId == currentAuthorityId;
                        if ((sameAuthority && publication.PublicationSequence <= currentSequence) ||
                            (!sameAuthority &&
                             (publication.Identity.StartTimestamp < currentStartTimestamp ||
                              (publication.Identity.StartTimestamp == currentStartTimestamp &&
                               publication.AuthorityId <= currentAuthorityId))))
                        {
                            return;
                        }

                        long writingVersion = Volatile.Read(ref _latestVulkanFrameTelemetryVersion) + 1;
                        if ((writingVersion & 1) == 0)
                            writingVersion++;

                        long completedTimestamp = 0;
                        if (!sameAuthority && currentSequence != 0)
                            ResetCompletedFrameIntervals();
                        if (publication.Outcome == EVulkanFrameOutcome.Completed)
                        {
                            completedTimestamp = Stopwatch.GetTimestamp();
                            long sequence = checked(_completedFrameTimestampSequence + 1);
                            _completedFrameTimestampSequence = sequence;
                            _completedFrameTimestamps[(int)((sequence - 1) % CompletedFrameTimestampCapacity)] = completedTimestamp;
                            if (_completedFramePreviousTimestamp == 0)
                                _completedFrameFirstSequence = sequence;
                            _completedFramePreviousTimestamp = completedTimestamp;
                        }
                        else if (_completedFramePreviousTimestamp != 0)
                            ResetCompletedFrameIntervals();

                        Volatile.Write(ref _latestVulkanFrameTelemetryVersion, writingVersion);
                        _latestVulkanFrameTelemetry = publication with { CompletedTimestamp = completedTimestamp };
                        Volatile.Write(ref _latestVulkanFrameAuthorityId, publication.AuthorityId);
                        Volatile.Write(ref _latestVulkanFrameStartTimestamp, publication.Identity.StartTimestamp);
                        Volatile.Write(ref _latestVulkanFramePublicationSequence, publication.PublicationSequence);
                        Volatile.Write(ref _latestVulkanFrameTelemetryVersion, writingVersion + 1);
                        CountOutcome(_vulkanFrameOutcomeCounts, publication.Outcome);
                        CountOutcome(_vulkanCommandRecordOutcomeCounts, publication.CommandRecord.Outcome);
                    }
                    finally
                    {
                        Volatile.Write(ref _vulkanFrameTelemetryWriterGate, 0);
                    }

                    for (int telemetryIndex = 0; telemetryIndex < cpuStages.Length; telemetryIndex++)
                    {
                        ref readonly VulkanCpuStageTelemetry telemetry = ref cpuStages[telemetryIndex];
                        int stageIndex = (int)telemetry.Stage;
                        if ((uint)stageIndex >= (uint)_vulkanCpuStageTicks.Length)
                            continue;

                        Interlocked.Add(ref _vulkanCpuStageTicks[stageIndex], telemetry.Elapsed.Ticks);
                        Interlocked.Add(ref _vulkanCpuStageAllocatedBytes[stageIndex], telemetry.AllocatedBytes);
                        Interlocked.Add(ref _vulkanCpuStageBoundaryAllocatedBytes[stageIndex], telemetry.BoundaryAllocatedBytes);
                        Interlocked.Add(ref _vulkanCpuStageInvocationCount[stageIndex], telemetry.InvocationCount);
                        Interlocked.Add(ref _vulkanCpuStageCumulativeTicks[stageIndex], telemetry.Elapsed.Ticks);
                        UpdateHighWater(ref _vulkanCpuStagePeakTicks[stageIndex], telemetry.PeakElapsed.Ticks);
                        UpdateHighWater(ref _vulkanCpuStageAllocationHighWaterBytes[stageIndex], telemetry.AllocationHighWaterBytes);
                        UpdateHighWater(ref _vulkanCpuStageBoundaryAllocationHighWaterBytes[stageIndex], telemetry.BoundaryAllocationHighWaterBytes);
                    }
                }

                private static void CountOutcome(long[] counts, EVulkanFrameOutcome outcome)
                {
                    int index = (int)outcome;
                    if ((uint)index < (uint)counts.Length)
                        Interlocked.Increment(ref counts[index]);
                }

                private static void ResetCompletedFrameIntervals()
                {
                    _completedFramePreviousTimestamp = 0;
                    _completedFrameFirstSequence = _completedFrameTimestampSequence + 1;
                    _completedFrameResetCount = checked(_completedFrameResetCount + 1);
                }

                /// <summary>Gets a cursor or the p95 interval after a prior cursor.</summary>
                public static VulkanCompletedFrameIntervalTelemetry GetVulkanCompletedFrameIntervalTelemetry(
                    long afterSequence = -1)
                {
                    long[]? rented = afterSequence >= 0
                        ? ArrayPool<long>.Shared.Rent(CompletedFrameTimestampCapacity)
                        : null;
                    int count = 0;
                    long sequence = 0;
                    long resetCount = 0;
                    long dropped = 0;
                    bool valid = true;
                    SpinWait spinner = default;
                    while (Interlocked.CompareExchange(ref _vulkanFrameTelemetryWriterGate, 1, 0) != 0)
                        spinner.SpinOnce();
                    try
                    {
                        sequence = _completedFrameTimestampSequence;
                        resetCount = _completedFrameResetCount;
                        if (afterSequence >= 0)
                        {
                            long earliest = Math.Max(_completedFrameFirstSequence,
                                sequence - CompletedFrameTimestampCapacity + 1);
                            long firstInterval = Math.Max(afterSequence + 1, earliest + 1);
                            dropped = Math.Max(0, firstInterval - (afterSequence + 1));
                            if (afterSequence > sequence || dropped != 0)
                                valid = false;
                            for (long sampleSequence = firstInterval; sampleSequence <= sequence; ++sampleSequence)
                            {
                                long current = _completedFrameTimestamps[(int)((sampleSequence - 1) % CompletedFrameTimestampCapacity)];
                                long previous = _completedFrameTimestamps[(int)((sampleSequence - 2) % CompletedFrameTimestampCapacity)];
                                long interval = current - previous;
                                if (interval <= 0)
                                {
                                    valid = false;
                                    continue;
                                }
                                rented![count++] = interval;
                            }
                        }
                    }
                    finally
                    {
                        Volatile.Write(ref _vulkanFrameTelemetryWriterGate, 0);
                    }

                    try
                    {
                        if (count == 0)
                            return new(sequence, resetCount, 0, dropped, 0.0, false);
                        Array.Sort(rented!, 0, count);
                        int percentileIndex = (int)Math.Ceiling(count * 0.95) - 1;
                        double p95Milliseconds = rented![percentileIndex] * 1000.0 / Stopwatch.Frequency;
                        return new(sequence, resetCount, count, dropped, p95Milliseconds, valid);
                    }
                    finally
                    {
                        if (rented is not null)
                            ArrayPool<long>.Shared.Return(rented);
                    }
                }

                /// <summary>Cumulative count of published frame roots with the given outcome.</summary>
                public static long GetVulkanFrameOutcomeCount(EVulkanFrameOutcome outcome)
                    => (uint)outcome < (uint)_vulkanFrameOutcomeCounts.Length
                        ? Volatile.Read(ref _vulkanFrameOutcomeCounts[(int)outcome])
                        : 0L;

                /// <summary>
                /// Cumulative count of published frame roots whose command-record stage
                /// ended with the given outcome. Completed counts freshly recorded scenes.
                /// </summary>
                public static long GetVulkanCommandRecordOutcomeCount(EVulkanFrameOutcome outcome)
                    => (uint)outcome < (uint)_vulkanCommandRecordOutcomeCounts.Length
                        ? Volatile.Read(ref _vulkanCommandRecordOutcomeCounts[(int)outcome])
                        : 0L;

                /// <summary>Reads the newest complete shared Vulkan frame publication.</summary>
                public static bool TryGetLatestVulkanFrameTelemetry(out VulkanFrameTelemetryPublication publication)
                {
                    long version = Volatile.Read(ref _latestVulkanFrameTelemetryVersion);
                    if (version == 0 || (version & 1) != 0)
                    {
                        publication = default;
                        return false;
                    }

                    publication = _latestVulkanFrameTelemetry;
                    long verifiedVersion = Volatile.Read(ref _latestVulkanFrameTelemetryVersion);
                    return verifiedVersion == version && (verifiedVersion & 1) == 0;
                }

                /// <summary>Newest complete publication, or the default schema before the first root settles.</summary>
                public static VulkanFrameTelemetryPublication LatestVulkanFrameTelemetry
                    => TryGetLatestVulkanFrameTelemetry(out VulkanFrameTelemetryPublication publication)
                        ? publication
                        : default;

                /// <summary>Returns the shared CPU-stage schema for the last statistics snapshot.</summary>
                public static VulkanCpuStageTelemetry GetVulkanCpuStageTelemetry(EVulkanCpuStage stage)
                {
                    int index = (int)stage;
                    if ((uint)index >= (uint)_lastFrameVulkanCpuStageTicks.Length)
                        return default;

                    return new VulkanCpuStageTelemetry(
                        stage,
                        TimeSpan.FromTicks(Volatile.Read(ref _lastFrameVulkanCpuStageTicks[index])),
                        Volatile.Read(ref _lastFrameVulkanCpuStageAllocatedBytes[index]),
                        Volatile.Read(ref _lastFrameVulkanCpuStageAllocationHighWaterBytes[index]),
                        Volatile.Read(ref _lastFrameVulkanCpuStageBoundaryAllocatedBytes[index]),
                        Volatile.Read(ref _lastFrameVulkanCpuStageBoundaryAllocationHighWaterBytes[index]),
                        Volatile.Read(ref _vulkanCpuStageInvocationCount[index]),
                        TimeSpan.FromTicks(Volatile.Read(ref _vulkanCpuStageCumulativeTicks[index])),
                        TimeSpan.FromTicks(Volatile.Read(ref _vulkanCpuStagePeakTicks[index])));
                }
            }
        }
    }
}
