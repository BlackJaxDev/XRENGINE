using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    /// <summary>Changes optional frame measurements between engine frames; reset starts a new capture window.</summary>
    [JSExport]
    public static void ConfigureCanvasFrameStatistics(bool enabled, bool reset)
        => (_session ?? throw new InvalidOperationException("WebGPU.Statistics.WorldMissing: start an engine canvas world first."))
            .ConfigureCanvasFrameStatistics(enabled, reset);

    /// <summary>Serializes managed frame measurements only when explicitly requested.</summary>
    [JSExport]
    public static string GetCanvasFrameStatisticsJson()
        => JsonSerializer.Serialize(
            (_session ?? throw new InvalidOperationException("WebGPU.Statistics.WorldMissing: start an engine canvas world first."))
                .CaptureCanvasFrameStatistics(),
            BrowserEngineStatisticsJsonContext.Default.WebGpuEngineFrameStatistics);
}
