namespace XREngine.Rendering;

/// <summary>Executes the packet's single opaque canvas render pass and indexed draw commands.</summary>
public interface IBrowserFrameSubmissionCapability
{
    void SubmitPacket(BrowserFramePacket packet);
}
