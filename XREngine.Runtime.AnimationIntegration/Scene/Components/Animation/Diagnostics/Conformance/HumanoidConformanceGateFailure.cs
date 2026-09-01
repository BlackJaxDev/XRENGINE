namespace XREngine.Components.Animation;

/// <summary>One failed numeric or explicit Phase 10 gate.</summary>
public sealed class HumanoidConformanceGateFailure
{
    public string Gate { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public float Actual { get; set; }
    public float Limit { get; set; }
}
