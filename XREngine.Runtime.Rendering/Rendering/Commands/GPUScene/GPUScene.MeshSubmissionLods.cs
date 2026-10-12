using XREngine.Components.Scene.Mesh;
using System.Numerics;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private readonly XRMeshRenderer?[] _meshSubmissionLodRenderers = new XRMeshRenderer?[MaxLogicalMeshLodCount];
    private GpuMeshSubmissionSourceBindings?[] _updatingMeshSubmissionLodBindings = [];
    private GpuMeshSubmissionSourceBindings?[] _updatingMeshSubmissionOutlineBindings = [];
    private GpuMeshSubmissionLodTransforms?[] _publishedMeshSubmissionLodTransforms = [];
    private bool[] _publishedMeshSubmissionSkinning = [];

    private static RenderableMesh? ResolveLodRenderable(RenderInfo? renderInfo)
        => (renderInfo as RenderInfo3D)?.OwnerRenderableMesh ?? renderInfo?.Owner as RenderableMesh;

    private void CaptureMeshSubmissionLods(GpuMeshSubmissionPublication destination, int recordIndex,
        in GpuMeshSubmissionRecord record, LogicalMeshState? state, bool previousSourceSkinned)
    {
        RenderableMesh? renderable = ResolveLodRenderable((record.Source as RenderCommand)?.OwnerRenderInfo);
        bool primaryCommand = renderable?.IsPrimaryMeshCommand(record.Source) == true;
        if (primaryCommand)
        {
            MaterialPassSet? sourcePassSet = (record.MaterialOverride ?? record.Renderer.Material)?.PassSet;
            destination.WritableRecords[recordIndex] = record with
            {
                MaterialBasePassDisabled = IsLodMaterialPassDisabled(sourcePassSet, EMaterialPassIdentity.Base),
                MaterialShadowPassDisabled = IsLodMaterialPassDisabled(sourcePassSet, EMaterialPassIdentity.Shadow),
            };
        }
        if (state is null || state.LODCount <= 1)
        {
            ClearUpdatingMeshSubmissionLods((uint)recordIndex);
            int authoredCount = renderable?.CollectLodRenderers((uint)record.PrimitiveIndex, _meshSubmissionLodRenderers) ?? 1;
            Array.Clear(_meshSubmissionLodRenderers);
            destination.SetAuthoredLodCount(recordIndex, authoredCount);
            return;
        }
        int capacity = checked(_updatingMeshSubmissions.Length * MaxLogicalMeshLodCount);
        if (_updatingMeshSubmissionLodBindings.Length < capacity)
            Array.Resize(ref _updatingMeshSubmissionLodBindings, capacity);
        if (_updatingMeshSubmissionOutlineBindings.Length < capacity)
            Array.Resize(ref _updatingMeshSubmissionOutlineBindings, capacity);
        int count = renderable?.CollectLodRenderers((uint)record.PrimitiveIndex, _meshSubmissionLodRenderers)
            ?? checked((int)state.LODCount);
        bool authoredMaterialPass = primaryCommand &&
            record.MaterialOverride is null && record.RenderPass == record.Material.RenderPass;
        destination.SetAuthoredLodCount(recordIndex, count);
        if (primaryCommand)
            foreach (XRMeshRenderer? renderer in _meshSubmissionLodRenderers)
                if ((record.MaterialOverride ?? renderer?.Material)?.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outline) == true && outline.Enabled)
                {
                    bool modeled = outline.ShaderBehavior == EngineMaterialSemanticIdentity.UberOutlineV1;
                    destination.WritableRecords[recordIndex] = destination.WritableRecords[recordIndex] with
                    {
                        MaterialOutlineCommand = renderable!.MaterialOutlineCommand,
                        RequiresLodAuxiliaryPassPublication = destination.WritableRecords[recordIndex].RequiresLodAuxiliaryPassPublication || !modeled,
                    };
                }
        try
        {
            for (int level = (int)Math.Min(state.LODCount, MaxLogicalMeshLodCount); level < MaxLogicalMeshLodCount; level++)
            {
                _updatingMeshSubmissionLodBindings[recordIndex * MaxLogicalMeshLodCount + level] = null;
                _updatingMeshSubmissionOutlineBindings[recordIndex * MaxLogicalMeshLodCount + level] = null;
            }
            for (int level = 0; level < Math.Min(state.LODCount, MaxLogicalMeshLodCount); level++)
            {
                int bindingIndex = checked(recordIndex * MaxLogicalMeshLodCount + level);
                uint meshId = state.MeshIds[level];
                if (meshId == 0 || state.Meshes[level] is not { } mesh)
                { _updatingMeshSubmissionLodBindings[bindingIndex] = null; _updatingMeshSubmissionOutlineBindings[bindingIndex] = null; continue; }
                XRMeshRenderer renderer = _meshSubmissionLodRenderers[level] ?? record.Renderer;
                bool hasMesh = renderer.TryGetMesh(record.PrimitiveIndex, out XRMesh? ownedMesh, out XRMaterial? authoredMaterial);
                XRMaterial? material = record.MaterialOverride ?? authoredMaterial;
                if (!hasMesh || !ReferenceEquals(mesh, ownedMesh) || material is null)
                { _updatingMeshSubmissionLodBindings[bindingIndex] = null; _updatingMeshSubmissionOutlineBindings[bindingIndex] = null; continue; }
                GpuMeshSubmissionSourceBindings bindings = GpuMeshSubmissionSourceBindings.Capture(
                    mesh, renderer, material, _updatingMeshSubmissionLodBindings[bindingIndex]);
                _updatingMeshSubmissionLodBindings[bindingIndex] = bindings;
                MeshletPayload? payload = mesh.MeshletPayload;
                DrawMetadata metadata = record.Metadata;
                metadata.MeshID = meshId;
                metadata.LodPolicy = (uint)level;
                int renderPass = authoredMaterialPass ? material.RenderPass : record.RenderPass;
                metadata.RenderPass = unchecked((uint)renderPass);
                metadata.Flags &= ~(uint)(GPUIndirectRenderFlags.Transparent | GPUIndirectRenderFlags.CpuFallbackOnly |
                    GPUIndirectRenderFlags.Skinned | GPUIndirectRenderFlags.BlendShapes);
                if (material.IsTransparentLike()) metadata.Flags |= (uint)GPUIndirectRenderFlags.Transparent;
                if (record.ForceCpuRendering || material.RenderOptions?.ExcludeFromGpuIndirect == true)
                    metadata.Flags |= (uint)GPUIndirectRenderFlags.CpuFallbackOnly;
                if (mesh.HasSkinning) metadata.Flags |= (uint)GPUIndirectRenderFlags.Skinned;
                if (mesh.HasBlendshapes) metadata.Flags |= (uint)GPUIndirectRenderFlags.BlendShapes;
                Matrix4x4 currentWorld = record.CurrentWorld;
                Matrix4x4 previousWorld = record.PreviousWorld;
                bool unresolvedTransform = record.WorldMatrixIsModelMatrix &&
                    (mesh.HasSkinning != record.HasSkinning || mesh.HasSkinning != previousSourceSkinned);
                if (record.WorldMatrixIsModelMatrix && record.LodTransforms is { } transforms)
                {
                    // A custom command matrix is authoritative. Only reconstruct the
                    // other transform convention when the source proves it uses the
                    // captured component/identity default for both temporal images.
                    bool currentResolved = TryResolveLodWorld(record.CurrentWorld, record.HasSkinning, mesh.HasSkinning,
                        transforms.SkinningEnabled, transforms.CurrentComponentWorld, out currentWorld);
                    bool previousResolved = TryResolveLodWorld(record.PreviousWorld, previousSourceSkinned, mesh.HasSkinning,
                        transforms.PreviousSkinningEnabled, transforms.PreviousComponentWorld, out previousWorld);
                    unresolvedTransform = !currentResolved || !previousResolved;
                }
                MaterialPassSet? passSet = primaryCommand ? (record.MaterialOverride ?? renderer.Material)?.PassSet : null;
                GpuMeshSubmissionRecord candidate = record with
                {
                    Renderer = renderer, Mesh = mesh, Material = material, Metadata = metadata,
                    RenderPass = renderPass,
                    CurrentWorld = currentWorld, PreviousWorld = previousWorld,
                    RequiresLodTransformPublication = unresolvedTransform,
                    MaterialBasePassDisabled = IsLodMaterialPassDisabled(passSet, EMaterialPassIdentity.Base),
                    MaterialShadowPassDisabled = IsLodMaterialPassDisabled(passSet, EMaterialPassIdentity.Shadow),
                    SourcePrimitiveCount = Math.Max(1, renderer.Submeshes.Count),
                    SourceBindings = bindings, BillboardMode = material.BillboardMode,
                    AuthoredPrimitiveInstanceCount = renderer.Submeshes.Count == 0 ? 1u : renderer.Submeshes[record.PrimitiveIndex].InstanceCount,
                    HasSkinning = mesh.HasSkinning, HasBlendshapes = mesh.HasBlendshapes,
                    MeshletPayload = payload, GeometryRevision = mesh.GeometryRevision,
                    PayloadValidationRevision = payload?.ValidationRevision ?? 0,
                    PayloadOwnerGeometryRevision = payload?.OwnerGeometryRevision ?? 0,
                    PayloadOwnerValidationToken = payload?.OwnerValidationToken ?? 0,
                };
                if (passSet?.TryGetPass(EMaterialPassIdentity.Outline, out MaterialPassDefinition outlinePass) == true && outlinePass.Enabled &&
                    outlinePass.ShaderBehavior == EngineMaterialSemanticIdentity.UberOutlineV1)
                {
                    XRMaterial outlineSource = record.MaterialOverride ?? renderer.Material!;
                    XRMaterial? outlineMaterial = outlineSource.OutlinePassVariant;
                    if (outlineMaterial?.EngineSemantic != EngineMaterialSemanticIdentity.UberOutlineV1)
                        throw new NotSupportedException("GPUScene.OutlineCompanionMissing: the modeled outline requires its exact cooked material variant.");
                    GpuMeshSubmissionSourceBindings outlineBindings = GpuMeshSubmissionSourceBindings.Capture(
                        mesh, renderer, outlineMaterial, _updatingMeshSubmissionOutlineBindings[bindingIndex]);
                    _updatingMeshSubmissionOutlineBindings[bindingIndex] = outlineBindings;
                    candidate = candidate with { OutlinePass = outlinePass, OutlineMaterial = outlineMaterial, OutlineSourceBindings = outlineBindings };
                }
                else _updatingMeshSubmissionOutlineBindings[bindingIndex] = null;
                destination.CaptureLodCandidate(recordIndex, level, in candidate);
            }
        }
        finally { Array.Clear(_meshSubmissionLodRenderers); }
    }

    private static bool TryResolveLodWorld(in Matrix4x4 sourceWorld, bool sourceSkinned, bool candidateSkinned,
        bool skinningEnabled, in Matrix4x4 componentWorld, out Matrix4x4 candidateWorld)
    {
        candidateWorld = sourceWorld;
        if (!skinningEnabled || sourceSkinned == candidateSkinned) return true;
        Matrix4x4 expectedSource = sourceSkinned ? Matrix4x4.Identity : componentWorld;
        if (sourceWorld != expectedSource) return false;
        candidateWorld = candidateSkinned ? Matrix4x4.Identity : componentWorld;
        return true;
    }

    private static bool IsLodMaterialPassDisabled(MaterialPassSet? passSet, EMaterialPassIdentity identity)
        => passSet is { Passes.Length: > 0 } && (!passSet.TryGetPass(identity, out MaterialPassDefinition pass) || !pass.Enabled);

    private void ClearUpdatingMeshSubmissionLods(uint recordIndex)
    {
        int start = checked((int)recordIndex * MaxLogicalMeshLodCount);
        if (start < _updatingMeshSubmissionLodBindings.Length)
            Array.Clear(_updatingMeshSubmissionLodBindings, start, MaxLogicalMeshLodCount);
        if (start < _updatingMeshSubmissionOutlineBindings.Length)
            Array.Clear(_updatingMeshSubmissionOutlineBindings, start, MaxLogicalMeshLodCount);
    }
}
