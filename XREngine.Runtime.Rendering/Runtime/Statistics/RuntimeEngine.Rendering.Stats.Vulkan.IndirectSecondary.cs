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
                private static readonly long[]
                    _vulkanIndirectSecondaryEligibilityCounts =
                        new long[
                            (int)EVulkanIndirectSecondaryEligibility.Count];
                private static readonly long[]
                    _lastFrameVulkanIndirectSecondaryEligibilityCounts =
                        new long[
                            (int)EVulkanIndirectSecondaryEligibility.Count];
                private static int _vulkanLastIndirectSecondaryEligibility;
                private static int
                    _lastFrameVulkanLastIndirectSecondaryEligibility;

                public static EVulkanIndirectSecondaryEligibility
                    VulkanLastIndirectSecondaryEligibility =>
                        (EVulkanIndirectSecondaryEligibility)Volatile.Read(
                            ref
                            _lastFrameVulkanLastIndirectSecondaryEligibility);

                public static long
                    GetVulkanIndirectSecondaryEligibilityCount(
                        EVulkanIndirectSecondaryEligibility reason)
                {
                    int reasonIndex = (int)reason;
                    return (uint)reasonIndex <
                        (uint)
                        _lastFrameVulkanIndirectSecondaryEligibilityCounts.Length
                            ? Volatile.Read(
                                ref
                                _lastFrameVulkanIndirectSecondaryEligibilityCounts[
                                    reasonIndex])
                            : 0;
                }

                public static void
                    RecordVulkanIndirectSecondaryEligibility(
                        EVulkanIndirectSecondaryEligibility reason,
                        int operationCount = 1)
                {
                    int reasonIndex = (int)reason;
                    if (!EnableTracking ||
                        operationCount <= 0 ||
                        reasonIndex <=
                            (int)
                            EVulkanIndirectSecondaryEligibility.NotEvaluated ||
                        (uint)reasonIndex >=
                            (uint)
                            _vulkanIndirectSecondaryEligibilityCounts.Length)
                    {
                        return;
                    }

                    Interlocked.Add(
                        ref _vulkanIndirectSecondaryEligibilityCounts[
                            reasonIndex],
                        operationCount);
                    Volatile.Write(
                        ref _vulkanLastIndirectSecondaryEligibility,
                        reasonIndex);
                }

                private static void
                    SnapshotAndResetIndirectSecondaryTelemetry()
                {
                    for (int reasonIndex = 0;
                         reasonIndex <
                         _vulkanIndirectSecondaryEligibilityCounts.Length;
                         reasonIndex++)
                    {
                        _lastFrameVulkanIndirectSecondaryEligibilityCounts[
                            reasonIndex] = Interlocked.Exchange(
                                ref _vulkanIndirectSecondaryEligibilityCounts[
                                    reasonIndex],
                                0);
                    }

                    _lastFrameVulkanLastIndirectSecondaryEligibility =
                        Interlocked.Exchange(
                            ref _vulkanLastIndirectSecondaryEligibility,
                            (int)
                            EVulkanIndirectSecondaryEligibility.NotEvaluated);
                }
            }
        }
    }
}
