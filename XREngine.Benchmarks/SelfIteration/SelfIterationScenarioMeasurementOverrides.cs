namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Overrides shared measurement controls for one scenario in a campaign matrix.
/// </summary>
public sealed class SelfIterationScenarioMeasurementOverrides
{
    public string? ZeroReadbackMaterialDrawPath { get; set; }
    public string? UnitTestVrMode { get; set; }
    public string? VulkanRenderTargetMode { get; set; }
    public string? VulkanPrimaryReuse { get; set; }
    public string? VulkanCommandChains { get; set; }
    public string? VulkanParallelCommandChainRecording { get; set; }
    public string? VulkanParallelSecondaryRecording { get; set; }
    public string? OcclusionCullingMode { get; set; }
    public string? VulkanDiagnosticPreset { get; set; }
    public bool? VulkanCommandBufferLabels { get; set; }
    public bool? GpuTimestampDense { get; set; }
    public string? GpuClockPolicy { get; set; }
    public double? TargetRefreshHz { get; set; }
    public string? ProfileScene { get; set; }
    public string? ProfileCamera { get; set; }
    public string? ProfileLights { get; set; }
    public string? ProfileViewport { get; set; }
    public string? RenderScale { get; set; }
    public string[] AdditionalMeasureArguments { get; set; } = [];

    internal void Validate(string scenarioName, string profileMode)
    {
        ValidateOptional(
            scenarioName,
            nameof(ZeroReadbackMaterialDrawPath),
            ZeroReadbackMaterialDrawPath,
            "FullBucketScan",
            "ActiveBucketList",
            "MaterialTable",
            "BindlessMaterialTable");
        ValidateOptional(
            scenarioName,
            nameof(UnitTestVrMode),
            UnitTestVrMode,
            "Configured",
            "Desktop",
            "Emulated",
            "MonadoOpenXR",
            "OpenVR",
            "OpenXR");
        ValidateOptional(
            scenarioName,
            nameof(VulkanRenderTargetMode),
            VulkanRenderTargetMode,
            "Configured",
            "DynamicRendering",
            "LegacyRenderPass");
        ValidateTriState(scenarioName, nameof(VulkanPrimaryReuse), VulkanPrimaryReuse);
        ValidateTriState(scenarioName, nameof(VulkanCommandChains), VulkanCommandChains);
        ValidateTriState(
            scenarioName,
            nameof(VulkanParallelCommandChainRecording),
            VulkanParallelCommandChainRecording);
        ValidateTriState(
            scenarioName,
            nameof(VulkanParallelSecondaryRecording),
            VulkanParallelSecondaryRecording);
        ValidateOptional(
            scenarioName,
            nameof(OcclusionCullingMode),
            OcclusionCullingMode,
            "Configured",
            "Disabled",
            "CpuQueryAsync",
            "CpuSoftwareOcclusion",
            "GpuHiZ");
        ValidateOptional(
            scenarioName,
            nameof(VulkanDiagnosticPreset),
            VulkanDiagnosticPreset,
            "Configured",
            "Off",
            "StandardValidation",
            "SyncValidation",
            "GpuAssisted",
            "BestPractices",
            "CrashDiagnostics",
            "RenderDocFriendly");
        if (TargetRefreshHz is <= 0)
        {
            throw new InvalidDataException(
                $"Scenario '{scenarioName}' TargetRefreshHz must be positive when specified.");
        }
        if (profileMode is "CleanProfile" or "ReleaseBenchmark" &&
            (GpuTimestampDense == true || VulkanCommandBufferLabels == true ||
             VulkanDiagnosticPreset is not (null or "Configured" or "Off")))
        {
            throw new InvalidDataException(
                $"Scenario '{scenarioName}' enables diagnostics that {profileMode} does not permit.");
        }
    }

    private static void ValidateTriState(
        string scenarioName,
        string property,
        string? value)
        => ValidateOptional(
            scenarioName,
            property,
            value,
            "Configured",
            "Enabled",
            "Disabled");

    private static void ValidateOptional(
        string scenarioName,
        string property,
        string? value,
        params string[] allowed)
    {
        if (value is null)
            return;
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Scenario '{scenarioName}' has invalid {property} '{value}'.");
        }
    }
}
