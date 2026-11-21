using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds commands that need a code delegate in a pipeline that saves as data. A data pipeline cannot save the
/// delegate, so the command does nothing after a reload.
/// </summary>
public sealed class CodeCommandInDataPipelineRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "command.requires-code";

    /// <inheritdoc />
    public string Description => "A data pipeline uses a command that needs code.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Pipeline is not CustomRenderPipeline)
            return;

        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = context.Structure.Commands[i].Command;
            if (!RenderCommandInfo.Get(command).RequiresCode)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Error,
                string.Concat(RenderPipelineProblemText.CommandName(command), " needs a code delegate, which a data pipeline cannot save. Remove it or replace it with a data command."),
                command));
        }
    }
}
