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
    public GpuMeshSubmissionLodTransforms? LodTransforms { get; init; }
    public bool WorldMatrixIsModelMatrix { get; init; }
    public bool ForceCpuRendering { get; init; }
    public EMeshBillboardMode BillboardMode { get; init; }
    public bool ForceNoStereo { get; init; }
    public bool DisableMeshletCulling { get; init; }
    public bool HasSkinning { get; init; }
    public bool HasBlendshapes { get; init; }
    public bool MaterialBasePassDisabled { get; init; }
    public bool MaterialShadowPassDisabled { get; init; }
    public bool RequiresLodAuxiliaryPassPublication { get; init; }
    public bool RequiresLodTransformPublication { get; init; }
    public IRenderCommandMesh? MaterialOutlineCommand { get; init; }
    public MaterialPassDefinition? OutlinePass { get; init; }
    public XRMaterial? OutlineMaterial { get; init; }
    public GpuMeshSubmissionSourceBindings? OutlineSourceBindings { get; init; }
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

    /// <summary>Uses the authored collection policy frozen for this exact LOD renderer.</summary>
    public bool IsMaterialPassEnabled(bool shadowPass)
        => !(shadowPass ? MaterialShadowPassDisabled : MaterialBasePassDisabled);

    /// <summary>Projects the same frozen geometry and deformation owner into its authored auxiliary pass.</summary>
    public bool TryGetOutlineCandidate(out GpuMeshSubmissionRecord candidate)
    {
        candidate = default;
        if (OutlinePass is not { Enabled: true } pass || OutlineMaterial is not { } material || OutlineSourceBindings is not { } bindings)
            return false;
        DrawMetadata metadata = Metadata;
        metadata.RenderPass = unchecked((uint)pass.RenderPass);
        metadata.Flags &= ~(uint)(GPUIndirectRenderFlags.Transparent | GPUIndirectRenderFlags.CpuFallbackOnly);
        if (material.IsTransparentLike()) metadata.Flags |= (uint)GPUIndirectRenderFlags.Transparent;
        candidate = this with
        {
            Material = material, MaterialOverride = material, SourceBindings = bindings,
            RenderPass = pass.RenderPass, RenderOptionsOverride = pass.RenderOptions, Metadata = metadata,
            DisableMeshletCulling = true, MaterialBasePassDisabled = false, MaterialShadowPassDisabled = true,
            OutlinePass = null, OutlineMaterial = null, OutlineSourceBindings = null,
        };
        return true;
    }

    /// <summary>Checks the captured owner proof without rereading a mutable mesh payload.</summary>
    public bool HasValidatedMeshletPayload
        => MeshletPayload is { HasMeshlets: true, IsRuntimeCompatible: true }
           && PayloadValidationRevision != 0
           && PayloadOwnerValidationToken != 0
           && PayloadOwnerGeometryRevision == GeometryRevision;
}
