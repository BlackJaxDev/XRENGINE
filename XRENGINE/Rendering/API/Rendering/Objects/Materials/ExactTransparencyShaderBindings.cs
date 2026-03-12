using XREngine.Data.Rendering;

namespace XREngine.Rendering;

internal static class ExactTransparencyShaderBindings
{
    public static void ConfigureMaterialProgram(XRMaterialBase materialBase, XRRenderProgram program)
    {
        if (materialBase is not XRMaterial material)
            return;

        XRRenderPipelineInstance? pipelineInstance = Engine.Rendering.State.CurrentRenderingPipeline;
        if (pipelineInstance?.Pipeline is not DefaultRenderPipeline pipeline)
            return;

        switch (material.RenderPass)
        {
            case (int)EDefaultRenderPass.PerPixelLinkedListForward:
                ConfigurePpll(program, pipelineInstance, pipeline);
                break;
            case (int)EDefaultRenderPass.DepthPeelingForward:
                ConfigureDepthPeeling(program, pipelineInstance, pipeline);
                break;
        }
    }

    private static void ConfigurePpll(XRRenderProgram program, XRRenderPipelineInstance pipelineInstance, DefaultRenderPipeline pipeline)
    {
        XRTexture? headPointers = pipeline.PpllHeadPointerTexture;
        if (headPointers is null)
            return;

        pipeline.PpllNodeBuffer?.BindTo(program, 24u);
        pipeline.PpllCounterBuffer?.BindTo(program, 25u);
        program.BindImageTexture(0u, headPointers, 0, false, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.R32UI);
        float width = Math.Max(1, pipelineInstance.RenderState.WindowViewport?.InternalWidth ?? pipelineInstance.LastWindowViewport?.InternalWidth ?? 1);
        float height = Math.Max(1, pipelineInstance.RenderState.WindowViewport?.InternalHeight ?? pipelineInstance.LastWindowViewport?.InternalHeight ?? 1);
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
        program.Uniform("PpllMaxNodes", (int)pipeline.PpllMaxNodeCount);
    }

    private static void ConfigureDepthPeeling(XRRenderProgram program, XRRenderPipelineInstance pipelineInstance, DefaultRenderPipeline pipeline)
    {
        XRTexture? previousDepth = pipeline.PreviousDepthPeelDepthTexture;
        if (previousDepth is not null)
            program.Sampler("PrevPeelDepth", previousDepth, 8);

        float width = Math.Max(1, pipelineInstance.RenderState.WindowViewport?.InternalWidth ?? pipelineInstance.LastWindowViewport?.InternalWidth ?? 1);
        float height = Math.Max(1, pipelineInstance.RenderState.WindowViewport?.InternalHeight ?? pipelineInstance.LastWindowViewport?.InternalHeight ?? 1);
        program.Uniform("ScreenWidth", width);
        program.Uniform("ScreenHeight", height);
        program.Uniform("DepthPeelLayerIndex", pipeline.ActiveDepthPeelLayerIndex);
        program.Uniform("DepthPeelEpsilon", pipeline.ActiveDepthPeelingEpsilon);
    }
}
