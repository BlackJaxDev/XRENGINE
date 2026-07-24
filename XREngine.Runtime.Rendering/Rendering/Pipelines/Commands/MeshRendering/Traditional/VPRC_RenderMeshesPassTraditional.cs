using XREngine.Rendering.Commands;
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

    public static void Execute(VPRC_RenderMeshesPassShared command, EMeshSubmissionStrategy meshSubmissionStrategy)
    {
        if (meshSubmissionStrategy != EMeshSubmissionStrategy.CpuDirect)
            RenderGPU(command, meshSubmissionStrategy);
        else
            RenderCPU(command);
    }

    private static void RenderGPU(VPRC_RenderMeshesPassShared command, EMeshSubmissionStrategy meshSubmissionStrategy)
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
        RenderCommandCollection commands = activeInstance.ActiveMeshRenderCommands;
        using (RuntimeEngine.Profiler.Start("VPRC_RenderMeshesPassTraditional.RenderGPU.NonMeshPrefilter", ProfilerScopeKind.AlwaysOnHotPathLoop))
            commands.RenderCPUNonMeshAndExcluded(command.RenderPass);

        using (RuntimeEngine.Profiler.Start("VPRC_RenderMeshesPassTraditional.RenderGPU.Dispatch", ProfilerScopeKind.AlwaysOnHotPathLoop))
            commands.RenderGPU(command.RenderPass, meshSubmissionStrategy);

        if (commands.TryGetGpuPass(command.RenderPass, out var gpuPass))
        {
            if (gpuPass.GpuProgramsPendingThisFrame)
                return;

            if (ShouldUseOpenGLZeroReadbackProgramWarmupFallback(meshSubmissionStrategy, gpuPass))
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);
                WarnZeroReadbackProgramWarmupFallback(command.RenderPass, gpuPass.ZeroReadbackProgramPendingCountThisFrame);
                commands.RenderCPUMeshOnly(command.RenderPass);
                return;
            }

            if (gpuPass.VisibleCommandCount != 0)
                return;

            // The production zero-readback lane intentionally does not mirror the GPU-written
            // count back to the CPU. A zero CPU shadow can therefore mean either a legitimately
            // empty result or that the upper-bound publication is still crossing a frame boundary;
            // neither is evidence of a fallback attempt. Explicit scatter/program/submission
            // failures report their own forbidden-fallback event at the point of failure.
            if (meshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback)
                return;

            // An empty render pass is not a fallback event. Only apply the
            // safety-net policy when this pass actually contains meshes the
            // GPU submission path was expected to consume.
            if (!commands.HasGpuEligibleMeshCommands(command.RenderPass))
                return;

            bool shaderWarmupFallback = ShouldUseOpenGLShaderWarmupFallback(meshSubmissionStrategy);
            bool allowCpuSafetyNet = shaderWarmupFallback || IsExplicitCpuFallbackAllowed(meshSubmissionStrategy);

            if (allowCpuSafetyNet)
            {
                RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(1, 0);
                WarnCpuSafetyNetFallback(command.RenderPass, shaderWarmupFallback);
                commands.RenderCPUMeshOnly(command.RenderPass);
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
        activeInstance.ActiveMeshRenderCommands.RenderCPU(command.RenderPass, false, camera);
    }

    private static void WarnMissingPipeline(string path, int renderPass)
        => XREngine.Debug.RenderingWarningEvery(
            $"RenderMeshesPassTraditional.MissingPipeline.{path}.{renderPass}",
            TimeSpan.FromSeconds(5),
            "[RenderDiag] Skipping {0} for pass {1}: no active render pipeline instance.",
            path,
            renderPass);

    private static bool IsExplicitCpuFallbackAllowed(EMeshSubmissionStrategy strategy)
    {
        bool fallbackRequested = (RuntimeEngine.EditorPreferences?.Debug?.AllowGpuCpuFallback == true)
            || (RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging && RuntimeEngine.EffectiveSettings.EnableGpuIndirectCpuFallback);

        if (!fallbackRequested)
            return false;

        if (!IsActiveRendererVulkan())
            return true;

        if (strategy.IsGpuZeroReadbackStrategy())
        {
            string? explicitSafetyNet = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanAllowCpuMeshSafetyNet);
            return string.Equals(explicitSafetyNet, "1", StringComparison.OrdinalIgnoreCase);
        }

        return !VulkanFeatureProfile.EnforceStrictNoFallbacks
            && (!VulkanFeatureProfile.IsActive ||
                VulkanFeatureProfile.ActiveProfile == EVulkanGpuDrivenProfile.Diagnostics);
    }

    private static bool ShouldUseOpenGLShaderWarmupFallback(EMeshSubmissionStrategy strategy)
        => strategy.IsGpuZeroReadbackStrategy()
           && IsActiveRendererOpenGL()
           && AbstractRenderer.Current is IRuntimeRendererHost renderer
           && renderer.TryGetBackendCapability<IRendererStartupWarmupBackendCapability>(out var warmup)
           && warmup?.HasPendingAsyncPrograms == true;

    private static bool ShouldUseOpenGLZeroReadbackProgramWarmupFallback(
        EMeshSubmissionStrategy strategy,
        GPURenderPassCollection gpuPass)
        => strategy.IsGpuZeroReadbackStrategy()
           && IsActiveRendererOpenGL()
           && gpuPass.ZeroReadbackProgramPendingThisFrame;

    private static bool IsActiveRendererOpenGL()
        => AbstractRenderer.Current?.BackendId == RendererBackendId.OpenGL
           || RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport?.Window?.Renderer?.BackendId == RendererBackendId.OpenGL;

    private static bool IsActiveRendererVulkan()
        => AbstractRenderer.Current?.BackendId == RendererBackendId.Vulkan
           || RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.RenderState.WindowViewport?.Window?.Renderer?.BackendId == RendererBackendId.Vulkan;

    private static string GetCpuSafetyNetPolicyName()
    {
        if (IsActiveRendererVulkan())
            return $"Vulkan/{VulkanFeatureProfile.ActiveProfile} (set {XREngineEnvironmentVariables.VulkanAllowCpuMeshSafetyNet}=1 to opt into CPU mesh safety-net)";

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
