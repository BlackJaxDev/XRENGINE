namespace XREngine.Rendering;

/// <summary>Portable resource and packet capability implemented by a browser renderer module.</summary>
public interface IBrowserRendererHost : IRuntimeRendererHost, IBrowserResourceCapability,
    IBrowserFrameSubmissionCapability, IBrowserTextureCopyCapability,
    IBrowserPresentationCapability, IBrowserCompletionCapability, IDisposable
{
    BrowserRendererState State { get; }
    void MarkReady(int sessionId);
    void MarkFailed(bool deviceLost);
}
