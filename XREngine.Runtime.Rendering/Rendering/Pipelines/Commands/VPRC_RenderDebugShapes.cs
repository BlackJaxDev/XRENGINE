using XREngine.Data.Rendering;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderDebugShapes : ViewportRenderCommand
    {
        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            XRRenderPipelineInstance instance = ActivePipelineInstance;
            XRCamera? camera = instance.RenderState.SceneCamera
                ?? instance.RenderState.RenderingCamera
                ?? instance.LastSceneCamera
                ?? instance.LastRenderingCamera;

            using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.OnTopForward))
            using (instance.RenderState.PushRenderingCamera(camera))
            {
                RenderEnabledSpatialTreeDebug(instance, camera);
                RuntimeEngine.Rendering.Debug.RenderShapes();
            }
        }

        private static void RenderEnabledSpatialTreeDebug(XRRenderPipelineInstance instance, XRCamera? camera)
        {
            if (instance.RenderState.Scene is VisualScene3D scene3d && Is3DSpatialTreePreviewEnabled(instance))
            {
                scene3d.DebugRenderSpatialTreeNodes(camera);
                return;
            }

            if (instance.RenderState.Scene is VisualScene2D scene2d && Is2DSpatialTreePreviewEnabled(instance))
                scene2d.DebugRenderSpatialTreeNodes(camera);
        }

        private static bool Is3DSpatialTreePreviewEnabled(XRRenderPipelineInstance instance)
            => RuntimeRenderingHostServices.Current.Preview3DWorldOctree
            || ResolveWorld(instance)?.PreviewOctrees == true;

        private static bool Is2DSpatialTreePreviewEnabled(XRRenderPipelineInstance instance)
            => RuntimeRenderingHostServices.Current.Preview2DWorldQuadtree
            || ResolveWorld(instance)?.PreviewQuadtrees == true;

        private static IRuntimeRenderWorld? ResolveWorld(XRRenderPipelineInstance instance)
            => instance.RenderState.WindowViewport?.World ?? RuntimeEngine.Rendering.State.RenderingWorld;
    }
}
