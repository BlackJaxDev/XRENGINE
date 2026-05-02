using XREngine.Data.Colors;
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
                using (c.AddUsing<VPRC_BindOutputFBO>())
                {
                    c.Add<VPRC_StencilMask>().Set(~0u);
                    c.Add<VPRC_ClearByBoundFBO>();
                    c.Add<VPRC_DepthTest>().Enable = true;
                    c.Add<VPRC_DepthWrite>().Allow = true;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.MaskedForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.WeightedBlendedOitForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PerPixelLinkedListForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.DepthPeelingForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
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
            Chain(metadata, EDefaultRenderPass.TransparentForward, EDefaultRenderPass.MaskedForward);
            Chain(metadata, EDefaultRenderPass.WeightedBlendedOitForward, EDefaultRenderPass.TransparentForward);
            Chain(metadata, EDefaultRenderPass.PerPixelLinkedListForward, EDefaultRenderPass.WeightedBlendedOitForward);
            Chain(metadata, EDefaultRenderPass.DepthPeelingForward, EDefaultRenderPass.PerPixelLinkedListForward);
            Chain(metadata, EDefaultRenderPass.PostRender, EDefaultRenderPass.DepthPeelingForward);
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

            Engine.Rendering.State.ClearColor(clearColor);
            Engine.Rendering.State.ClearDepth(1.0f);
            Engine.Rendering.State.ClearStencil(0);
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
