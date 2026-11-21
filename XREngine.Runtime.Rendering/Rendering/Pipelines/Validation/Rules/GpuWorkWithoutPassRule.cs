using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds commands that do GPU work but describe no render pass.</summary>
public sealed class GpuWorkWithoutPassRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "command.gpu-without-pass";

    /// <inheritdoc />
    public string Description => "A command does GPU work but declares no render pass.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = context.Structure.Commands[i];
            if (!entry.IsGpuWorkWithoutPass)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Error,
                string.Concat(RenderCommandInfo.Get(entry.Command).DisplayName, " does GPU work but declares no pass. Vulkan does not order or synchronize it."),
                entry.Command));
        }
    }
}
