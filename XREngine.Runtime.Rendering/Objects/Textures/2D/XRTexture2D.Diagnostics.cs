using System.Text;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    /// <summary>
    /// Captures the active backend's imported-texture upload diagnostics for
    /// on-demand inspection. Counters describe backend activity, not one texture
    /// or an atomically sampled frame.
    /// </summary>
    /// <remarks>
    /// This allocates a diagnostic string and must not be called on a per-frame
    /// rendering path. An unavailable backend returns an empty summary.
    /// </remarks>
    public static string GetTextureStreamingBackendProfilerSummary()
    {
        if (!TextureStreamingBackendRegistry.TryGet(
                RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend,
                out ITextureStreamingBackendProvider? provider)
            || provider is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new(1024);
        provider.AppendProfilerSummary(builder);
        return builder.ToString();
    }
}
