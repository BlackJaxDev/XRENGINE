using XREngine.Rendering.Commands;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using System;
using System.Threading;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRC_RenderMeshesPassTraditional
{
    private static int _forbiddenFallbackLogBudget = 8;
    private static int _excludedGpuFallbackLogBudget = 16;
    private static int _cpuSafetyNetLogBudget = 8;

    public static void Execute(VPRC_RenderMeshesPassShared command)
    {
        if (command.MeshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect)
            RenderGPU(command);
        else
            RenderCPU(command);
    }

    private static void RenderGPU(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        using var prof = RuntimeEngine.Profiler.Start("VPRC_RenderMeshesPassTraditional.RenderGPU", ProfilerScopeKind.AlwaysOnHotPathLoop);
        var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
        {
            WarnMissingPipeline(nameof(RenderGPU), command.RenderPass);
            return;
        }

        // Render only the commands the GPU dispatch path cannot own:
        // non-mesh debug/UI/decal commands and mesh commands flagged
        // ExcludeFromGpuIndirect / ForceCpuRendering. Bypasses the full
        // RenderCPU machinery (CPU occlusion BeginPass, per-mesh skip
        // iteration, fallback warning budget) which is pure overhead when
        // the GPU is authoritative for mesh visibility.
        using (RuntimeEngine.Profiler.Start("VPRC_RenderMeshesPassTraditional.RenderGPU.NonMeshPrefilter", ProfilerScopeKind.AlwaysOnHotPathLoop))
            activeInstance.MeshRenderCommands.RenderCPUNonMeshAndExcluded(command.RenderPass);

        using (RuntimeEngine.Profiler.Start("VPRC_RenderMeshesPassTraditional.RenderGPU.Dispatch", ProfilerScopeKind.AlwaysOnHotPathLoop))
            activeInstance.MeshRenderCommands.RenderGPU(command.RenderPass, command.MeshSubmissionStrategy);

        if (activeInstance.MeshRenderCommands.TryGetGpuPass(command.RenderPass, out var gpuPass))
        {
            if (ShouldUseOpenGLZeroReadbackProgramWarmupFallback(command.MeshSubmissionStrategy, gpuPass))
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);
                WarnZeroReadbackProgramWarmupFallback(command.RenderPass, gpuPass.ZeroReadbackProgramPendingCountThisFrame);
                activeInstance.MeshRenderCommands.RenderCPUMeshOnly(command.RenderPass);
                return;
            }

            if (gpuPass.VisibleCommandCount != 0)
                return;

            bool shaderWarmupFallback = ShouldUseOpenGLShaderWarmupFallback(command.MeshSubmissionStrategy);
            bool allowCpuSafetyNet = shaderWarmupFallback || IsExplicitCpuFallbackAllowed();

            if (allowCpuSafetyNet)
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);
                WarnCpuSafetyNetFallback(command.RenderPass, shaderWarmupFallback);
                activeInstance.MeshRenderCommands.RenderCPUMeshOnly(command.RenderPass);
            }
            else
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(1);
                if (Interlocked.Decrement(ref _forbiddenFallbackLogBudget) >= 0)
                {
                    XREngine.Debug.LogWarning(
                        $"[GPU-PIPELINE] Render pass {command.RenderPass} produced zero visible GPU commands; CPU mesh safety-net suppressed for policy {GetCpuSafetyNetPolicyName()}.");
                }
            }
        }
    }

    private static void RenderCPU(VPRC_RenderMeshesPassShared command)
    {
        using var passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(command.RenderPass);
        var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (activeInstance is null)
        {
            WarnMissingPipeline(nameof(RenderCPU), command.RenderPass);
            return;
        }

        var camera = activeInstance.RenderState.SceneCamera
            ?? activeInstance.RenderState.RenderingCamera
            ?? activeInstance.LastSceneCamera
            ?? activeInstance.LastRenderingCamera;
        activeInstance.MeshRenderCommands.RenderCPU(command.RenderPass, false, camera);
    }

    private static void WarnMissingPipeline(string path, int renderPass)
        => XREngine.Debug.RenderingWarningEvery(
            $"RenderMeshesPassTraditional.MissingPipeline.{path}.{renderPass}",
            TimeSpan.FromSeconds(5),
            "[RenderDiag] Skipping {0} for pass {1}: no active render pipeline instance.",
            path,
            renderPass);

    private static bool IsExplicitCpuFallbackAllowed()
    {
        bool fallbackRequested = (RuntimeEngine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
            || (RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging && RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback);

        if (!fallbackRequested)
            return false;

        if (!IsActiveRendererVulkan())
            return true;

        return !VulkanFeatureProfile.EnforceStrictNoFallbacks
            && (!VulkanFeatureProfile.IsActive ||
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics);
    }

    private static bool ShouldUseOpenGLShaderWarmupFallback(EMeshSubmissionStrategy strategy)
        => strategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback
           && IsActiveRendererOpenGL()
           && OpenGLRenderer.GLRenderProgram.HasPendingAsyncPrograms;

    private static bool ShouldUseOpenGLZeroReadbackProgramWarmupFallback(
        EMeshSubmissionStrategy strategy,
        GPURenderPassCollection gpuPass)
        => strategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback
           && IsActiveRendererOpenGL()
           && gpuPass.ZeroReadbackProgramPendingThisFrame;

    private static bool IsActiveRendererOpenGL()
        => AbstractRenderer.Current is OpenGLRenderer
           || RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport?.Window?.Renderer is OpenGLRenderer;

    private static bool IsActiveRendererVulkan()
        => AbstractRenderer.Current is VulkanRenderer
           || RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport?.Window?.Renderer is VulkanRenderer;

    private static string GetCpuSafetyNetPolicyName()
    {
        if (IsActiveRendererVulkan())
            return VulkanFeatureProfile.ActiveProfile.ToString();

        return IsActiveRendererOpenGL()
            ? "OpenGL"
            : "current renderer";
    }

    private static void WarnExcludedGpuFallback(int renderPass, IRenderCommandMesh meshCmd)
    {
        if (Interlocked.Decrement(ref _excludedGpuFallbackLogBudget) < 0)
            return;

        string meshName = meshCmd.Mesh?.Mesh?.Name ?? "<unnamed-mesh>";
        string materialName = (meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material)?.Name ?? "<unnamed-material>";
        XREngine.Debug.LogWarning(
            $"[GPU-PIPELINE] Render pass {renderPass} skipped CPU fallback for mesh '{meshName}' with material '{materialName}' because GPU render dispatch is enabled and ExcludeFromGpuIndirect is set.");
    }

    private static void WarnCpuSafetyNetFallback(int renderPass, bool shaderWarmupFallback)
    {
        if (Interlocked.Decrement(ref _cpuSafetyNetLogBudget) < 0)
            return;

        string reason = shaderWarmupFallback
            ? "OpenGL shader/program warmup is still pending"
            : "GPU CPU fallback diagnostics are enabled";

        XREngine.Debug.LogWarning(
            $"[GPU-PIPELINE] Render pass {renderPass} produced zero visible GPU commands; CPU mesh safety-net fallback is running because {reason}.");
    }

    private static void WarnZeroReadbackProgramWarmupFallback(int renderPass, int pendingProgramCount)
    {
        if (Interlocked.Decrement(ref _cpuSafetyNetLogBudget) < 0)
            return;

        XREngine.Debug.LogWarning(
            $"[GPU-PIPELINE] Render pass {renderPass} deferred zero-readback GPU draws because {pendingProgramCount} OpenGL program(s) are still warming asynchronously; CPU mesh safety-net fallback is running for this frame.");
    }
}
