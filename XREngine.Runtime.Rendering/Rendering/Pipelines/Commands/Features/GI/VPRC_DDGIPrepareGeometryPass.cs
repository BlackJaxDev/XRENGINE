using XREngine.Components.Lights;
using XREngine.Rendering.GI.DDGI;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Builds triangle geometry for DDGI independently of the mesh submission strategy.</summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DDGIPrepareGeometryPass : VPRC_DDGIComputePass
{
    protected override bool ShouldExecuteThisFrame()
        => ActivePipelineInstance.Pipeline is IGlobalIlluminationPipelineProvider { UsesDDGI: true };

    protected override void ExecuteDDGI()
    {
        var pipeline = ActivePipelineInstance;
        var variables = pipeline.Variables;
        variables.Set("DDGIGeometryReady", false);
        var world = pipeline.RenderState.WindowViewport?.World ?? RuntimeEngine.Rendering.State.RenderingWorld;
        if (world is null || !DDGIVolumeComponent.Registry.TryGetFirstActive(world, out var volume) || volume is null ||
            volume.UpdateMode == EDDGIUpdateMode.Baked || pipeline.RenderState.Scene is not VisualScene3D scene)
            return;
        var geometry = DDGIGeometryResources.GetOrCreate(pipeline, scene);
        bool ready;
        try
        {
            ready = geometry.Prepare(scene);
        }
        finally
        {
            // Preparation can queue material copies before its BVH is ready. Publish
            // partial allocations even when a later dispatch cannot run yet.
            DDGIResourceImports.BindAvailable(pipeline, geometry);
        }
        if (!ready)
        {
            Debug.RenderingWarningEvery("DDGI.GeometryPending", TimeSpan.FromSeconds(5),
                "DDGI geometry is not ready ({0}): {1}", geometry.Status, geometry.Diagnostic ?? "Pending GPU work");
            return;
        }
        variables.Set("DDGIGeometryReady", true);
        variables.Set("DDGIGeometryNodeCount", geometry.NodeCount);
        variables.Set("DDGIGeometryTriangleCount", geometry.TriangleCount);
        variables.Set("DDGIGeometryMaterialCount", geometry.MaterialCount);
        variables.Set("DDGIGeometryRootIndex", geometry.RootIndex);
        variables.SetBuffer("DDGIGeometryNodes", geometry.Nodes!);
        variables.SetBuffer("DDGIGeometryTriangles", geometry.Triangles!);
        variables.SetBuffer("DDGIGeometryMaterials", geometry.Materials!);
        variables.SetBuffer("DDGIGeometryAttributes", geometry.Attributes!);
        variables.SetTexture("DDGIMaterialTextures", geometry.MaterialTextures!);
    }
}
