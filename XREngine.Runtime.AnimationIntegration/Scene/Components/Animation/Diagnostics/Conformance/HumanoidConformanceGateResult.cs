namespace XREngine.Components.Animation;

/// <summary>Machine-readable evaluation of one comparison report against a matrix row.</summary>
public sealed class HumanoidConformanceGateResult
{
    public string MatrixCaseId { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public List<HumanoidConformanceGateFailure> Failures { get; set; } = [];
}
