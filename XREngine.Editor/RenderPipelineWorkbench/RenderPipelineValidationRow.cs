using XREngine.Rendering.Pipelines.Validation;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>One problem row of the Validation view, with the text and the navigation target that the view draws.</summary>
/// <param name="Severity">The severity of the problem.</param>
/// <param name="SeverityText">The severity as text.</param>
/// <param name="RuleId">The rule or source that reported the problem.</param>
/// <param name="Message">The problem text.</param>
/// <param name="TargetText">The object that the problem refers to, as text.</param>
/// <param name="Target">The selection that "go to" makes, or <see cref="RenderPipelineWorkbenchSelectionEntry.None"/>.</param>
/// <param name="TargetView">The view that "go to" shows, or null to keep the current view.</param>
public sealed record RenderPipelineValidationRow(
    ERenderPipelineProblemSeverity Severity,
    string SeverityText,
    string RuleId,
    string Message,
    string TargetText,
    RenderPipelineWorkbenchSelectionEntry Target,
    ERenderPipelineWorkbenchView? TargetView)
{
    /// <summary>Gets whether the row has a navigation target.</summary>
    public bool HasTarget => Target.Kind != ERenderPipelineWorkbenchSelectionKind.None;
}
