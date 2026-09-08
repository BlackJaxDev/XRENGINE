using System.Threading;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>
/// Retains one completed offscreen output while a consumer records and completes its GPU read.
/// Dispose only after the consumer's own completion fence has signaled.
/// </summary>
public sealed class AdvancedOffscreenTextureCaptureLease : IDisposable
{
    private AdvancedOffscreenTextureCaptureComponent? _owner;

    internal AdvancedOffscreenTextureCaptureLease(
        AdvancedOffscreenTextureCaptureComponent owner,
        XRTexture2D texture)
    {
        _owner = owner;
        Texture = texture;
    }

    public XRTexture2D Texture { get; }

    public void Dispose()
        => Interlocked.Exchange(ref _owner, null)?.ReleaseOutputPublication();
}
