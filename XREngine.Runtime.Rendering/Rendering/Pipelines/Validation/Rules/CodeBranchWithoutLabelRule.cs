using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds branches with a code evaluator and no label. Tools, scripts, and forks name a code branch by its label.
/// </summary>
public sealed class CodeBranchWithoutLabelRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "flow.unlabelled-code-branch";

    /// <inheritdoc />
    public string Description => "A code branch has no label.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = context.Structure.Commands[i].Command;
            bool unlabelled = command switch
            {
                VPRC_IfElse ifElse => ifElse.Condition is null && ifElse.ConditionEvaluator is not null && string.IsNullOrWhiteSpace(ifElse.Label),
                VPRC_Switch switchCommand => switchCommand.Condition is null && switchCommand.SwitchEvaluator is not null && string.IsNullOrWhiteSpace(switchCommand.Label),
                _ => false,
            };

            if (!unlabelled)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Info,
                "This branch uses a code condition and has no Label. Add a Label so that tools and forks can name it.",
                command));
        }
    }
}
