namespace XREngine.Benchmarks;

/// <summary>
/// Canonical scene, camera, runtime, and budget definition for one Vulkan lane.
/// </summary>
public sealed class VulkanPerformanceCohort
{
    public string Id { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public string SettingsPath { get; init; } = string.Empty;
    public string Scene { get; init; } = string.Empty;
    public string Camera { get; init; } = string.Empty;
    public string Lights { get; init; } = string.Empty;
    public string Viewport { get; init; } = string.Empty;
    public string RenderScale { get; init; } = string.Empty;
    public string Strategy { get; init; } = string.Empty;
    public string ZeroReadbackMaterialDrawPath { get; init; } = string.Empty;
    public string VrMode { get; init; } = string.Empty;
    public string FoveationMode { get; init; } = string.Empty;
    public bool RequireFoveation { get; init; }
    public string BudgetMetric { get; init; } = string.Empty;
    public double BudgetMilliseconds { get; init; }
    public double MinimumPrimaryReuseRatio { get; init; }
    public List<VulkanPerformanceOutputRequirement> RequiredOutputs { get; init; } = [];
    public bool Gate { get; init; }
}
