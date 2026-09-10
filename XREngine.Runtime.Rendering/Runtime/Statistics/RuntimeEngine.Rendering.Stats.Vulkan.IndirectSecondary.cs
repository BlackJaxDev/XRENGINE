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
                private static long _indirectSecondaryReuses;
                private static long _indirectSecondaryRecordings;
                private static long _indirectSecondaryKeyEvaluations;
                private static long _indirectSecondaryCompleteKeys;
                private static long _indirectSecondaryMatchingKeys;
                private static long _indirectSecondaryPolicyRejections;
                private static string _indirectSecondaryIncompleteKeyReason = string.Empty;

                /// <summary>Process-wide accepted indirect-secondary reuse decisions; not GPU completion receipts.</summary>
                public static long IndirectSecondaryReuses => Interlocked.Read(ref _indirectSecondaryReuses);

                /// <summary>Process-wide successful indirect-secondary recordings, including later discarded primaries.</summary>
                public static long IndirectSecondaryRecordings => Interlocked.Read(ref _indirectSecondaryRecordings);

                /// <summary>Prepared-key evaluations, including explicitly requested diagnostic proof checks.</summary>
                public static long IndirectSecondaryKeyEvaluations => Interlocked.Read(ref _indirectSecondaryKeyEvaluations);
                /// <summary>Complete prepared native identities; does not imply policy permission to reuse.</summary>
                public static long IndirectSecondaryCompleteKeys => Interlocked.Read(ref _indirectSecondaryCompleteKeys);
                /// <summary>Exact matches against the previous recording, before output-policy admission.</summary>
                public static long IndirectSecondaryMatchingKeys => Interlocked.Read(ref _indirectSecondaryMatchingKeys);
                /// <summary>Producer-complete draws for which output policy requires fresh recording.</summary>
                public static long IndirectSecondaryPolicyRejections => Interlocked.Read(ref _indirectSecondaryPolicyRejections);
                /// <summary>Last incomplete-key classification from explicit command-chain validation.</summary>
                public static string IndirectSecondaryIncompleteKeyReason => Volatile.Read(ref _indirectSecondaryIncompleteKeyReason);

                public static void RecordIndirectSecondaryIncompleteKey(string reason)
                    => Volatile.Write(ref _indirectSecondaryIncompleteKeyReason, reason);

                public static void RecordIndirectSecondaryReuseProof(bool evaluated, bool complete, bool matches, bool policyAllowsReuse)
                {
                    if (evaluated)
                        Interlocked.Increment(ref _indirectSecondaryKeyEvaluations);
                    if (complete)
                        Interlocked.Increment(ref _indirectSecondaryCompleteKeys);
                    if (matches)
                        Interlocked.Increment(ref _indirectSecondaryMatchingKeys);
                    if (!policyAllowsReuse)
                        Interlocked.Increment(ref _indirectSecondaryPolicyRejections);
                }

                public static void RecordIndirectSecondaryArtifact(bool reused)
                {
                    if (reused)
                        Interlocked.Increment(ref _indirectSecondaryReuses);
                    else
                        Interlocked.Increment(ref _indirectSecondaryRecordings);
                }

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
