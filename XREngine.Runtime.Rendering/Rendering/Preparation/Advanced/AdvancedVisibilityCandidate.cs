using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU-readable early visibility candidate keyed by a stable draw handle.
/// Explicit offsets preserve the std430 padding before the first vec4; setting
/// only the total size does not align System.Numerics.Vector4 fields to 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public readonly record struct AdvancedVisibilityCandidate(
    [field: FieldOffset(0)] AdvancedGpuHandle Draw,
    [field: FieldOffset(16)] Vector4 BoundsSphere,
    [field: FieldOffset(32)] Vector4 BoundsMin,
    [field: FieldOffset(48)] Vector4 BoundsMax,
    [field: FieldOffset(64)] ulong ViewMask,
    [field: FieldOffset(72)] uint BvhLeaf,
    [field: FieldOffset(76)] EAdvancedVisibilityPreparationFlags Flags)
{
    /// <summary>
    /// Mesh-space distance that the cluster cut of a skinned draw subtracts from the camera
    /// distance to <see cref="BoundsSphere"/>: the largest blendshape displacement of the mesh,
    /// because the posed bounds come from the bones and do not contain the blendshape deltas.
    /// Zero for other draws. It uses the padding word after <see cref="Draw"/>, which the visibility
    /// passes do not read.
    /// </summary>
    [field: FieldOffset(8)] public float ClusterCutMargin { get; init; }

    /// <summary>
    /// Minimum cluster level of a skinned draw whose deformation job deforms only the prefix of
    /// the cooked deformation order for this level. The skinned cluster cut treats the projected
    /// error of every group finer than this level (with a finite error) as zero, so it never draws
    /// a cluster that reads a vertex outside the prefix. Zero for other draws, which keeps the cut
    /// as it was. It uses the second padding word after <see cref="Draw"/> (word 3), which the
    /// physics chain bounds patch does not write.
    /// </summary>
    [field: FieldOffset(12)] public uint DeformationLevel { get; init; }
}
