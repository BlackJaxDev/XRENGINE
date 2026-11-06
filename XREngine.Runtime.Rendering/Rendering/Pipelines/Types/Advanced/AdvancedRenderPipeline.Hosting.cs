using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Host entry points for an owning pipeline that executes this Advanced stage
/// family as its own frame. The owner keeps command execution, resource
/// generations, output reservations and the frame lifecycle; this definition
/// only supplies the command chain, resources, passes and post-process schema.
/// </summary>
public partial class AdvancedRenderPipeline
{
    /// <summary>
    /// Creates a definition-only Advanced family for an OpenXR eye host: one mono
    /// eye, or both eyes as one layered stereo target. The definition does not
    /// subscribe to runtime settings; the host copies features into it.
    /// </summary>
    internal static AdvancedRenderPipeline CreateOpenXrEyeStageFamilyDefinition(bool stereo)
        => new(
            stereo: stereo,
            capabilityResult: null,
            offscreenProfile: null,
            stageFamilyProfile: EAdvancedStageFamilyExecutionProfile.OpenXrTwoPassEye,
            subscribeToRuntimeSettings: false);

    /// <summary>
    /// Returns this family's command chain with every command rebound to
    /// <paramref name="executionOwner"/>, so resource lookup and output authoring
    /// stay with the owning instance.
    /// </summary>
    internal ViewportRenderCommandContainer CreateHostedCommandChain(RenderPipeline executionOwner)
    {
        ArgumentNullException.ThrowIfNull(executionOwner);
        ViewportRenderCommandContainer commands = GenerateCommandChain();
        commands.ParentPipeline = executionOwner;
        return commands;
    }

    internal void DescribeHostedResources(RenderPipelineResourceLayoutBuilder builder)
        => DescribeResources(builder);

    internal Dictionary<int, IComparer<RenderCommand>?> GetHostedPassIndicesAndSorters()
        => GetPassIndicesAndSorters();

    internal void DescribeHostedPostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
        => DescribePostProcessSchema(builder);
}
