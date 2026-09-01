namespace XREngine.Components.Animation;

/// <summary>Result of calculating required corpus coverage from executable observations.</summary>
public sealed class HumanoidConformanceCoverageEvaluationResult
{
    public HumanoidConformanceCapability RequiredCoverage { get; set; }
    public HumanoidConformanceCapability ObservedCoverage { get; set; }
    public HumanoidConformanceCapability MissingCoverage { get; set; }
    public List<string> Failures { get; set; } = [];

    public bool Passed => MissingCoverage == HumanoidConformanceCapability.None && Failures.Count == 0;
}
