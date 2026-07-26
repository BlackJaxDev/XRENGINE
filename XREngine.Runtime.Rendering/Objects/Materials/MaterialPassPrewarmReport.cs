namespace XREngine.Rendering;

/// <summary>
/// Diagnostics emitted by one material pass-set prewarm operation.
/// </summary>
public sealed record MaterialPassPrewarmReport
{
    public MaterialPassPrewarmEntry[] Entries { get; init; } = [];
    public int RequestedPassCount { get; init; }
    public int PreparedPassCount { get; init; }
    public int FeatureCount { get; init; }
    public int SamplerCount { get; init; }
    public int GeneratedSourceLength { get; init; }
    public double PreparationMilliseconds { get; init; }
    public double CompileMilliseconds { get; init; }
    public double LinkMilliseconds { get; init; }
    public UberMaterialBindingPlan? OpenGlMinimumBindingPlan { get; init; }
    public UberMaterialBindingPlan? VulkanMinimumBindingPlan { get; init; }
}
