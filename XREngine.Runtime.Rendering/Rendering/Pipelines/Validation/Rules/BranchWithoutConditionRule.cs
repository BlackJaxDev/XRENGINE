using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds branch commands that have neither a data condition nor a code evaluator. Such a branch always takes the
/// false or default path. A fork of a code pipeline drops code conditions, so this rule lists each one to fix.
/// </summary>
public sealed class BranchWithoutConditionRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "flow.branch-without-condition";

    /// <inheritdoc />
    public string Description => "A branch has no condition.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < context.Structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = context.Structure.Commands[i].Command;
            bool missing = command switch
            {
                VPRC_IfElse ifElse => ifElse.Condition is null && ifElse.ConditionEvaluator is null,
                VPRC_Switch switchCommand => switchCommand.Condition is null && switchCommand.SwitchEvaluator is null,
                _ => false,
            };

            if (!missing)
                continue;

            problems.Add(RenderPipelineProblem.ForCommand(
                Id,
                ERenderPipelineProblemSeverity.Error,
                string.Concat(RenderPipelineProblemText.CommandName(command), " has no condition, so it always takes the same path. Set a data condition."),
                command));
        }
    }
}
