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
}
