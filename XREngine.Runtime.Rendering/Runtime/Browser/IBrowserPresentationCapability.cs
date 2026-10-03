namespace XREngine.Rendering;

/// <summary>Describes the canvas output acquired by the frame executor at submission time.</summary>
public interface IBrowserPresentationCapability
{
    bool TryDescribeFrameOutput(out RenderFrameOutputDescription output);
}
