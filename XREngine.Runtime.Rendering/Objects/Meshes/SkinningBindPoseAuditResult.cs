using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Summarizes a CPU reconstruction of a mesh's bind-pose skinning inputs.
/// This is intended for import diagnostics and tests, not per-frame rendering.
/// </summary>
internal readonly record struct SkinningBindPoseAuditResult
{
    public int VertexCount { get; init; }
    public bool UsedPackedInfluenceBuffers { get; init; }
    public int WeightedVertexCount { get; init; }
    public int UnweightedVertexCount { get; init; }
    public int InfluenceCount { get; init; }
    public int MissingPaletteBoneCount { get; init; }
    public int InvalidInfluenceCount { get; init; }
    public int NonFiniteVertexCount { get; init; }
    public int NonFiniteMatrixCount { get; init; }
    public int MaximumInfluenceCount { get; init; }
    public float MinimumWeightSum { get; init; }
    public float MaximumWeightSum { get; init; }
    public float MaximumWeightSumError { get; init; }
    public int MaximumWeightSumErrorVertexIndex { get; init; }
    public float MaximumBoneIdentityError { get; init; }
    public string? MaximumBoneIdentityErrorBoneName { get; init; }
    public float MaximumVertexBindDisplacement { get; init; }
    public int MaximumVertexBindDisplacementIndex { get; init; }
    public float MaximumInfluenceInverseBindDifference { get; init; }
    public int MaximumInfluenceInverseBindDifferenceVertexIndex { get; init; }
    public Vector3 SourceBoundsMinimum { get; init; }
    public Vector3 SourceBoundsMaximum { get; init; }
    public Vector3 BindBoundsMinimum { get; init; }
    public Vector3 BindBoundsMaximum { get; init; }
}
