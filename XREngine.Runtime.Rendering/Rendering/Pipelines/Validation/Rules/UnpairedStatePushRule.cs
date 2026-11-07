using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>Finds state push commands whose pop command is not later in the same container.</summary>
public sealed class UnpairedStatePushRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "command.unpaired-push";

    /// <inheritdoc />
    public string Description => "A state push has no pop later in the same container.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            RenderPipelineStructureCommand entry = context.Structure.Commands[i];
            if (entry.Command is not ViewportStateRenderCommandBase push)
                continue;

            ViewportRenderCommandContainer container = entry.Container;
            ViewportPopStateRenderCommand pop = push.PairedPopCommand;
            bool found = false;
            for (int c = entry.IndexInContainer + 1; c < container.Count; c++)
            {
                if (ReferenceEquals(container[c], pop))
                {
                    found = true;
                    break;
                }
            }

            if (found)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat(RenderCommandInfo.Get(push).DisplayName, " pushes state, but its pop command is not later in the same container. The state stays for the rest of the frame. Use GetUsingState or add the pop."),
                push));
        }
    }
}
