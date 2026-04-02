namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassMeshlet
{
    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = ViewportRenderCommand.ActivePipelineInstance;
        var camera = activeInstance.RenderState.SceneCamera;

        activeInstance.MeshRenderCommands.RenderCPU(
            command.RenderPass,
            true,
            camera,
            allowExcludedGpuFallbackMeshes: false);

        if (!activeInstance.MeshRenderCommands.TryGetGpuPass(command.RenderPass, out var gpuPass))
            return;

        bool previousValue = gpuPass.UseMeshletPipeline;
        gpuPass.UseMeshletPipeline = true;
        try
        {
            activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass);
        }
        finally
        {
            gpuPass.UseMeshletPipeline = previousValue;
        }
    }
}
