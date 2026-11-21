using System.Text;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    /// <summary>The highest level that the summary counts separately. Higher levels add to this one.</summary>
    private const int DeformationLevelSummaryMaxLevel = 15;

    // Counts of the summary window. Written under the shared preparation service lock, like the
    // other summary counters, and cleared after each SUMMARY line.
    private readonly long[] _deformationLevelGateCounts = new long[(int)EAdvancedDeformationLevelGate.Count];
    private readonly long[] _deformationLevelCounts = new long[DeformationLevelSummaryMaxLevel + 1];
    private long _summaryDeformedVertices;
    private long _summaryFullVertices;

    /// <summary>
    /// Counts the outcome of one deformation level choice while the deformation slot
    /// diagnostics are on, and returns <paramref name="level"/> so that a level-0 outcome can be
    /// the return value of the choice. Counting does not allocate.
    /// </summary>
    private uint NoteDeformationLevelGate(EAdvancedDeformationLevelGate gate, uint level = 0u)
    {
        if (!s_deformationSlotDiagnostics)
            return level;
        _deformationLevelGateCounts[(int)gate]++;
        if (gate == EAdvancedDeformationLevelGate.Chosen)
            _deformationLevelCounts[(int)Math.Min(level, (uint)DeformationLevelSummaryMaxLevel)]++;
        return level;
    }

    /// <summary>
    /// Adds the deformed vertex count of one skinned draw and its full vertex count to the
    /// summary window. The ratio shows how much deformation level of detail saves.
    /// </summary>
    private void NoteDeformedVertexCount(uint deformedVertexCount, int fullVertexCount)
    {
        if (!s_deformationSlotDiagnostics)
            return;
        _summaryDeformedVertices += deformedVertexCount;
        _summaryFullVertices += fullVertexCount;
    }

    /// <summary>
    /// Appends the latched virtual geometry settings, the deformed and full vertex counts, the
    /// level gate outcomes, and the chosen level histogram of the summary window to a SUMMARY
    /// line, and then clears the window counts.
    /// </summary>
    private void AppendDeformationLevelSummary(StringBuilder line)
    {
        line.Append(" vg=").Append(AdvancedVirtualGeometryPolicy.Enabled ? 1 : 0)
            .Append(" skinnedCut=").Append(AdvancedVirtualGeometryPolicy.SkinnedEnabled ? 1 : 0)
            .Append(" deformationLod=").Append(AdvancedVirtualGeometryPolicy.DeformationLodEnabled ? 1 : 0)
            .Append(" deformedVertices=").Append(_summaryDeformedVertices)
            .Append(" fullVertices=").Append(_summaryFullVertices);
        for (EAdvancedDeformationLevelGate gate = 0; gate < EAdvancedDeformationLevelGate.Count; ++gate)
        {
            long count = _deformationLevelGateCounts[(int)gate];
            if (count != 0)
                line.Append(" gate.").Append(gate).Append('=').Append(count);
        }
        for (int level = 1; level < _deformationLevelCounts.Length; level++)
        {
            long count = _deformationLevelCounts[level];
            if (count != 0)
                line.Append(" level").Append(level).Append('=').Append(count);
        }

        Array.Clear(_deformationLevelGateCounts);
        Array.Clear(_deformationLevelCounts);
        _summaryDeformedVertices = 0L;
        _summaryFullVertices = 0L;
    }
}
