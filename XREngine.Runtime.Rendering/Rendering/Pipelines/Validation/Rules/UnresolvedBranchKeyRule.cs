using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds data conditions whose key does not resolve: a pipeline setting that the pipeline does not register, or a
/// stage that the schema does not declare. Such a condition reads as false.
/// </summary>
public sealed class UnresolvedBranchKeyRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "flow.unresolved-branch-key";

    /// <inheritdoc />
    public string Description => "A branch condition names a setting or a stage that does not exist.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = context.Structure.Commands[i].Command;
            RenderBranchCondition? condition = command switch
            {
                VPRC_IfElse ifElse => ifElse.Condition,
                VPRC_Switch switchCommand => switchCommand.Condition,
                _ => null,
            };

            if (condition is null)
                continue;

            string? problem = condition.Source switch
            {
                ERenderBranchConditionSource.PipelineSetting when context.Pipeline is { } pipeline && !pipeline.BranchSettings.Contains(condition.Key)
                    => string.Concat("The pipeline registers no branch setting named '", condition.Key, "'."),
                ERenderBranchConditionSource.StageEnabled when !context.Schema.TryGetStage(condition.Key, out _)
                    => string.Concat("The schema declares no stage '", condition.Key, "'."),
                _ => null,
            };

            if (problem is null)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Warning,
                string.Concat(RenderPipelineProblemText.CommandName(command), ": ", problem, " The condition reads as false."),
                command));
        }
    }
}
