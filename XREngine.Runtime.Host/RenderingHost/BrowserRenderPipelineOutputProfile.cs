using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine;

/// <summary>Resolves browser publication selections solely from the packaged settings.</summary>
public static class BrowserRenderPipelineOutputProfile
{
    /// <summary>Captures the single canvas selection without consulting live engine or editor settings.</summary>
    public static RenderPipelineResourceProfile FromStartup(GameStartupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        UserSettings user = settings.DefaultUserSettings;
        GameWindowStartupSettings? window = settings.StartupWindows.Count == 1 ? settings.StartupWindows[0] : null;
        uint width = checked((uint)Math.Max(1, window?.Width ?? 1280));
        uint height = checked((uint)Math.Max(1, window?.Height ?? 720));
        return new RenderPipelineResourceProfile(width, height, width, height,
            OutputHDR: window?.OutputHDR ?? false,
            OverrideableSettingExtensions.ResolveValueCascade(EAntiAliasingMode.None,
                settings.AntiAliasingModeOverride, user.AntiAliasingModeOverride),
            OverrideableSettingExtensions.ResolveValueCascade(1u,
                settings.MsaaSampleCountOverride, user.MsaaSampleCountOverride),
            Stereo: false, ExternalTargetKind: RenderPipelineExternalTargetKind.Window);
    }

    /// <summary>Applies only explicit camera overrides to a captured output selection.</summary>
    public static RenderPipelineResourceProfile ForCamera(in RenderPipelineResourceProfile output, XRCamera? camera)
        => output with
        {
            AntiAliasingMode = camera?.AntiAliasingModeOverride ?? output.AntiAliasingMode,
            MsaaSampleCount = camera?.MsaaSampleCountOverride ?? output.MsaaSampleCount,
            OutputHDR = camera?.OutputHDROverride ?? output.OutputHDR,
        };

    /// <summary>Reports native vendor operations explicitly selected by the packaged project/user cascade.</summary>
    public static string? GetVendorOperationRejection(GameStartupSettings settings)
    {
        UserSettings user = settings.DefaultUserSettings;
        return GetVendorOperationRejection(
            OverrideableSettingExtensions.ResolveValueCascade(false, settings.EnableNvidiaDlssOverride, user.EnableNvidiaDlssOverride),
            OverrideableSettingExtensions.ResolveValueCascade(false, settings.EnableIntelXessOverride, user.EnableIntelXessOverride),
            OverrideableSettingExtensions.ResolveValueCascade(false, settings.EnableNvidiaDlssFrameGenerationOverride, user.EnableNvidiaDlssFrameGenerationOverride));
    }

    /// <summary>Checks captured selections; callers choose packaged or live output values explicitly.</summary>
    public static string? GetVendorOperationRejection(bool nvidiaDlss, bool intelXess, bool nvidiaFrameGeneration)
        => nvidiaDlss ? "Selected NVIDIA DLSS reconstruction requires a native vendor service unavailable in WebGPU."
            : intelXess ? "Selected Intel XeSS reconstruction requires a native vendor service unavailable in WebGPU."
            : nvidiaFrameGeneration ? "Selected NVIDIA DLSS frame generation requires a native vendor service unavailable in WebGPU."
            : null;
}
