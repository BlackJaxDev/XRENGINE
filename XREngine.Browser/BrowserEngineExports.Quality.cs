using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    internal static string? RequestedCanvasQualityPreset { get; private set; }

    /// <summary>Consumes one startup selection, including on a headless start.</summary>
    internal static string? TakeRequestedCanvasQualityPreset()
    {
        string? preset = RequestedCanvasQualityPreset;
        RequestedCanvasQualityPreset = null;
        return preset;
    }

    /// <summary>Selects a named browser quality policy before starting an authored engine world.</summary>
    [JSExport]
    public static void SetCanvasQualityPreset(string preset)
    {
        if (_session is not null || _loading is not null)
            throw new InvalidOperationException("WebGPU.Quality.StartupOnly: stop the active browser engine world before changing quality.");
        if (string.IsNullOrEmpty(preset))
        {
            RequestedCanvasQualityPreset = null;
            return;
        }
        _ = BrowserWebGpuQualitySettings.CreatePreset(preset);
        RequestedCanvasQualityPreset = preset;
    }

    /// <summary>Returns the resolved engine canvas sizing policy for the current world.</summary>
    [JSExport]
    public static string GetCanvasQualitySettingsJson()
        => (_session ?? throw new InvalidOperationException("WebGPU.Quality.WorldMissing: start an engine canvas world first."))
            .GetCanvasQualitySettingsJson();
}
