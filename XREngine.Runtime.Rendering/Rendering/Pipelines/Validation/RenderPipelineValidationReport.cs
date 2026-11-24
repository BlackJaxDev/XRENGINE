namespace XREngine.Rendering.Pipelines.Validation;

/// <summary>The result of one validator run.</summary>
public sealed class RenderPipelineValidationReport
{
    /// <summary>Gets a report with no problems.</summary>
    public static RenderPipelineValidationReport Empty { get; } = new([]);

    /// <summary>Initializes a report. Problems are sorted by severity, highest first, and keep their order otherwise.</summary>
    public RenderPipelineValidationReport(IReadOnlyList<RenderPipelineProblem> problems)
    {
        RenderPipelineProblem[] sorted = [.. problems];
        // A stable sort keeps the rule order inside each severity.
        int[] order = new int[sorted.Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int severity = sorted[b].Severity.CompareTo(sorted[a].Severity);
            return severity != 0 ? severity : a.CompareTo(b);
        });

        RenderPipelineProblem[] result = new RenderPipelineProblem[sorted.Length];
        for (int i = 0; i < order.Length; i++)
        {
            result[i] = sorted[order[i]];
            switch (result[i].Severity)
            {
                case ERenderPipelineProblemSeverity.Error:
                    ErrorCount++;
                    break;
                case ERenderPipelineProblemSeverity.Warning:
                    WarningCount++;
                    break;
                default:
                    InfoCount++;
                    break;
            }
        }

        Problems = result;
    }

    /// <summary>Gets the problems, errors first.</summary>
    public IReadOnlyList<RenderPipelineProblem> Problems { get; }

    /// <summary>Gets the number of errors.</summary>
    public int ErrorCount { get; }

    /// <summary>Gets the number of warnings.</summary>
    public int WarningCount { get; }

    /// <summary>Gets the number of hints.</summary>
    public int InfoCount { get; }

    /// <summary>Returns whether the report has a problem from <paramref name="ruleId"/>.</summary>
    public bool Has(string ruleId)
    {
        for (int i = 0; i < Problems.Count; i++)
            if (string.Equals(Problems[i].RuleId, ruleId, StringComparison.Ordinal))
                return true;

        return false;
    }
}
