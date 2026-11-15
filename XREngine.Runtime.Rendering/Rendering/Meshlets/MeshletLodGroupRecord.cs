using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// One group of a meshlet cluster hierarchy (32 bytes). A group is a set of clusters that the cook
/// merged and simplified together. <see cref="SimplifiedSphere"/> and <see cref="SimplifiedError"/>
/// are the bounds of the simplified result: a cluster of this group is drawn while the projected
/// error of these bounds is above the threshold (the coarser result is not good enough), and a
/// cluster that was made from this group is drawn when it is at or below the threshold.
/// <see cref="SimplifiedError"/> is <see cref="float.MaxValue"/> for a group that the cook could not
/// simplify further. The sphere and error grow from child to parent group, which makes the cut free
/// of cracks.
/// </summary>
/// <param name="SimplifiedSphere">Center (xyz) and radius (w) in mesh space.</param>
/// <param name="SimplifiedError">Simplification error in mesh-space units.</param>
/// <param name="Depth">Hierarchy level at which the cook made the group (0 for the full-detail level).</param>
/// <param name="Reserved0">Zero; keeps the record at 32 bytes.</param>
/// <param name="Reserved1">Zero; keeps the record at 32 bytes.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletLodGroupRecord(
    Vector4 SimplifiedSphere,
    float SimplifiedError,
    int Depth,
    uint Reserved0 = 0u,
    uint Reserved1 = 0u);
