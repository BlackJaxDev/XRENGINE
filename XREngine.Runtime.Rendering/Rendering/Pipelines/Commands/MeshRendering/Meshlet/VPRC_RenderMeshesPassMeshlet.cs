namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassMeshlet
{
    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = Engine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
            return;

        var camera = activeInstance.RenderState.SceneCamera
            ?? activeInstance.RenderState.RenderingCamera
            ?? activeInstance.LastSceneCamera
            ?? activeInstance.LastRenderingCamera;

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
