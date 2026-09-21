using XREngine.Components.Lights;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Builds triangle geometry for DDGI independently of the mesh submission strategy.</summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DDGIPrepareGeometryPass : VPRC_DDGIComputePass
{
    protected override bool ShouldExecuteThisFrame()
        => GlobalIlluminationPlanSelection.IsSelectedAndSupported(ActivePipelineInstance.Pipeline, EGlobalIlluminationMode.DDGI);

    protected override void ExecuteDDGI()
    {
        var pipeline = ActivePipelineInstance;
        var variables = pipeline.Variables;
        variables.Set("DDGIGeometryReady", false);
        var world = pipeline.RenderState.WindowViewport?.World ?? RuntimeEngine.Rendering.State.RenderingWorld;
        var context = DDGIFrameContext.Get(pipeline);
        if (world is null || !context.TryGetSelectedVolume(world, out var volume) || volume is null ||
            volume.UpdateMode == EDDGIUpdateMode.Baked || pipeline.RenderState.Scene is not VisualScene3D scene)
            return;
        var geometry = DDGIGeometryResources.GetOrCreate(pipeline, scene);
        bool ready;
        bool importsPublished = false;
        try
        {
            ready = geometry.Prepare(scene);
        }
        finally
        {
            // Preparation can queue material copies before its BVH is ready. Stage
            // partial allocations even when a later dispatch cannot run yet.
            importsPublished = DDGIResourceImports.BindAvailable(pipeline, geometry);
        }
        if (!ready)
        {
            Debug.RenderingWarningEvery("DDGI.GeometryPending", TimeSpan.FromSeconds(5),
                "DDGI geometry is not ready ({0}): {1}", geometry.Status, geometry.Diagnostic ?? "Pending GPU work");
            return;
        }
        if (!importsPublished)
        {
            Debug.RenderingEvery("DDGI.GeometryImportsPending", TimeSpan.FromSeconds(5),
                "DDGI geometry is waiting for frame-boundary import publication.");
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

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_DDGIPrepareGeometryPass), ERenderGraphPassStage.Compute);
        builder.ReadWriteBuffer("DDGIGeometryNodes");
        builder.ReadWriteBuffer("DDGIGeometryTriangles");
        builder.ReadWriteBuffer("DDGIGeometryMaterials");
        builder.ReadWriteBuffer("DDGIGeometryAttributes");
        builder.ReadWriteTexture(MakeTextureResource("DDGIMaterialTextures"));
    }
}
