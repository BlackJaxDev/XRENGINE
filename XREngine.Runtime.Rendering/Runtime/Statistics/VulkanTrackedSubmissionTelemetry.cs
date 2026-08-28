using System;
using System.Diagnostics;
using System.Numerics;
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
                /// <summary>Allocation-free aggregate timing for the tracked submission gateway.</summary>
                public enum TrackedSubmissionTimingStage
                {
                    ImageValidation,
                    QueueOwnershipValidation,
                    LifetimePinAcquisition,
                    SubmissionStateSerialization,
                    NativeQueueAdmission,
                    NativeSubmit,
                    LifetimePublication,
                    ImagePublication,
                    DiagnosticPublication,
                    CleanupPinRelease,
                    GatewayTotal,
                }

                public enum SealedSubmissionFallbackReason
                {
                    Shape,
                    ForcedFull,
                    MissingContract,
                    ResourceVector,
                    DescriptorVector,
                    TrackingBatch,
                    ImageVector,
                    PinCommit,
                    Unknown,
                }

                public enum SealedSubmissionSealFailureReason
                {
                    ResourceState,
                    DescriptorPublication,
                    ImageState,
                    QueueOwnership,
                    PublicationRace,
                }

                public readonly record struct TrackedSubmissionTimingSnapshot(
                    long Count, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds);

                private const int TrackedTimingBuckets = 32;
                private static readonly long[][] _trackedSubmissionTimingCounts = CreateTrackedTimingCounts();
                private static long[][] CreateTrackedTimingCounts()
                    => Array.ConvertAll(
                        new long[(int)TrackedSubmissionTimingStage.GatewayTotal + 1][],
                        _ => new long[TrackedTimingBuckets]);
                private static readonly long[] _trackedSubmissionTimingCount = new long[(int)TrackedSubmissionTimingStage.GatewayTotal + 1];
                private static long _vulkanSealedSubmissionHits;
                private static long _vulkanSealedSubmissionFallbacks;
                private static readonly long[] _vulkanSealedSubmissionFallbackReasons =
                    new long[(int)SealedSubmissionFallbackReason.Unknown + 1];
                private static long _vulkanSealedSubmissionSeals;
                private static readonly long[] _vulkanSealedSubmissionSealFailures =
                    new long[(int)SealedSubmissionSealFailureReason.PublicationRace + 1];
                private static long _vulkanSealedSubmissionParitySamples;
                private static long _vulkanSealedSubmissionParityMismatches;
                private static long _vulkanResidentTemplateExactDependencyInvalidations;
                private static long _vulkanResidentTemplateBroadFallbackInvalidations;
                private static long _vulkanResidentTemplateBroadFallbackEntries;

                public static long VulkanSealedSubmissionHits
                    => Volatile.Read(ref _vulkanSealedSubmissionHits);
                public static long VulkanSealedSubmissionFallbacks
                    => Volatile.Read(ref _vulkanSealedSubmissionFallbacks);
                public static long VulkanSealedSubmissionSeals
                    => Volatile.Read(ref _vulkanSealedSubmissionSeals);
                public static long VulkanSealedSubmissionParitySamples
                    => Volatile.Read(ref _vulkanSealedSubmissionParitySamples);
                public static long VulkanSealedSubmissionParityMismatches
                    => Volatile.Read(ref _vulkanSealedSubmissionParityMismatches);
                public static long VulkanResidentTemplateExactDependencyInvalidations
                    => Volatile.Read(ref _vulkanResidentTemplateExactDependencyInvalidations);
                public static long VulkanResidentTemplateBroadFallbackInvalidations
                    => Volatile.Read(ref _vulkanResidentTemplateBroadFallbackInvalidations);
                public static long VulkanResidentTemplateBroadFallbackEntries
                    => Volatile.Read(ref _vulkanResidentTemplateBroadFallbackEntries);

                public static void RecordVulkanSealedSubmissionHit()
                    => Interlocked.Increment(ref _vulkanSealedSubmissionHits);

                public static void RecordVulkanSealedSubmissionFallback(
                    SealedSubmissionFallbackReason reason)
                {
                    Interlocked.Increment(ref _vulkanSealedSubmissionFallbacks);
                    Interlocked.Increment(
                        ref _vulkanSealedSubmissionFallbackReasons[(int)reason]);
                }

                public static long GetVulkanSealedSubmissionFallbackCount(
                    SealedSubmissionFallbackReason reason)
                    => Volatile.Read(
                        ref _vulkanSealedSubmissionFallbackReasons[(int)reason]);

                public static void RecordVulkanSealedSubmissionSeal()
                    => Interlocked.Increment(ref _vulkanSealedSubmissionSeals);

                public static void RecordVulkanSealedSubmissionSealFailure(
                    SealedSubmissionSealFailureReason reason)
                    => Interlocked.Increment(
                        ref _vulkanSealedSubmissionSealFailures[(int)reason]);

                public static long GetVulkanSealedSubmissionSealFailureCount(
                    SealedSubmissionSealFailureReason reason)
                    => Volatile.Read(
                        ref _vulkanSealedSubmissionSealFailures[(int)reason]);

                public static void RecordVulkanSealedSubmissionParity(bool matches)
                {
                    Interlocked.Increment(ref _vulkanSealedSubmissionParitySamples);
                    if (!matches)
                        Interlocked.Increment(ref _vulkanSealedSubmissionParityMismatches);
                }

                public static void RecordVulkanResidentTemplateExactDependencyInvalidation(
                    int invalidatedCount)
                {
                    if (!EnableTracking || invalidatedCount <= 0)
                        return;
                    Interlocked.Add(
                        ref _vulkanResidentTemplateExactDependencyInvalidations,
                        invalidatedCount);
                }

                public static void RecordVulkanResidentTemplateBroadFallback(
                    int affectedCount)
                {
                    if (!EnableTracking)
                        return;
                    Interlocked.Increment(
                        ref _vulkanResidentTemplateBroadFallbackInvalidations);
                    if (affectedCount > 0)
                    {
                        Interlocked.Add(
                            ref _vulkanResidentTemplateBroadFallbackEntries,
                            affectedCount);
                    }
                }

                public static TrackedSubmissionTimingSnapshot VulkanTrackedSubmissionTiming
                    => GetVulkanTrackedSubmissionTiming(TrackedSubmissionTimingStage.NativeSubmit);

                public static TrackedSubmissionTimingSnapshot GetVulkanTrackedSubmissionTiming(TrackedSubmissionTimingStage stage)
                    => new(
                        Volatile.Read(ref _trackedSubmissionTimingCount[(int)stage]),
                        TrackedPercentile(stage, 0.50), TrackedPercentile(stage, 0.95), TrackedPercentile(stage, 0.99));

                public static void RecordVulkanTrackedSubmissionTiming(TrackedSubmissionTimingStage stage, long elapsedTicks)
                {
                    if (!EnableTracking || elapsedTicks < 0)
                        return;
                    int bucket = elapsedTicks == 0
                        ? 0
                        : Math.Min(
                            TrackedTimingBuckets - 1,
                            BitOperations.Log2(unchecked((ulong)elapsedTicks)) + 1);
                    Interlocked.Increment(ref _trackedSubmissionTimingCounts[(int)stage][bucket]);
                    Interlocked.Increment(ref _trackedSubmissionTimingCount[(int)stage]);
                }

                private static double TrackedPercentile(TrackedSubmissionTimingStage stage, double quantile)
                {
                    long count = Volatile.Read(ref _trackedSubmissionTimingCount[(int)stage]);
                    if (count == 0)
                        return 0;
                    long target = Math.Max(1, (long)Math.Ceiling(count * quantile));
                    long seen = 0;
                    for (int bucket = 0; bucket < TrackedTimingBuckets; bucket++)
                    {
                        seen += Volatile.Read(ref _trackedSubmissionTimingCounts[(int)stage][bucket]);
                        if (seen < target)
                            continue;
                        long stopwatchTicks = bucket == 0 ? 0 : 1L << (bucket - 1);
                        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
                    }
                    return 0;
                }
            }
        }
    }
}
