namespace XREngine.Rendering;

/// <summary>A browser canvas presentation surface described without a platform-host dependency.</summary>
public interface IBrowserCanvasPresentationTarget : IRendererPresentationTarget, IRuntimeSurfaceHost
{
    /// <summary>Describes the configured output only while the current surface is drawable.</summary>
    bool TryDescribeFrameOutput(out RenderFrameOutputDescription output);
}
