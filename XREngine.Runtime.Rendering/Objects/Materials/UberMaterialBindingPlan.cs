namespace XREngine.Rendering;

/// <summary>
/// Result of selecting a faithful binding strategy for one uber variant.
/// </summary>
public sealed record UberMaterialBindingPlan
{
    public required EUberMaterialBindingRung Rung { get; init; }
    public required int SamplerCount { get; init; }
    public required int SampledImageCount { get; init; }
    public required int UniformBytes { get; init; }
    public string? FailureReason { get; init; }
    public bool IsSupported => Rung != EUberMaterialBindingRung.Unsupported;
}
