namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Runtime configuration queries supplied by the installed OpenXR backend.</summary>
public static class OpenXrRuntimeSettings
{
    public const float MinPoseTimeOffsetMs = -20.0f;
    public const float MaxPoseTimeOffsetMs = 20.0f;

    private static IOpenXrRuntimeConfiguration? _configuration;

    private static IOpenXrRuntimeConfiguration Configuration
        => Volatile.Read(ref _configuration)
            ?? throw new InvalidOperationException("OpenXR runtime configuration backend has not been registered.");

    public static bool CanChangeRuntimeConfiguration
        => Configuration.CanChangeRuntimeConfiguration;

    public static string? RuntimeConfigurationChangeFailureReason
        => Configuration.RuntimeConfigurationChangeFailureReason;

    public static void ClearVulkanRuntimeRequirementsCache()
        => Configuration.ClearVulkanRuntimeRequirementsCache();

    public static void Register(IOpenXrRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Volatile.Write(ref _configuration, configuration);
    }
}
