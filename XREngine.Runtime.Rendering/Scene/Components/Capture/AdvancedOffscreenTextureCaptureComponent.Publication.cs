using System.Diagnostics.CodeAnalysis;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Lights;

public abstract partial class AdvancedOffscreenTextureCaptureComponent
{
    private AdvancedMutableTexturePublicationLifetime? _canonicalLifetime;

    /// <summary>
    /// Publishes the completed texture for canonical Advanced material consumers.
    /// Publication pins the producer even between frames; withdraw it before requesting
    /// another write. Accepted scene snapshots keep their own generation-checked retains.
    /// </summary>
    public bool TryPublishCompletedOutput([NotNullWhen(true)] out XRTexture2D? texture)
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Canonical capture publication requires the render thread.");
        texture = null;
        lock (_publicationSync)
        {
            if (_retirementRequested || _writeInProgress || HasPendingWriter || _resourcesDirty ||
                !_hasCompletedCapture || _quarantined || _outputTexture is null ||
                _canonicalLifetime is null || !_canonicalLifetime.TryPublish())
                return false;
            texture = _outputTexture;
            return true;
        }
    }

    /// <summary>
    /// Stops admitting new canonical readers. Previously prepared/recorded publications
    /// continue to block refresh and retirement until their exact ownership is released.
    /// </summary>
    public void WithdrawPublishedOutput()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Canonical capture withdrawal requires the render thread.");
        lock (_publicationSync)
            _canonicalLifetime?.Withdraw();
    }
}
