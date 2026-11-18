using System.IO;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// GPU timing scopes for primary-recorded work that the stage and fullscreen scopes do not cover:
/// generic compute dispatches (GPU skinning, auto exposure, physics chains and other compute
/// passes), the Advanced visibility preparation dispatches, and each render-graph pass from its
/// barriers to its end. The pass scopes use their own root ("Passes"), so their time is not
/// added to the stage roots; they overlap the "Advanced", "Stereo" and "Fullscreen" rows.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private const string PassesGpuProfilerRoot = "Passes";
    private const string ComputeGpuProfilerRoot = "Compute";
    private static readonly string[] AdvancedVisibilityPrepareGpuProfilerPath = ["Advanced", "Visibility", "Prepare"];
    private static readonly string[] AdvancedVisibilityLateFinalizeGpuProfilerPath = ["Advanced", "Visibility", "LateFinalize"];
    private static readonly string[] ComputeSecondaryRangeGpuProfilerPath = [ComputeGpuProfilerRoot, "SecondaryRange"];

    /// <summary>Profiler path "Compute > shader" of each compute program, built on first use.</summary>
    private readonly ConditionalWeakTable<VkRenderProgram, string[]> _computeDispatchGpuProfilerPaths = new();

    /// <summary>
    /// Profiler paths "Passes > pipeline > pass" for each pass-metadata collection, by pass index.
    /// The weak table drops the entries when a pipeline rebuilds its pass metadata.
    /// </summary>
    private readonly ConditionalWeakTable<IReadOnlyCollection<RenderPassMetadata>, Dictionary<int, string[]>> _passGpuProfilerPaths = new();

    private bool IsWorkGpuTimingActive
        => FrameTelemetry._vulkanGpuProfilerRecordingActive && RuntimeEngine.IsRenderThread;

    /// <summary>
    /// Starts the scope "Compute > shader" around one generic compute dispatch recorded in the
    /// primary. The shader name is the file name of the program's first compute shader.
    /// Frames without GPU timing get an empty scope.
    /// </summary>
    private VulkanGpuProfilerScope TryBeginComputeDispatchGpuProfilerScope(CommandBuffer commandBuffer, VkRenderProgram program)
    {
        if (!IsWorkGpuTimingActive)
            return default;

        if (!_computeDispatchGpuProfilerPaths.TryGetValue(program, out string[]? path))
        {
            path = [ComputeGpuProfilerRoot, ResolveComputeProgramProfilerName(program)];
            _computeDispatchGpuProfilerPaths.AddOrUpdate(program, path);
        }

        return TryBeginVulkanGpuProfilerScope(commandBuffer, path);
    }

    /// <summary>
    /// Starts the scope "Compute > SecondaryRange" around a planned range of non-graphics
    /// operations that the primary executes as one secondary command buffer.
    /// </summary>
    private VulkanGpuProfilerScope TryBeginComputeSecondaryRangeGpuProfilerScope(CommandBuffer commandBuffer)
        => IsWorkGpuTimingActive
            ? TryBeginVulkanGpuProfilerScope(commandBuffer, ComputeSecondaryRangeGpuProfilerPath)
            : default;

    /// <summary>Starts a scope with a static path when GPU timing records this frame.</summary>
    private VulkanGpuProfilerScope TryBeginWorkGpuProfilerScope(CommandBuffer commandBuffer, string[] path)
        => IsWorkGpuTimingActive
            ? TryBeginVulkanGpuProfilerScope(commandBuffer, path)
            : default;

    /// <summary>
    /// Opens the scope "Passes > pipeline > pass" for the pass that the primary enters. The
    /// scope stays open in the recording state until <see cref="EndPassGpuProfilerScope"/>,
    /// which runs when the pass ends. It starts before the pass barriers and ends after the
    /// render pass ends, so it includes the barriers, load, clear, store and resolve work.
    /// Both timestamps are written outside a render pass: inside a multiview render pass a
    /// timestamp also writes one zero query per extra view, which would overwrite the start of
    /// the first scope that this pass scope contains.
    /// </summary>
    private void BeginPassGpuProfilerScope(scoped ref PrimaryCommandBufferRecordingState state, int passIndex)
    {
        if (!IsWorkGpuTimingActive)
            return;

        string[]? path = ResolvePassGpuProfilerPath(in state.ActiveContext, passIndex);
        if (path is null)
            return;

        state.PassGpuProfilerScope = TryBeginVulkanGpuProfilerScope(state.CommandBuffer, path);
        state.PassGpuProfilerScopeActive = true;
    }

    /// <summary>Closes the open pass scope, if any. Call it only after the active render pass ended.</summary>
    private static void EndPassGpuProfilerScope(scoped ref PrimaryCommandBufferRecordingState state)
    {
        if (!state.PassGpuProfilerScopeActive)
            return;

        state.PassGpuProfilerScope.Dispose();
        state.PassGpuProfilerScope = default;
        state.PassGpuProfilerScopeActive = false;
    }

    /// <summary>
    /// Returns the cached path of a pass, or builds it once from the pass name and the pipeline
    /// type name ("AdvancedRenderPipelineStereo" for a stereo Advanced pipeline). Returns null
    /// when the context has no pass metadata for the index.
    /// </summary>
    private string[]? ResolvePassGpuProfilerPath(in FrameOpContext context, int passIndex)
    {
        if (context.PassMetadata is not { } metadata)
            return null;

        Dictionary<int, string[]> paths = _passGpuProfilerPaths.GetValue(metadata, static _ => new Dictionary<int, string[]>());
        if (paths.TryGetValue(passIndex, out string[]? path))
            return path;

        string? passName = null;
        foreach (RenderPassMetadata pass in metadata)
        {
            if (pass.PassIndex == passIndex)
            {
                passName = pass.Name;
                break;
            }
        }

        RenderPipeline? pipeline = context.PipelineInstance?.Pipeline;
        string pipelineName = pipeline is null ? "UnknownPipeline"
            : pipeline is AdvancedRenderPipeline { Stereo: true } ? pipeline.GetType().Name + "Stereo"
            : pipeline.GetType().Name;
        path = [PassesGpuProfilerRoot, pipelineName, string.IsNullOrEmpty(passName) ? $"Pass{passIndex}" : passName];
        paths[passIndex] = path;
        return path;
    }

    private static string ResolveComputeProgramProfilerName(VkRenderProgram program)
    {
        foreach (XRShader shader in program.Data.Shaders)
        {
            if (shader.Type != EShaderType.Compute)
                continue;
            string? source = shader.Source?.FilePath ?? shader.FilePath ?? shader.Name;
            if (!string.IsNullOrEmpty(source))
                return Path.GetFileNameWithoutExtension(source);
        }

        return string.IsNullOrEmpty(program.Data.Name) ? "UnnamedCompute" : program.Data.Name;
    }
}
