using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Components.Lights
{
    public class ShadowRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);
        private XRMaterial MakeInvalidMaterial()
        {
            Debug.Rendering("Generating invalid material");
            return XRMaterial.CreateColorMaterialDeferred();
        }

        protected override ViewportRenderCommandContainer GenerateCommandChain()
        {
            ViewportRenderCommandContainer c = [];

            c.Add<VPRC_SetClears>().Set(ColorF4.White, 1.0f, 0);
            c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PreRender;

            using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
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
}
