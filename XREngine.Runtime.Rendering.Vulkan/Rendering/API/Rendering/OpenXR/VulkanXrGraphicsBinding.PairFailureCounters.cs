using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Counts why a layered stereo pair did not render, per pair, and writes the totals to
/// <c>openxr-pair-failures.log</c> about every five seconds. The counts are written only when
/// <c>XRE_VULKAN_RECORDING_DIAG=1</c> is set, so they also work in Release builds, where the
/// rate-limited warnings are compiled out.
/// </summary>
internal sealed unsafe partial class VulkanXrGraphicsBinding
{
    private const int OpenXrPairFailureCounterPairs = 2;
    private readonly Dictionary<string, long>[] _openXrPairFailureCounts =
        [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private long _openXrPairFailureLastFlushTimestamp;

    /// <summary>
    /// Records one failed render of a view pair with a constant reason string, and returns
    /// false so a failure return can call it directly. Pair 0 is the wide pair; pair 1 is
    /// the quad-view inset pair.
    /// </summary>
    private bool FailOpenXrStereoPair(uint pair, string reason)
    {
        if (!VulkanMeshRenderingConventions.CommandRecordingDiagnosticsEnabled ||
            pair >= OpenXrPairFailureCounterPairs)
            return false;

        Dictionary<string, long> counts = _openXrPairFailureCounts[pair];
        lock (counts)
        {
            counts.TryGetValue(reason, out long count);
            counts[reason] = count + 1;
        }

        long now = Stopwatch.GetTimestamp();
        if (_openXrPairFailureLastFlushTimestamp != 0 &&
            now - _openXrPairFailureLastFlushTimestamp < Stopwatch.Frequency * 5)
            return false;

        _openXrPairFailureLastFlushTimestamp = now;
        StringBuilder line = new("PairFailures");
        for (int p = 0; p < OpenXrPairFailureCounterPairs; p++)
        {
            Dictionary<string, long> pairCounts = _openXrPairFailureCounts[p];
            lock (pairCounts)
            {
                foreach (KeyValuePair<string, long> entry in pairCounts)
                    line.Append(" pair").Append(p).Append('.').Append(entry.Key).Append('=').Append(entry.Value);
            }
        }
        Debug.WriteAuxiliaryLog("openxr-pair-failures.log", line.ToString());
        return false;
    }
}
