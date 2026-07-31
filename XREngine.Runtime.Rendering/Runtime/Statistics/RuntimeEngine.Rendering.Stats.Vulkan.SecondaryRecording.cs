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
                private const int VulkanSecondaryFamilyCount =
                    (int)EVulkanSecondaryCommandFamily.Count;
                private const int VulkanSecondaryEligibilityCount =
                    (int)EVulkanSecondaryRecordingEligibility.Count;
                private static readonly long[]
                    _vulkanSecondaryRecordingEligibilityCounts =
                        new long[
                            VulkanSecondaryFamilyCount *
                            VulkanSecondaryEligibilityCount];
                private static readonly long[]
                    _lastFrameVulkanSecondaryRecordingEligibilityCounts =
                        new long[
                            VulkanSecondaryFamilyCount *
                            VulkanSecondaryEligibilityCount];
                private static readonly int[]
                    _vulkanLastSecondaryRecordingEligibility =
                        new int[VulkanSecondaryFamilyCount];
                private static readonly int[]
                    _lastFrameVulkanLastSecondaryRecordingEligibility =
                        new int[VulkanSecondaryFamilyCount];

                public static EVulkanSecondaryRecordingEligibility
                    GetVulkanLastSecondaryRecordingEligibility(
                        EVulkanSecondaryCommandFamily family)
                {
                    int familyIndex = (int)family;
                    return (uint)familyIndex < VulkanSecondaryFamilyCount
                        ? (EVulkanSecondaryRecordingEligibility)Volatile.Read(
                            ref
                            _lastFrameVulkanLastSecondaryRecordingEligibility[
                                familyIndex])
                        : EVulkanSecondaryRecordingEligibility.NotEvaluated;
                }

                public static long
                    GetVulkanSecondaryRecordingEligibilityCount(
                        EVulkanSecondaryCommandFamily family,
                        EVulkanSecondaryRecordingEligibility reason)
                {
                    int index = GetVulkanSecondaryEligibilityIndex(
                        family,
                        reason);
                    return index >= 0
                        ? Volatile.Read(
                            ref
                            _lastFrameVulkanSecondaryRecordingEligibilityCounts[
                                index])
                        : 0;
                }

                public static void
                    RecordVulkanSecondaryRecordingEligibility(
                        EVulkanSecondaryCommandFamily family,
                        EVulkanSecondaryRecordingEligibility reason,
                        int operationCount = 1)
                {
                    int familyIndex = (int)family;
                    int reasonIndex = (int)reason;
                    int index = GetVulkanSecondaryEligibilityIndex(
                        family,
                        reason);
                    if (!EnableTracking ||
                        operationCount <= 0 ||
                        reasonIndex <=
                            (int)
                            EVulkanSecondaryRecordingEligibility.NotEvaluated ||
                        index < 0)
                    {
                        return;
                    }

                    Interlocked.Add(
                        ref _vulkanSecondaryRecordingEligibilityCounts[index],
                        operationCount);
                    Volatile.Write(
                        ref _vulkanLastSecondaryRecordingEligibility[
                            familyIndex],
                        reasonIndex);
                }

                private static int GetVulkanSecondaryEligibilityIndex(
                    EVulkanSecondaryCommandFamily family,
                    EVulkanSecondaryRecordingEligibility reason)
                {
                    int familyIndex = (int)family;
                    int reasonIndex = (int)reason;
                    if ((uint)familyIndex >= VulkanSecondaryFamilyCount ||
                        (uint)reasonIndex >= VulkanSecondaryEligibilityCount)
                    {
                        return -1;
                    }

                    return familyIndex * VulkanSecondaryEligibilityCount +
                           reasonIndex;
                }

                private static void
                    SnapshotAndResetSecondaryRecordingTelemetry()
                {
                    for (int index = 0;
                         index <
                         _vulkanSecondaryRecordingEligibilityCounts.Length;
                         index++)
                    {
                        _lastFrameVulkanSecondaryRecordingEligibilityCounts[
                            index] = Interlocked.Exchange(
                                ref
                                _vulkanSecondaryRecordingEligibilityCounts[
                                    index],
                                0);
                    }

                    for (int familyIndex = 0;
                         familyIndex < VulkanSecondaryFamilyCount;
                         familyIndex++)
                    {
                        _lastFrameVulkanLastSecondaryRecordingEligibility[
                            familyIndex] = Interlocked.Exchange(
                                ref
                                _vulkanLastSecondaryRecordingEligibility[
                                    familyIndex],
                                (int)
                                EVulkanSecondaryRecordingEligibility
                                    .NotEvaluated);
                    }
                }
            }
        }
    }
}
