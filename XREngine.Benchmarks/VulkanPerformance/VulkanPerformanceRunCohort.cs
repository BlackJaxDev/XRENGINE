namespace XREngine.Benchmarks;

/// <summary>
/// Result location and immutable settings identity for one captured cohort.
/// </summary>
public sealed class VulkanPerformanceRunCohort
{
    public string Id { get; init; } = string.Empty;
    public string SummaryPath { get; init; } = string.Empty;
    public string SettingsPath { get; init; } = string.Empty;
    public string SettingsSha256 { get; init; } = string.Empty;
    public string Lane { get; init; } = string.Empty;
    public string Scene { get; init; } = string.Empty;
    public string CameraTrajectory { get; init; } = string.Empty;
    public string Lights { get; init; } = string.Empty;
    public string Viewport { get; init; } = string.Empty;
    public string RenderScale { get; init; } = string.Empty;
    public string SubmissionStrategy { get; init; } = string.Empty;
    public string FeatureStack { get; init; } = string.Empty;
    public string PresentationProfile { get; init; } = string.Empty;
    public double TargetRefreshHz { get; init; }
    public string VrMode { get; init; } = string.Empty;
    public string OpenXrRuntime { get; init; } = string.Empty;
}
