using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Validation;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Runs the render pipeline validator for the Workbench target and caches the rows and counters. The validator runs
/// again only when the pipeline, the instance, the backend, the structure, the resources, or a reported source
/// failure changes. Views draw the cached rows.
/// </summary>
public sealed class RenderPipelineWorkbenchValidation
{
    private readonly List<RenderPipelineValidationRow> _rows = new(32);
    private readonly List<RenderPipelineValidationRow> _visible = new(32);
    private RenderPipeline? _pipeline;
    private XRRenderPipelineInstance? _instance;
    private string _backend = "\0";
    private int _structureBuild = -1;
    private int _resourceBuild = -1;
    private string? _generationFailure;
    private string? _declineReason;
    private bool _filterDirty = true;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showInfo = true;

    /// <summary>Gets the newest report.</summary>
    public RenderPipelineValidationReport Report { get; private set; } = RenderPipelineValidationReport.Empty;

    /// <summary>Gets the rows that pass the severity filter.</summary>
    public IReadOnlyList<RenderPipelineValidationRow> VisibleRows => _visible;

    /// <summary>Gets the problem counts as text, for example <c>2 errors, 5 warnings, 1 info</c>.</summary>
    public string SummaryText { get; private set; } = "No problems";

    /// <summary>Gets the short counter text of the target bar, or an empty string when there are no errors or warnings.</summary>
    public string CounterText { get; private set; } = string.Empty;

    /// <summary>Gets the number of validator runs, used by views to detect a new report.</summary>
    public int RunCount { get; private set; }

    /// <summary>Gets or sets whether the view shows errors.</summary>
    public bool ShowErrors
    {
        get => _showErrors;
        set => SetFilter(ref _showErrors, value);
    }

    /// <summary>Gets or sets whether the view shows warnings.</summary>
    public bool ShowWarnings
    {
        get => _showWarnings;
        set => SetFilter(ref _showWarnings, value);
    }

    /// <summary>Gets or sets whether the view shows information rows.</summary>
    public bool ShowInfo
    {
        get => _showInfo;
        set => SetFilter(ref _showInfo, value);
    }

    /// <summary>Runs the validator when an input changed, then applies the severity filter when needed.</summary>
    public void Update(RenderPipelineWorkbenchModel model, RendererBackendId backend)
    {
        RenderPipeline? pipeline = model.Pipeline;
        XRRenderPipelineInstance? instance = model.Instance;
        string? failure = instance?.LastResourceGenerationFailure;
        string? decline = instance?.LastRenderDeclineReason;
        bool changed =
            !ReferenceEquals(_pipeline, pipeline) ||
            !ReferenceEquals(_instance, instance) ||
            !string.Equals(_backend, backend.Value, StringComparison.Ordinal) ||
            _structureBuild != model.StructureBuildCount ||
            _resourceBuild != model.ResourceBuildCount ||
            !string.Equals(_generationFailure, failure, StringComparison.Ordinal) ||
            !string.Equals(_declineReason, decline, StringComparison.Ordinal);

        if (changed)
        {
            _pipeline = pipeline;
            _instance = instance;
            _backend = backend.Value;
            _structureBuild = model.StructureBuildCount;
            _resourceBuild = model.ResourceBuildCount;
            _generationFailure = failure;
            _declineReason = decline;
            Run(model, pipeline, instance, backend);
        }

        if (_filterDirty)
            ApplyFilter();
    }

    /// <summary>Forgets the cached report so the next update runs the validator again.</summary>
    public void Invalidate()
        => _structureBuild = -1;

    private void Run(RenderPipelineWorkbenchModel model, RenderPipeline? pipeline, XRRenderPipelineInstance? instance, RendererBackendId backend)
    {
        RunCount++;
        _rows.Clear();
        _filterDirty = true;
        if (pipeline is null)
        {
            Report = RenderPipelineValidationReport.Empty;
            UpdateCounters();
            return;
        }

        // Without a live generation the model has no resource snapshot. The validator then builds a layout for its
        // default profile, so the resource rules still run.
        RenderPipelineValidationContext context = model.Generation is null
            ? RenderPipelineValidationContext.FromPipeline(pipeline, instance, backend)
            : RenderPipelineValidationContext.FromSnapshots(pipeline, instance, model.Passes, model.Structure, model.Resources, backend);
        Report = RenderPipelineValidator.Validate(context);

        IReadOnlyList<RenderPipelineProblem> problems = Report.Problems;
        for (int i = 0; i < problems.Count; i++)
            _rows.Add(CreateRow(problems[i], model));

        UpdateCounters();
    }

    private static RenderPipelineValidationRow CreateRow(RenderPipelineProblem problem, RenderPipelineWorkbenchModel model)
    {
        string severity = problem.Severity switch
        {
            ERenderPipelineProblemSeverity.Error => "Error",
            ERenderPipelineProblemSeverity.Warning => "Warning",
            _ => "Info",
        };

        string targetText;
        RenderPipelineWorkbenchSelectionEntry target = RenderPipelineWorkbenchSelectionEntry.None;
        ERenderPipelineWorkbenchView? view = null;
        switch (problem.Target)
        {
            case ERenderPipelineProblemTarget.Command when problem.Command is not null:
                targetText = string.Concat("Command: ", RenderPipelineWorkbenchText.GetCommandLabel(problem.Command));
                target = RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(problem.Command));
                view = ERenderPipelineWorkbenchView.Flow;
                break;
            case ERenderPipelineProblemTarget.Pass when problem.PassIndex >= 0:
                targetText = string.Concat("Pass ", RenderPipelineWorkbenchText.FormatCount(problem.PassIndex), ": ", FindPassName(model, problem.PassIndex));
                target = RenderPipelineWorkbenchSelectionEntry.ForPass(problem.PassIndex);
                view = ERenderPipelineWorkbenchView.Passes;
                break;
            case ERenderPipelineProblemTarget.Resource when problem.Name is { Length: > 0 } resource:
                targetText = string.Concat("Resource: ", resource);
                target = RenderPipelineWorkbenchSelectionEntry.ForResource(resource);
                view = ERenderPipelineWorkbenchView.Resources;
                break;
            case ERenderPipelineProblemTarget.Stage when problem.Name is { Length: > 0 } stage:
                targetText = string.Concat("Stage: ", stage);
                target = RenderPipelineWorkbenchSelectionEntry.ForStage(stage);
                view = ERenderPipelineWorkbenchView.Settings;
                break;
            default:
                targetText = "Pipeline";
                break;
        }

        return new RenderPipelineValidationRow(problem.Severity, severity, problem.RuleId, problem.Message, targetText, target, view);
    }

    private static string FindPassName(RenderPipelineWorkbenchModel model, int passIndex)
    {
        foreach (RenderPassMetadata pass in model.Passes)
            if (pass.PassIndex == passIndex)
                return pass.Name;

        return "?";
    }

    private void UpdateCounters()
    {
        RenderPipelineValidationReport report = Report;
        if (report.Problems.Count == 0)
        {
            SummaryText = "No problems";
            CounterText = string.Empty;
            return;
        }

        SummaryText = string.Concat(
            RenderPipelineWorkbenchText.FormatCount(report.ErrorCount), report.ErrorCount == 1 ? " error, " : " errors, ",
            RenderPipelineWorkbenchText.FormatCount(report.WarningCount), report.WarningCount == 1 ? " warning, " : " warnings, ",
            RenderPipelineWorkbenchText.FormatCount(report.InfoCount), " info");
        CounterText = report.ErrorCount + report.WarningCount == 0
            ? string.Empty
            : string.Concat(
                "Problems: ",
                RenderPipelineWorkbenchText.FormatCount(report.ErrorCount), "E ",
                RenderPipelineWorkbenchText.FormatCount(report.WarningCount), "W");
    }

    private void ApplyFilter()
    {
        _filterDirty = false;
        _visible.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            RenderPipelineValidationRow row = _rows[i];
            bool show = row.Severity switch
            {
                ERenderPipelineProblemSeverity.Error => _showErrors,
                ERenderPipelineProblemSeverity.Warning => _showWarnings,
                _ => _showInfo,
            };

            if (show)
                _visible.Add(row);
        }
    }

    private void SetFilter(ref bool field, bool value)
    {
        if (field == value)
            return;

        field = value;
        _filterDirty = true;
    }
}
