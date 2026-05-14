namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassMeshlet
{
    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
            return;

        if (!activeInstance.MeshRenderCommands.TryGetGpuPass(command.RenderPass, out var gpuPass))
            return;

        bool previousValue = gpuPass.UseMeshletPipeline;
        gpuPass.UseMeshletPipeline = true;
        try
        {
            activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass, command.MeshSubmissionStrategy);
        }
        finally
        {
            gpuPass.UseMeshletPipeline = previousValue;
        }
    }
}
