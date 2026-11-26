using XREngine;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models;
using XREngine.Scene;

namespace StaticMeshletParity;

/// <summary>Checks the saved source identities and the Editor-cooked meshlet owner after hydration.</summary>
public static class StaticMeshletParityWorldContract
{
    public static readonly Guid MeshId = Guid.Parse("56f211b4-9743-4823-a5d7-5811788e65b7");
    public static readonly Guid MaterialId = Guid.Parse("567bd906-0f69-425b-b2cc-147a21fa72df");
    private static readonly Guid PipelineId = Guid.Parse("6ae570dc-6307-49df-8102-068df6766081");

    public static void Validate(XRWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        List<ModelComponent> models = [];
        List<CameraComponent> cameras = [];
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
            {
                models.AddRange(root.FindAllDescendantComponents<ModelComponent>());
                cameras.AddRange(root.FindAllDescendantComponents<CameraComponent>());
            }
        if (models.Count != 1 || cameras.Count != 1 ||
            cameras[0].RenderPipelineSource is not DefaultRenderPipeline { ID: var id } || id != PipelineId)
            throw new InvalidDataException("StaticMeshletParity.Scene: expected one static model and the saved Default camera pipeline.");

        Model? model = models[0].Model;
        if (model is null || model.Meshes.Count != 1)
            throw new InvalidDataException("StaticMeshletParity.Model: expected one saved submesh.");
        SubMesh subMesh = model.Meshes[0];
        if (subMesh.LODs.Count != 1 || !subMesh.MeshOptimizer.Meshlets.Enabled)
            throw new InvalidDataException("StaticMeshletParity.LOD: expected one enabled authored resident LOD.");
        SubMeshLOD lod = subMesh.LODs.Min
            ?? throw new InvalidDataException("StaticMeshletParity.LOD: the saved LOD is missing.");
        XRMesh mesh = lod.Mesh ?? throw new InvalidDataException("StaticMeshletParity.Mesh: source mesh is missing.");
        XRMaterial material = lod.Material ?? throw new InvalidDataException("StaticMeshletParity.Material: source material is missing.");
        if (mesh.ID != MeshId || material.ID != MaterialId || mesh.Name != "Static Mapped Panel" ||
            material.Name != "Mapped Checker Surface" ||
            material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitTextureV1 ||
            material.RenderPass != (int)EDefaultRenderPass.OpaqueDeferred)
            throw new InvalidDataException("StaticMeshletParity.SourceIdentity: saved mesh or mapped material changed.");

        if (!XRRuntimeEnvironment.IsPublishedBuild)
            return;
        MeshletPayload? payload = mesh.MeshletPayload;
        if (mesh.Triangles is null || payload is null || !payload.HasMeshlets || !payload.IsValidatedFor(mesh) ||
            payload.State != MeshletPayloadState.Present || !payload.MeshletSettings.Enabled ||
            payload.SourceTriangleCount != mesh.Triangles.Count)
            throw new InvalidDataException("StaticMeshletParity.CookedPayload: the published mesh has no owner-validated native meshlet payload.");
    }
}
