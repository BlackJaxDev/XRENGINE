namespace XREngine.Rendering;

/// <summary>Portable resource and packet capability implemented by a browser renderer module.</summary>
public interface IBrowserRendererHost : IRuntimeRendererHost, IBrowserResourceCapability,
    IBrowserFrameSubmissionCapability, IBrowserUploadCapability, IBrowserTextureCopyCapability,
    IBrowserPresentationCapability, IBrowserCompletionCapability, IBrowserGpuResourceCapability,
    IBrowserReadbackCapability, IBrowserCommandCapability, IBrowserFocusedPipelineCapability, IDisposable
{
    BrowserRendererState State { get; }
    BrowserDeviceCapabilities? DeviceCapabilities { get; }
    void MarkReady(int sessionId);
    void MarkFailed(bool deviceLost);
}
