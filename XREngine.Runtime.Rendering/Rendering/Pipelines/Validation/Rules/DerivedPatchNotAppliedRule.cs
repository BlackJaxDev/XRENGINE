using XREngine.Rendering.Pipelines.Overlays;

namespace XREngine.Rendering.Pipelines.Validation.Rules;

/// <summary>
/// Finds derived pipeline patches that did not apply: a missing anchor or a failed patch is an error, and a renamed
/// anchor is a warning. A part with an error does not apply, so the base renders that part.
/// </summary>
public sealed class DerivedPatchNotAppliedRule : IRenderPipelineValidationRule
{
    /// <inheritdoc />
    public string Id => "derived.patch-not-applied";

    /// <inheritdoc />
    public string Description => "A derived pipeline patch did not find its anchor or failed to apply.";

    /// <inheritdoc />
    public void Evaluate(RenderPipelineValidationContext context, List<RenderPipelineProblem> problems)
    {
        if (context.Pipeline is not { IsDerived: true } pipeline)
            return;

        RenderPipelineOverlayReport report = pipeline.OverlayReport;
        AddProblems(report.CommandResults, problems);
        AddProblems(report.ResourceResults, problems);
        AddProblems(report.SchemaResults, problems);
    }

    private void AddProblems(IReadOnlyList<RenderPipelinePatchResult> results, List<RenderPipelineProblem> problems)
    {
        for (int i = 0; i < results.Count; i++)
        {
            RenderPipelinePatchResult result = results[i];
            ERenderPipelineProblemSeverity severity;
            string fix;
            switch (result.Status)
            {
                case ERenderPipelinePatchStatus.AnchorMissing:
                    severity = ERenderPipelineProblemSeverity.Error;
                    fix = " Retarget the patch in the Patches tab, or revert it.";
                    break;
                case ERenderPipelinePatchStatus.Failed:
                    severity = ERenderPipelineProblemSeverity.Error;
                    fix = " Fix the patch in the Patches tab, or revert it.";
                    break;
                case ERenderPipelinePatchStatus.AnchorMoved:
                    severity = ERenderPipelineProblemSeverity.Warning;
                    fix = " Accept the rebase to keep the new id.";
                    break;
                default:
                    continue;
            }

            problems.Add(new RenderPipelineProblem(
                Id,
                severity,
                string.Concat("Overlay ", (result.OverlayIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ": ",
                    result.PatchText, ": ", result.Message, fix)));
        }
    }
}
