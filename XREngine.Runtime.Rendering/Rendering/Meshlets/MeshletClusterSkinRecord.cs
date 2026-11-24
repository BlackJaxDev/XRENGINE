using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The skinning record of one meshlet of a skinned cluster hierarchy payload (8 bytes), in meshlet
/// order. It names the palette bones that influence any vertex of the meshlet. With linear blend
/// skinning a posed vertex lies in the convex hull of its bones' transforms of the bind vertex, so
/// the meshlet's posed geometry lies inside the convex hull of its bind sphere moved by each of
/// these bones. The Advanced task stage takes the box around those moved spheres, which holds that
/// hull in every pose (see <c>AdvancedClusterSkinTables</c>).
/// </summary>
/// <param name="FirstBone">The first entry of the meshlet's bones in the payload's cluster bone list.</param>
/// <param name="BoneCount">The number of bones in the list; 0 for a meshlet without bone influences.</param>
/// <param name="Flags">
/// <see cref="MorphFlag"/> when a vertex of the meshlet has a blendshape delta. Blendshape deltas move
/// vertices outside the bone-derived bound, so the runtime treats such a meshlet conservatively.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletClusterSkinRecord(uint FirstBone, ushort BoneCount, ushort Flags)
{
    /// <summary>A vertex of the meshlet has a blendshape delta.</summary>
    public const ushort MorphFlag = 1;

    /// <summary>True when a vertex of the meshlet has a blendshape delta.</summary>
    public bool HasMorphVertices => (Flags & MorphFlag) != 0;
}
