using System;
using System.Threading;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class Vulkan
            {
                private static long _vulkanPreparedStreamElements;
                private static long _vulkanPreparedStreamBytes;
                private static long _vulkanPreparedStreamHighWaterBytes;
                private static long _vulkanDescriptorPublicationScanned;
                private static long _vulkanDescriptorPublicationDirty;
                private static long _vulkanDescriptorPublicationRanges;
                private static long _vulkanDescriptorPublicationNativeBytes;
                private static long _vulkanDescriptorPublicationCompatibilityTicks;
                private static int _vulkanDescriptorPublicationHighWater;
                private static int _vulkanWorkerQueueDepth;
                private static int _vulkanWorkerQueueBytes;
                private static int _vulkanWorkerQueueHighWaterDepth;
                private static int _vulkanWorkerQueueHighWaterBytes;
                private static long _vulkanWorkerLocalMergeBytes;
                private static long _vulkanWorkerLocalMergeTicks;
                private static long _vulkanWorkerExecutionMergeBytes;
                private static long _vulkanWorkerExecutionMergeTicks;

                private static long _lastFrameVulkanPreparedStreamElements;
                private static long _lastFrameVulkanPreparedStreamBytes;
                private static long _lastFrameVulkanPreparedStreamHighWaterBytes;
                private static long _lastFrameVulkanDescriptorPublicationScanned;
                private static long _lastFrameVulkanDescriptorPublicationDirty;
                private static long _lastFrameVulkanDescriptorPublicationRanges;
                private static long _lastFrameVulkanDescriptorPublicationNativeBytes;
                private static long _lastFrameVulkanDescriptorPublicationCompatibilityTicks;
                private static int _lastFrameVulkanDescriptorPublicationHighWater;
                private static int _lastFrameVulkanWorkerQueueDepth;
                private static int _lastFrameVulkanWorkerQueueBytes;
                private static int _lastFrameVulkanWorkerQueueHighWaterDepth;
                private static int _lastFrameVulkanWorkerQueueHighWaterBytes;
                private static long _lastFrameVulkanWorkerLocalMergeBytes;
                private static long _lastFrameVulkanWorkerLocalMergeTicks;
                private static long _lastFrameVulkanWorkerExecutionMergeBytes;
                private static long _lastFrameVulkanWorkerExecutionMergeTicks;

                public static long VulkanPreparedStreamElements => Volatile.Read(ref _lastFrameVulkanPreparedStreamElements);
                public static long VulkanPreparedStreamBytes => Volatile.Read(ref _lastFrameVulkanPreparedStreamBytes);
                public static long VulkanPreparedStreamHighWaterBytes => Volatile.Read(ref _lastFrameVulkanPreparedStreamHighWaterBytes);
                public static long VulkanDescriptorPublicationScanned => Volatile.Read(ref _lastFrameVulkanDescriptorPublicationScanned);
                public static long VulkanDescriptorPublicationDirty => Volatile.Read(ref _lastFrameVulkanDescriptorPublicationDirty);
                public static long VulkanDescriptorPublicationRanges => Volatile.Read(ref _lastFrameVulkanDescriptorPublicationRanges);
                public static long VulkanDescriptorPublicationNativeBytes => Volatile.Read(ref _lastFrameVulkanDescriptorPublicationNativeBytes);
                public static double VulkanDescriptorPublicationCompatibilityMs => TimeSpan.FromTicks(Volatile.Read(ref _lastFrameVulkanDescriptorPublicationCompatibilityTicks)).TotalMilliseconds;
                public static int VulkanDescriptorPublicationHighWater => Volatile.Read(ref _lastFrameVulkanDescriptorPublicationHighWater);
                public static int VulkanWorkerQueueDepth => Volatile.Read(ref _lastFrameVulkanWorkerQueueDepth);
                public static int VulkanWorkerQueueBytes => Volatile.Read(ref _lastFrameVulkanWorkerQueueBytes);
                public static int VulkanWorkerQueueHighWaterDepth => Volatile.Read(ref _lastFrameVulkanWorkerQueueHighWaterDepth);
                public static int VulkanWorkerQueueHighWaterBytes => Volatile.Read(ref _lastFrameVulkanWorkerQueueHighWaterBytes);
                public static long VulkanWorkerLocalMergeBytes => Volatile.Read(ref _lastFrameVulkanWorkerLocalMergeBytes);
                public static double VulkanWorkerLocalMergeMs => StopwatchTicksToTimeSpan(Volatile.Read(ref _lastFrameVulkanWorkerLocalMergeTicks)).TotalMilliseconds;
                public static long VulkanWorkerExecutionMergeBytes => Volatile.Read(ref _lastFrameVulkanWorkerExecutionMergeBytes);
                public static double VulkanWorkerExecutionMergeMs => StopwatchTicksToTimeSpan(Volatile.Read(ref _lastFrameVulkanWorkerExecutionMergeTicks)).TotalMilliseconds;

                public static void RecordVulkanPreparedFrameStreamTelemetry(
                    int elementCount, int byteCount, int highWaterBytes)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vulkanPreparedStreamElements, Math.Max((long)elementCount, 0L));
                    Interlocked.Exchange(ref _vulkanPreparedStreamBytes, Math.Max((long)byteCount, 0L));
                    Interlocked.Exchange(ref _vulkanPreparedStreamHighWaterBytes, Math.Max((long)highWaterBytes, 0L));
                }

                public static void RecordVulkanDescriptorPublicationStream(
                    int scanned, int dirty, int ranges, long nativeBytes,
                    ulong compatibilityTicks, int highWater)
                {
                    if (!EnableTracking)
                        return;

                    AddNonNegativeLong(ref _vulkanDescriptorPublicationScanned, scanned);
                    AddNonNegativeLong(ref _vulkanDescriptorPublicationDirty, dirty);
                    AddNonNegativeLong(ref _vulkanDescriptorPublicationRanges, ranges);
                    AddNonNegativeLong(ref _vulkanDescriptorPublicationNativeBytes, nativeBytes);
                    if (compatibilityTicks > 0)
                        Interlocked.Add(ref _vulkanDescriptorPublicationCompatibilityTicks, unchecked((long)compatibilityTicks));
                    UpdateHighWater(ref _vulkanDescriptorPublicationHighWater, highWater);
                }

                public static void RecordVulkanCommandChainWorkerLayoutTelemetry(
                    int queueDepth, int queueBytes, int queueHighWaterDepth,
                    int queueHighWaterBytes, int localMergeBytes, long localMergeTicks,
                    int executionMergeBytes, long executionMergeTicks)
                {
                    if (!EnableTracking)
                        return;

                    Interlocked.Exchange(ref _vulkanWorkerQueueDepth, Math.Max(queueDepth, 0));
                    Interlocked.Exchange(ref _vulkanWorkerQueueBytes, Math.Max(queueBytes, 0));
                    UpdateHighWater(ref _vulkanWorkerQueueHighWaterDepth, queueHighWaterDepth);
                    UpdateHighWater(ref _vulkanWorkerQueueHighWaterBytes, queueHighWaterBytes);
                    Interlocked.Exchange(ref _vulkanWorkerLocalMergeBytes, Math.Max((long)localMergeBytes, 0L));
                    Interlocked.Exchange(ref _vulkanWorkerLocalMergeTicks, Math.Max(localMergeTicks, 0L));
                    Interlocked.Exchange(ref _vulkanWorkerExecutionMergeBytes, Math.Max((long)executionMergeBytes, 0L));
                    Interlocked.Exchange(ref _vulkanWorkerExecutionMergeTicks, Math.Max(executionMergeTicks, 0L));
                }

                private static void SnapshotAndResetHardeningTelemetry()
                {
                    _lastFrameVulkanPreparedStreamElements = Interlocked.Exchange(ref _vulkanPreparedStreamElements, 0);
                    _lastFrameVulkanPreparedStreamBytes = Interlocked.Exchange(ref _vulkanPreparedStreamBytes, 0);
                    _lastFrameVulkanPreparedStreamHighWaterBytes = Interlocked.Exchange(ref _vulkanPreparedStreamHighWaterBytes, 0);
                    _lastFrameVulkanDescriptorPublicationScanned = Interlocked.Exchange(ref _vulkanDescriptorPublicationScanned, 0);
                    _lastFrameVulkanDescriptorPublicationDirty = Interlocked.Exchange(ref _vulkanDescriptorPublicationDirty, 0);
                    _lastFrameVulkanDescriptorPublicationRanges = Interlocked.Exchange(ref _vulkanDescriptorPublicationRanges, 0);
                    _lastFrameVulkanDescriptorPublicationNativeBytes = Interlocked.Exchange(ref _vulkanDescriptorPublicationNativeBytes, 0);
                    _lastFrameVulkanDescriptorPublicationCompatibilityTicks = Interlocked.Exchange(ref _vulkanDescriptorPublicationCompatibilityTicks, 0);
                    _lastFrameVulkanDescriptorPublicationHighWater = Interlocked.Exchange(ref _vulkanDescriptorPublicationHighWater, 0);
                    _lastFrameVulkanWorkerQueueDepth = Interlocked.Exchange(ref _vulkanWorkerQueueDepth, 0);
                    _lastFrameVulkanWorkerQueueBytes = Interlocked.Exchange(ref _vulkanWorkerQueueBytes, 0);
                    _lastFrameVulkanWorkerQueueHighWaterDepth = Interlocked.Exchange(ref _vulkanWorkerQueueHighWaterDepth, 0);
                    _lastFrameVulkanWorkerQueueHighWaterBytes = Interlocked.Exchange(ref _vulkanWorkerQueueHighWaterBytes, 0);
                    _lastFrameVulkanWorkerLocalMergeBytes = Interlocked.Exchange(ref _vulkanWorkerLocalMergeBytes, 0);
                    _lastFrameVulkanWorkerLocalMergeTicks = Interlocked.Exchange(ref _vulkanWorkerLocalMergeTicks, 0);
                    _lastFrameVulkanWorkerExecutionMergeBytes = Interlocked.Exchange(ref _vulkanWorkerExecutionMergeBytes, 0);
                    _lastFrameVulkanWorkerExecutionMergeTicks = Interlocked.Exchange(ref _vulkanWorkerExecutionMergeTicks, 0);
                }

                private static TimeSpan StopwatchTicksToTimeSpan(long ticks)
                    => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / System.Diagnostics.Stopwatch.Frequency);

                private static void AddNonNegativeLong(ref long target, long value)
                {
                    if (value > 0)
                        Interlocked.Add(ref target, value);
                }
            }
        }
    }
}
