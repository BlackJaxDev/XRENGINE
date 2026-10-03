using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Material-independent projection of one existing resident GPU command. All
/// command selectors and geometry ownership are sealed at the scene swap boundary.
/// </summary>
public readonly record struct GpuMeshSubmissionRecord
{
    /// <summary>Existing command identity only; render consumers use the sealed fields below.</summary>
    public IRenderCommandMesh Source { get; init; }
    public uint CommandIndex { get; init; }
    public uint StableQueryKey { get; init; }
    public int PrimitiveIndex { get; init; }
    public int SourcePrimitiveCount { get; init; }
    public int RenderPass { get; init; }
    public ulong SourceOrder { get; init; }
    public XRMeshRenderer Renderer { get; init; }
    public XRMesh Mesh { get; init; }
    public XRMaterial Material { get; init; }
    public XRMaterial? MaterialOverride { get; init; }
    public RenderingParameters? RenderOptionsOverride { get; init; }
    public GpuMeshSubmissionSourceBindings SourceBindings { get; init; }
    public DrawMetadata Metadata { get; init; }
    public BoundsGpu Bounds { get; init; }
    public Matrix4x4 CurrentWorld { get; init; }
    public Matrix4x4 PreviousWorld { get; init; }
    public bool WorldMatrixIsModelMatrix { get; init; }
    public bool ForceCpuRendering { get; init; }
    public EMeshBillboardMode BillboardMode { get; init; }
    public bool ForceNoStereo { get; init; }
    public bool DisableMeshletCulling { get; init; }
    public bool HasSkinning { get; init; }
    public bool HasBlendshapes { get; init; }
    public uint LodCount { get; init; }
    public GPUScene.LODTableEntry LodMetadata { get; init; }
    public MeshletPayload? MeshletPayload { get; init; }
    public long GeometryRevision { get; init; }
    public long PayloadValidationRevision { get; init; }
    public long PayloadOwnerGeometryRevision { get; init; }
    public ulong PayloadOwnerValidationToken { get; init; }
    public uint AuthoredInstanceCount { get; init; }
    public uint AuthoredPrimitiveInstanceCount { get; init; }
    public uint InstanceCount => AuthoredInstanceCount;

    /// <summary>Checks the captured owner proof without rereading a mutable mesh payload.</summary>
    public bool HasValidatedMeshletPayload
        => MeshletPayload is { HasMeshlets: true, IsRuntimeCompatible: true }
           && PayloadValidationRevision != 0
           && PayloadOwnerValidationToken != 0
           && PayloadOwnerGeometryRevision == GeometryRevision;
}
