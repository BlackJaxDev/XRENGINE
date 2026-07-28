namespace XREngine.Benchmarks.SelfIteration;

/// <summary>
/// Controls editor builds, warmup, capture, profiling, reload validation, and retries.
/// </summary>
public sealed class SelfIterationMeasurementConfiguration
{
    public string Configuration { get; set; } = "Release";
    public string ProfileMode { get; set; } = "CleanProfile";
    public string CacheMode { get; set; } = "Warm";
    public int WarmupSeconds { get; set; } = 25;
    public int CaptureSeconds { get; set; } = 30;
    public int Repetitions { get; set; } = 3;
    public int StabilityWindowSeconds { get; set; } = 5;
    public int StabilityTimeoutSeconds { get; set; } = 120;
    public int ShutdownGraceSeconds { get; set; } = 20;
    public int NoSampleHangSeconds { get; set; } = 15;
    public int MaxLaunchAttempts { get; set; } = 2;
    public bool RunDetailedDiagnosticCapture { get; set; } = true;
    public string DiagnosticProfileMode { get; set; } = "DevelopmentProfile";
    public int DiagnosticWarmupSeconds { get; set; } = 15;
    public int DiagnosticCaptureSeconds { get; set; } = 15;
    public int DiagnosticRepetitions { get; set; } = 1;
    public bool DiagnosticGpuTimestampDense { get; set; } = true;
    public bool DiagnosticVulkanCommandBufferLabels { get; set; }
    public string ZeroReadbackMaterialDrawPath { get; set; } = "FullBucketScan";
    public string UnitTestVrMode { get; set; } = "Desktop";
    public string VulkanRenderTargetMode { get; set; } = "Configured";
    public string VulkanPrimaryReuse { get; set; } = "Configured";
    public string VulkanCommandChains { get; set; } = "Configured";
    public string VulkanParallelCommandChainRecording { get; set; } = "Configured";
    public string VulkanParallelSecondaryRecording { get; set; } = "Configured";
    public string OcclusionCullingMode { get; set; } = "Configured";
    public string VulkanDiagnosticPreset { get; set; } = "Configured";
    public bool VulkanCommandBufferLabels { get; set; }
    public bool GpuTimestampDense { get; set; }
    public string GpuClockPolicy { get; set; } = "Unspecified";
    public double TargetRefreshHz { get; set; }
    public string ProfileScene { get; set; } = string.Empty;
    public string ProfileCamera { get; set; } = string.Empty;
    public string ProfileLights { get; set; } = string.Empty;
    public string ProfileViewport { get; set; } = string.Empty;
    public string RenderScale { get; set; } = string.Empty;
    public bool NoRestore { get; set; } = true;
    public bool ValidateHotReload { get; set; } = true;
    public bool CaptureScreenshotAfterReload { get; set; } = true;
    public int ReloadFirstFrameTimeoutMilliseconds { get; set; } = 30000;
    public string[] AdditionalMeasureArguments { get; set; } = [];

    internal void Validate()
    {
        if (Configuration is not ("Debug" or "Release"))
            throw new InvalidDataException("Measurement.Configuration must be Debug or Release.");
        if (ProfileMode is not ("Diagnostics" or "DevelopmentProfile" or "CleanProfile" or "ReleaseBenchmark"))
            throw new InvalidDataException("Measurement.ProfileMode is invalid.");
        if (DiagnosticProfileMode is not ("Diagnostics" or "DevelopmentProfile" or "CleanProfile" or "ReleaseBenchmark"))
            throw new InvalidDataException("Measurement.DiagnosticProfileMode is invalid.");
        if (CacheMode is not ("Cold" or "Warm"))
            throw new InvalidDataException("Measurement.CacheMode must be Cold or Warm.");
        if (WarmupSeconds < 0 || CaptureSeconds < 1 || Repetitions < 1)
            throw new InvalidDataException("WarmupSeconds must be non-negative; CaptureSeconds and Repetitions must be positive.");
        if (StabilityWindowSeconds < 1 || StabilityTimeoutSeconds < 1 ||
            ShutdownGraceSeconds < 1 || NoSampleHangSeconds < 0)
        {
            throw new InvalidDataException(
                "Stability and shutdown durations must be positive; NoSampleHangSeconds cannot be negative.");
        }
        if (MaxLaunchAttempts is < 1 or > 10)
            throw new InvalidDataException("MaxLaunchAttempts must be between 1 and 10.");
        if (DiagnosticWarmupSeconds < 0 || DiagnosticCaptureSeconds < 1 ||
            DiagnosticRepetitions < 1)
        {
            throw new InvalidDataException(
                "DiagnosticWarmupSeconds must be non-negative; diagnostic capture and repetitions must be positive.");
        }
        if (ReloadFirstFrameTimeoutMilliseconds is < 1000 or > 120000)
            throw new InvalidDataException("ReloadFirstFrameTimeoutMilliseconds must be between 1000 and 120000.");
        if (TargetRefreshHz < 0)
            throw new InvalidDataException("TargetRefreshHz cannot be negative.");

        ValidateValue(
            nameof(ZeroReadbackMaterialDrawPath),
            ZeroReadbackMaterialDrawPath,
            "FullBucketScan",
            "ActiveBucketList",
            "MaterialTable",
            "BindlessMaterialTable");
        ValidateValue(
            nameof(UnitTestVrMode),
            UnitTestVrMode,
            "Configured",
            "Desktop",
            "Emulated",
            "MonadoOpenXR",
            "OpenVR",
            "OpenXR");
        ValidateValue(
            nameof(VulkanRenderTargetMode),
            VulkanRenderTargetMode,
            "Configured",
            "DynamicRendering",
            "LegacyRenderPass");
        ValidateTriState(nameof(VulkanPrimaryReuse), VulkanPrimaryReuse);
        ValidateTriState(nameof(VulkanCommandChains), VulkanCommandChains);
        ValidateTriState(
            nameof(VulkanParallelCommandChainRecording),
            VulkanParallelCommandChainRecording);
        ValidateTriState(
            nameof(VulkanParallelSecondaryRecording),
            VulkanParallelSecondaryRecording);
        ValidateValue(
            nameof(OcclusionCullingMode),
            OcclusionCullingMode,
            "Configured",
            "Disabled",
            "CpuQueryAsync",
            "CpuSoftwareOcclusion",
            "GpuHiZ");
        ValidateValue(
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

        if (ProfileMode is "CleanProfile" or "ReleaseBenchmark" &&
            (GpuTimestampDense || VulkanCommandBufferLabels ||
             VulkanDiagnosticPreset is not ("Configured" or "Off")))
        {
            throw new InvalidDataException(
                $"{ProfileMode} does not permit dense GPU timestamps, command-buffer labels, or Vulkan diagnostic layers.");
        }
        if (RunDetailedDiagnosticCapture &&
            DiagnosticProfileMode is "CleanProfile" or "ReleaseBenchmark" &&
            (DiagnosticGpuTimestampDense || DiagnosticVulkanCommandBufferLabels ||
             VulkanDiagnosticPreset is not ("Configured" or "Off")))
        {
            throw new InvalidDataException(
                $"{DiagnosticProfileMode} does not permit the configured detailed diagnostic instrumentation.");
        }
    }

    private static void ValidateTriState(string property, string value)
        => ValidateValue(property, value, "Configured", "Enabled", "Disabled");

    private static void ValidateValue(
        string property,
        string value,
        params string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Measurement.{property} has invalid value '{value}'.");
        }
    }
}
