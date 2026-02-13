using XREngine.Rendering.Vulkan;
using System.Threading;

namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassTraditional
{
    private static int _forbiddenFallbackLogBudget = 8;

    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        if (command.GPUDispatch)
            RenderGPU(command);
        else
            RenderCPU(command);
    }

    private static void RenderGPU(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = ViewportRenderCommand.ActivePipelineInstance;
        var camera = activeInstance.RenderState.SceneCamera;

        activeInstance.MeshRenderCommands.RenderCPU(command.RenderPass, true, camera);
        activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass);

        if (activeInstance.MeshRenderCommands.TryGetGpuPass(command.RenderPass, out var gpuPass) && gpuPass.VisibleCommandCount == 0)
        {
            bool allowCpuSafetyNet = !VulkanFeatureProfile.EnforceStrictNoFallbacks &&
                (!VulkanFeatureProfile.IsActive ||
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics);

            if (allowCpuSafetyNet)
            {
                activeInstance.MeshRenderCommands.RenderCPUMeshOnly(command.RenderPass);
            }
            else
            {
                Engine.Rendering.Stats.RecordGpuCpuFallback(1, 0);
                Engine.Rendering.Stats.RecordForbiddenGpuFallback(1);
                if (Engine.EffectiveSettings.EnableGpuIndirectDebugLogging && Interlocked.Decrement(ref _forbiddenFallbackLogBudget) >= 0)
                {
                    XREngine.Debug.LogWarning(
                        $"[GPU-PIPELINE] Render pass {command.RenderPass} produced zero visible GPU commands; CPU mesh safety-net suppressed for profile {VulkanFeatureProfile.ActiveProfile}.");
                }
            }
        }
    }

    private static void RenderCPU(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = ViewportRenderCommand.ActivePipelineInstance;
        var camera = activeInstance.RenderState.SceneCamera;
        activeInstance.MeshRenderCommands.RenderCPU(command.RenderPass, false, camera);
    }
}
