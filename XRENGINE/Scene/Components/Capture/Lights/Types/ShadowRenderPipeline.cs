using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Components.Capture.Lights.Types
{
    public class ShadowRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(MakeInvalidMaterial, LazyThreadSafetyMode.PublicationOnly);
        private XRMaterial MakeInvalidMaterial()
        {
            Debug.Out("Generating invalid material");
            return XRMaterial.CreateColorMaterialDeferred();
        }

        protected override ViewportRenderCommandContainer GenerateCommandChain()
        {
            ViewportRenderCommandContainer c = [];

            c.Add<VPRC_SetClears>().Set(ColorF4.Transparent, 1.0f, 0);
            c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PreRender;

            using (c.AddUsing<VPRC_PushOutputFBORenderArea>())
            {
                using (c.AddUsing<VPRC_BindOutputFBO>())
                {
                    c.Add<VPRC_StencilMask>().Set(~0u);
                    c.Add<VPRC_ClearByBoundFBO>();
                    c.Add<VPRC_ColorMask>().Set(false, false, false, false);
                    c.Add<VPRC_DepthTest>().Enable = true;
                    c.Add<VPRC_DepthWrite>().Allow = true;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                    c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.TransparentForward;
                }
            }
            c.Add<VPRC_RenderMeshesPass>().RenderPass = (int)EDefaultRenderPass.PostRender;
            return c;
        }
        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
        {
            return new()
            {
                { -1, null }, //Prerender
                //{ 0, _nearToFarSorter }, //No background pass
                { 1, null }, //OpaqueDeferredLit
                //{ 2, _nearToFarSorter }, //No decals
                { 3, null }, //OpaqueForward
                { 4, null }, //TransparentForward
                //{ 5, _nearToFarSorter }, //No on top (UI)
                { 6, null } //Postrender
            };
        }
    }
}
