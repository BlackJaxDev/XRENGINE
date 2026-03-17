using XREngine.Data.Core;
using XREngine.Rendering.Commands;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands
{
    internal static class VPRCRenderTargetHelpers
    {
        internal static RenderCommandCollection EnsureCollection(XRRenderPipelineInstance pipeline, ref RenderCommandCollection? collection)
        {
            if (collection is not null)
                return collection;

            collection = new RenderCommandCollection();
            if (pipeline.Pipeline is not null)
                collection.SetRenderPasses(pipeline.Pipeline.PassIndicesAndSorters, pipeline.Pipeline.PassMetadata);
            collection.SetOwnerPipeline(pipeline);
            return collection;
        }

        internal static void CollectVisible(
            XRRenderPipelineInstance pipeline,
            RenderCommandCollection collection,
            XRCamera camera,
            bool cullWithFrustum,
            bool collectMirrors = false)
        {
            if (pipeline.RenderState.Scene is not VisualScene scene)
                return;

            scene.CollectRenderedItems(collection, camera, cullWithFrustum, null, null, collectMirrors);
        }

        internal static void RenderPass(
            XRRenderPipelineInstance pipeline,
            RenderCommandCollection collection,
            XRCamera camera,
            int renderPass,
            int width,
            int height,
            bool gpuDispatch = false)
        {
            using var areaScope = pipeline.RenderState.PushRenderArea(width, height);
            using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(renderPass);
            using var cameraScope = pipeline.RenderState.PushRenderingCamera(camera);
            if (gpuDispatch)
                collection.RenderGPU(renderPass);
            else
                collection.RenderCPU(renderPass, false, camera);
        }

        internal static StateObject PushSceneCapturePass()
        {
            bool previous = Engine.Rendering.State.IsSceneCapturePass;
            Engine.Rendering.State.IsSceneCapturePass = true;
            return StateObject.New(() => Engine.Rendering.State.IsSceneCapturePass = previous);
        }
    }
}