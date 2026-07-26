namespace XREngine.Rendering;

/// <summary>
/// Backend presentation state needed by host scheduling policy for NVIDIA Streamline.
/// </summary>
public interface IStreamlinePresentationBackendCapability
{
    bool StreamlineFrameGenerationProvisioned { get; }
    bool StreamlineFrameGenerationSwapchainActive { get; }
    bool SwapchainRequiresSrgbEncoding { get; }
}
