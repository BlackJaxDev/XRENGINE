using System;
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

                        Volatile.Write(ref _latestVulkanFrameTelemetryVersion, writingVersion);
                        _latestVulkanFrameTelemetry = publication;
                        Volatile.Write(ref _latestVulkanFrameAuthorityId, publication.AuthorityId);
                        Volatile.Write(ref _latestVulkanFrameStartTimestamp, publication.Identity.StartTimestamp);
                        Volatile.Write(ref _latestVulkanFramePublicationSequence, publication.PublicationSequence);
                        Volatile.Write(ref _latestVulkanFrameTelemetryVersion, writingVersion + 1);
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
