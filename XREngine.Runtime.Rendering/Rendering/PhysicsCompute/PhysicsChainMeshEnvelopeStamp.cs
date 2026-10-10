using System.Numerics;

namespace XREngine.Rendering.Compute;

/// <summary>Identifies the authored mesh inputs used by one conservative bound.</summary>
public readonly record struct PhysicsChainMeshEnvelopeStamp(
    XRMesh Mesh,
    long GeometryRevision,
    ulong BlendshapeWeightsVersion,
    Matrix4x4? BindRoot,
    object BoneLayout)
{
    public float VertexInfluenceRadius { get; init; }
    /// <summary>Checks the current inputs without reading GPU memory.</summary>
    public bool Matches(XRMeshRenderer renderer, XRMesh mesh)
        => ReferenceEquals(Mesh, mesh)
            && GeometryRevision == mesh.GeometryRevision
            && BindRoot == mesh.BindRootMatrix
            && ReferenceEquals(BoneLayout, mesh.UtilizedBones)
            && (mesh.BlendshapeCount == 0u ||
                BlendshapeWeightsVersion == renderer.BlendshapeWeightsVersion);
}
