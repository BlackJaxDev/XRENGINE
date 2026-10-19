using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Info;
using XREngine.Components.Scene.Mesh;

namespace XREngine.Rendering.Commands;

/// <summary>Checks one complete mesh command before shared shadow ownership is frozen.</summary>
internal static class SharedOpaqueShadowEligibility
{
    internal static bool SupportsRenderPass(int renderPass)
        => renderPass is (int)EDefaultRenderPass.OpaqueDeferred
            or (int)EDefaultRenderPass.OpaqueForward
            or (int)EDefaultRenderPass.MaskedForward;

    internal static bool CanOwn(
        XRMeshRenderer? renderer,
        XRMaterial? materialOverride,
        RenderingParameters? renderOptionsOverride,
        bool forceCpuRendering,
        RenderInfo? renderInfo,
        in GpuSceneOwnerSnapshot owner,
        bool shadowPassEnabled,
        int renderPass)
    {
        if (!SupportsRenderPass(renderPass) ||
            !owner.Is3D || !owner.CastsShadows || !shadowPassEnabled ||
            forceCpuRendering || renderOptionsOverride is not null ||
            renderer is null || renderer.MeshDeformEnabled ||
            renderer.HasSettingUniformsHandlers || renderer.HasRenderDataPreparation)
            return false;

        RenderableMesh? renderable = (renderInfo as RenderInfo3D)?.OwnerRenderableMesh ??
            renderInfo?.Owner as RenderableMesh;
        int count = Math.Max(1, renderer.Submeshes.Count);
        for (int index = 0; index < count; index++)
        {
            if (renderable?.CollectLodRenderers((uint)index, Span<XRMeshRenderer?>.Empty) > 1)
                return false;
            if (!renderer.TryGetMesh(index, out XRMesh? mesh, out XRMaterial? sourceMaterial) ||
                mesh is null || mesh.Type != EPrimitiveType.Triangles ||
                mesh.Lines is { Count: > 0 } || mesh.Points is { Count: > 0 } ||
                mesh.HasSkinning || mesh.HasBlendshapes || mesh.SupportsBillboarding)
                return false;

            XRMaterial? material = materialOverride ?? sourceMaterial;
            if (material is null ||
                (material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV1 &&
                 material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1 &&
                 material.EngineSemantic != EngineMaterialSemanticIdentity.UnlitColorV1) ||
                material.BillboardMode != EMeshBillboardMode.None ||
                material.HasSettingVertexUniformHandlers ||
                material.RenderOptions?.CullMode is not (null or ECullMode.Back) ||
                material.RenderOptions?.ExcludeFromGpuIndirect == true ||
                !material.CanUseSharedOpaqueShadowMaterial())
                return false;
        }

        return true;
    }
}
