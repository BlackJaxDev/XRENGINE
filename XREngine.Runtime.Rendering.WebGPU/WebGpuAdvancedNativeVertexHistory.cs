using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Accepted-frame input history; handles and program identity must all remain exact before reuse.</summary>
internal readonly record struct WebGpuAdvancedNativeVertexHistory(
    AdvancedGpuHandle Draw,
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Deformation,
    string ProgramIdentity,
    uint VertexCount,
    AdvancedNativeVertexInputs Inputs)
{
    /// <summary>Never substitutes object matrices for missing previous material geometry.</summary>
    internal bool TryResolve(in AdvancedVisibilityPayload source, AdvancedGpuHandle deformation,
        in AdvancedNativeVertexMaterial material, bool sourcePreviousValid, bool adjacentHistory,
        bool hasHistory, out AdvancedNativeVertexInputs previous, out EAdvancedVelocityValidityReason reason)
    {
        previous = material.Inputs;
        reason = source.TemporalReason;
        if (reason != EAdvancedVelocityValidityReason.Valid) return false;
        if (!hasHistory) { reason = EAdvancedVelocityValidityReason.HistoryReset; return false; }
        if (!adjacentHistory) { reason = EAdvancedVelocityValidityReason.FrameGap; return false; }
        if (source.Skinned && !sourcePreviousValid) { reason = EAdvancedVelocityValidityReason.HistoryReset; return false; }
        if (Draw != source.Draw || Geometry != source.Geometry || Deformation != deformation ||
            material.Program is null || ProgramIdentity != material.Program.Identity)
        { reason = EAdvancedVelocityValidityReason.TopologyChanged; return false; }
        if (VertexCount != source.VertexCount) { reason = EAdvancedVelocityValidityReason.VertexCountChanged; return false; }
        previous = Inputs;
        return true;
    }
}
