using XREngine.Data.Rendering;
using XREngine.Rendering.Diagnostics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The inputs of a canonical visibility stream seal, other than the visibility
/// columns, which the stream compares row by row. A sealed stream is kept for a
/// later frame only while this signature and the columns are equal.
/// </summary>
/// <remarks>
/// The fields hold no frame binding. The scene native generation and the
/// deformation output buffers change in each frame, so the stream applies them
/// again when it keeps its records.
/// </remarks>
/// <param name="DatabaseEpoch">The canonical database epoch.</param>
/// <param name="DrawGenerations">The draw table generations. The seal reads draw rows and dense draw indices.</param>
/// <param name="GeometryGenerations">The geometry table generations. Records copy the canonical geometry rows.</param>
/// <param name="StaticVertices">The resident static vertex slice.</param>
/// <param name="Indices">The resident index slice.</param>
/// <param name="PassIndex">The visibility pass index of the bin keys.</param>
/// <param name="ViewMask">The output view mask of the bin keys.</param>
/// <param name="Context">The frame-operation context part of the bin keys.</param>
/// <param name="Strategy">The resolved submission strategy.</param>
/// <param name="Capabilities">The submission lane capabilities.</param>
/// <param name="OutputPolicy">The submission output policy, including the output reservation.</param>
/// <param name="DiagnosticPlan">The diagnostic readback plan of the resolved strategy.</param>
/// <param name="PreviousOutputValid">Whether the deformation history is valid; the deformation overlay flags depend on it.</param>
internal readonly record struct VulkanVisibilitySealSignature(
    ulong DatabaseEpoch,
    AdvancedGpuOwnerGenerations DrawGenerations,
    AdvancedGpuOwnerGenerations GeometryGenerations,
    VulkanFrameDataSlice StaticVertices,
    VulkanFrameDataSlice Indices,
    int PassIndex,
    uint ViewMask,
    VulkanRenderBinContextCompatibility Context,
    EMeshSubmissionStrategy Strategy,
    VulkanSubmissionLaneCapabilities Capabilities,
    VulkanSubmissionOutputPolicy OutputPolicy,
    GpuDiagnosticReadbackPlanNode? DiagnosticPlan,
    bool PreviousOutputValid)
{
    /// <summary>Returns the first group of fields that differs, or <see cref="EAdvancedVisibilityStreamReuseMiss.None"/>.</summary>
    internal EAdvancedVisibilityStreamReuseMiss FindMiss(in VulkanVisibilitySealSignature retained)
    {
        if (DatabaseEpoch != retained.DatabaseEpoch ||
            DrawGenerations != retained.DrawGenerations ||
            GeometryGenerations != retained.GeometryGenerations)
            return EAdvancedVisibilityStreamReuseMiss.Publication;
        if (!StaticVertices.Matches(retained.StaticVertices) ||
            !Indices.Matches(retained.Indices))
            return EAdvancedVisibilityStreamReuseMiss.SceneGeometry;
        if (PassIndex != retained.PassIndex || ViewMask != retained.ViewMask)
            return EAdvancedVisibilityStreamReuseMiss.Pass;
        if (Context != retained.Context)
            return EAdvancedVisibilityStreamReuseMiss.Context;
        if (Strategy != retained.Strategy ||
            Capabilities != retained.Capabilities ||
            OutputPolicy != retained.OutputPolicy ||
            DiagnosticPlan != retained.DiagnosticPlan)
            return EAdvancedVisibilityStreamReuseMiss.Submission;
        if (PreviousOutputValid != retained.PreviousOutputValid)
            return EAdvancedVisibilityStreamReuseMiss.DeformationOutput;
        return EAdvancedVisibilityStreamReuseMiss.None;
    }
}
