using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassMeshlet
{
    private static int _cpuSafetyNetLogBudget = 8;

    public static void Execute(VPRC_RenderMeshesPassShared command, EMeshSubmissionStrategy meshSubmissionStrategy)
    {
        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
            return;

        RenderCommandCollection commands = activeInstance.ActiveMeshRenderCommands;
        if (!commands.TryGetGpuPass(command.RenderPass, out var gpuPass))
            return;

        bool previousValue = gpuPass.UseMeshletPipeline;
        gpuPass.UseMeshletPipeline = true;
        try
        {
            commands.RenderGPU(command.RenderPass, meshSubmissionStrategy);

            if (ShouldUseOpenGLMeshletProgramWarmupFallback(meshSubmissionStrategy, gpuPass))
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);
                WarnMeshletProgramWarmupFallback(command.RenderPass, gpuPass.ZeroReadbackProgramPendingCountThisFrame);
                commands.RenderCPUMeshOnly(command.RenderPass);
            }
        }
        finally
        {
            gpuPass.UseMeshletPipeline = previousValue;
        }
    }

    private static bool ShouldUseOpenGLMeshletProgramWarmupFallback(
        EMeshSubmissionStrategy strategy,
        GPURenderPassCollection gpuPass)
        => strategy.IsAnyMeshletStrategy()
           && IsActiveRendererOpenGL()
           && gpuPass.ZeroReadbackProgramPendingThisFrame;

    private static bool IsActiveRendererOpenGL()
        => AbstractRenderer.Current is OpenGLRenderer
           || RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport?.Window?.Renderer is OpenGLRenderer;

    private static void WarnMeshletProgramWarmupFallback(int renderPass, int pendingProgramCount)
    {
        if (Interlocked.Decrement(ref _cpuSafetyNetLogBudget) < 0)
            return;

        XREngine.Debug.LogWarning(
            $"[GPU-PIPELINE] Meshlet render pass {renderPass} deferred GPU meshlet draws because {pendingProgramCount} OpenGL program(s) are still warming asynchronously; CPU mesh safety-net fallback is running for this frame.");
    }
}
