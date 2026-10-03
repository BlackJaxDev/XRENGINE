using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    /// <summary>Supplies the canvas output's AA default without replacing an authored selection.</summary>
    private static void ConfigureCanvasResourceProfileDefaults(GameStartupSettings settings)
    {
        // Match cold publication's existing browser default only when neither
        // packaged setting selects a mode. The assigned pipeline admits that mode.
        if (!settings.AntiAliasingModeOverride.HasOverride &&
            !settings.DefaultUserSettings.AntiAliasingModeOverride.HasOverride)
        {
            settings.AntiAliasingModeOverride.SetOverride(EAntiAliasingMode.None);
        }
    }

    private RenderPipelineResourceProfile GetCanvasOutputProfile(XRCamera camera)
    {
        RuntimeEngine.Rendering.EngineSettings defaults = RuntimeEngine.Rendering.Settings;
        return new RenderPipelineResourceProfile(
            checked((uint)Math.Max(1, _renderViewport?.Width ?? _canvasWidth)),
            checked((uint)Math.Max(1, _renderViewport?.Height ?? _canvasHeight)),
            checked((uint)Math.Max(1, _renderViewport?.InternalWidth ?? _canvasWidth)),
            checked((uint)Math.Max(1, _renderViewport?.InternalHeight ?? _canvasHeight)),
            camera.OutputHDROverride ?? defaults.OutputHDR,
            camera.AntiAliasingModeOverride ?? OverrideableSettingExtensions.ResolveValueCascade(defaults.AntiAliasingMode,
                Engine.GameSettings?.AntiAliasingModeOverride, Engine.UserSettings?.AntiAliasingModeOverride),
            camera.MsaaSampleCountOverride ?? OverrideableSettingExtensions.ResolveValueCascade(defaults.MsaaSampleCount,
                Engine.GameSettings?.MsaaSampleCountOverride, Engine.UserSettings?.MsaaSampleCountOverride),
            Stereo: false, ExternalTargetKind: RenderPipelineExternalTargetKind.Window);
    }

    private static string? GetCanvasVendorOperationRejection()
    {
        RuntimeEngine.Rendering.EngineSettings defaults = RuntimeEngine.Rendering.Settings;
        return BrowserRenderPipelineOutputProfile.GetVendorOperationRejection(
            OverrideableSettingExtensions.ResolveValueCascade(defaults.EnableNvidiaDlss,
                Engine.GameSettings?.EnableNvidiaDlssOverride, Engine.UserSettings?.EnableNvidiaDlssOverride),
            OverrideableSettingExtensions.ResolveValueCascade(defaults.EnableIntelXess,
                Engine.GameSettings?.EnableIntelXessOverride, Engine.UserSettings?.EnableIntelXessOverride),
            OverrideableSettingExtensions.ResolveValueCascade(defaults.EnableNvidiaDlssFrameGeneration,
                Engine.GameSettings?.EnableNvidiaDlssFrameGenerationOverride, Engine.UserSettings?.EnableNvidiaDlssFrameGenerationOverride));
    }
}
