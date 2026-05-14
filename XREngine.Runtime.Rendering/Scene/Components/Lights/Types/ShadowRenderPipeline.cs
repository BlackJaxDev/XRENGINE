using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Minimal render pipeline used by shadow cameras to collect caster depth into a light-owned target.
    /// </summary>
    public class ShadowRenderPipeline : RenderPipeline
    {
        /// <summary>
        /// Keeps an atlas tile render area intact instead of replacing it with the full output FBO area.
        /// </summary>
        internal bool PreserveExistingRenderArea { get; set; }

        /// <summary>
        /// Atlas tile rectangles that must each be cleared before a grouped indexed viewport/scissor draw.
        /// </summary>
        internal BoundingRectangle[]? IndexedClearRegions { get; set; }

        /// <summary>
        /// Number of valid entries in <see cref="IndexedClearRegions"/>.
        /// </summary>
        internal int IndexedClearRegionCount { get; set; }

        /// <summary>
        /// Clear color used by shadow targets that encode depth or moments in color attachments.
        /// </summary>
        internal ColorF4 ClearColor { get; set; } = ColorF4.White;

        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);

        private XRMaterial MakeInvalidMaterial()
            => XRMaterial.CreateColorMaterialDeferred();

        protected override ViewportRenderCommandContainer GenerateCommandChain()
        {
            ViewportRenderCommandContainer c = [];

            c.Add<VPRC_SetShadowClears>();
            c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PreRender;

            using (c.AddUsing<VPRC_PushShadowOutputFBORenderArea>())
            {
                // FBO clears honor depth/stencil write masks, so restore them before the bind auto-clear.
                c.Add<VPRC_StencilMask>().Set(~0u);
                c.Add<VPRC_DepthTest>().Enable = true;
                c.Add<VPRC_DepthWrite>().Allow = true;

                using (c.AddUsing<VPRC_BindOutputFBO>(t => t.SetOptions(write: true, clearColor: false, clearDepth: false, clearStencil: false)))
                {
                    c.Add<VPRC_ClearShadowOutputFBO>();
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.MaskedForward;
                }
            }
            c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PostRender;
            return c;
        }

        protected override void DescribeRenderPasses(RenderPassMetadataCollection metadata)
        {
            base.DescribeRenderPasses(metadata);

            static void Chain(RenderPassMetadataCollection collection, EDefaultRenderPass pass, params EDefaultRenderPass[] dependencies)
            {
                var builder = collection.ForPass((int)pass, pass.ToString(), ERenderGraphPassStage.Graphics);
                foreach (var dependency in dependencies)
                    builder.DependsOn((int)dependency);
            }

            Chain(metadata, EDefaultRenderPass.PreRender);
            Chain(metadata, EDefaultRenderPass.OpaqueDeferred, EDefaultRenderPass.PreRender);
            Chain(metadata, EDefaultRenderPass.OpaqueForward, EDefaultRenderPass.OpaqueDeferred);
            Chain(metadata, EDefaultRenderPass.MaskedForward, EDefaultRenderPass.OpaqueForward);
            Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.MaskedForward);
        }

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        {
            // Include all standard passes so that render commands targeting any pass
            // are accepted into the collection (even if the command chain doesn't
            // render them). This prevents MISSING_PASS warnings when scene traversal
            // submits Background (skybox), DeferredDecals, or OnTopForward (debug
            // gizmo) commands to shadow pipeline collections.
            return new()
            {
                { (int)EDefaultRenderPass.PreRender, null },
                { (int)EDefaultRenderPass.Background, null },
                { (int)EDefaultRenderPass.OpaqueDeferred, null },
                { (int)EDefaultRenderPass.DeferredDecals, null },
                { (int)EDefaultRenderPass.OpaqueForward, null },
                { (int)EDefaultRenderPass.MaskedForward, null },
                { (int)EDefaultRenderPass.WeightedBlendedOitForward, null },
                { (int)EDefaultRenderPass.PerPixelLinkedListForward, null },
                { (int)EDefaultRenderPass.DepthPeelingForward, null },
                { (int)EDefaultRenderPass.TransparentForward, null },
                { (int)EDefaultRenderPass.OnTopForward, null },
                { (int)EDefaultRenderPass.PostRender, null }
            };
        }
    }

    [RenderPipelineScriptCommand]
    internal sealed class VPRC_SetShadowClears : ViewportRenderCommand
    {
        /// <summary>
        /// Applies the clear values requested by the active shadow pipeline before the shadow FBO is cleared.
        /// </summary>
        protected override void Execute()
        {
            ColorF4 clearColor = ActivePipelineInstance.Pipeline is ShadowRenderPipeline shadowPipeline
                ? shadowPipeline.ClearColor
                : ColorF4.White;

            RuntimeEngine.Rendering.State.ClearColor(clearColor);
            RuntimeEngine.Rendering.State.ClearDepth(1.0f);
            RuntimeEngine.Rendering.State.ClearStencil(0);
        }
    }

    [RenderPipelineScriptCommand]
    internal sealed class VPRC_ClearShadowOutputFBO : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (ActivePipelineInstance.Pipeline is not ShadowRenderPipeline shadowPipeline ||
                shadowPipeline.IndexedClearRegions is not { } regions ||
                shadowPipeline.IndexedClearRegionCount <= 0)
            {
                RuntimeEngine.Rendering.State.ClearByBoundFBO();
                return;
            }

            int count = Math.Min(shadowPipeline.IndexedClearRegionCount, regions.Length);
            var renderer = AbstractRenderer.Current;
            for (int i = 0; i < count; i++)
            {
                BoundingRectangle region = regions[i];
                if (region.Width <= 0 || region.Height <= 0)
                    continue;

                renderer?.SetRenderArea(region);
                renderer?.SetCroppingEnabled(true);
                renderer?.CropRenderArea(region);
                RuntimeEngine.Rendering.State.ClearByBoundFBO();
            }

            // Clearing uses scissor box 0, so the loop above temporarily overwrites viewport/scissor index 0.
            // Restore the indexed tile state before the grouped draw commands run.
            renderer?.SetIndexedViewportScissors(regions.AsSpan(0, count), regions.AsSpan(0, count));
        }
    }

    [RenderPipelineScriptCommand]
    internal sealed class VPRC_PushShadowOutputFBORenderArea : ViewportStateRenderCommand<VPRC_PopRenderArea>
    {
        /// <summary>
        /// Pushes a full-FBO render area unless an atlas tile has already established a cropped region.
        /// </summary>
        protected override void Execute()
        {
            XRRenderPipelineInstance instance = ActivePipelineInstance;
            var fbo = instance.RenderState.OutputFBO;
            if (fbo is null)
            {
                PopCommand.ShouldExecute = false;
                return;
            }

            if (instance.Pipeline is ShadowRenderPipeline { PreserveExistingRenderArea: true } &&
                instance.RenderState.CurrentRenderRegion.Width > 0 &&
                instance.RenderState.CurrentRenderRegion.Height > 0)
            {
                PopCommand.ShouldExecute = false;
                return;
            }

            instance.RenderState.PushRenderArea((int)fbo.Width, (int)fbo.Height);
        }
    }
}
