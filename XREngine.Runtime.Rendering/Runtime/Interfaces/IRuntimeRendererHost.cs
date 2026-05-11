namespace XREngine.Rendering;

/// <summary>
/// Marker interface for host renderer implementations such as OpenGL or Vulkan renderers.
/// </summary>
public interface IRuntimeRendererHost
{
    /// <summary>
    /// Returns whether this renderer can consume a GPU-written indirect draw-count buffer directly.
    /// </summary>
    bool SupportsIndirectCountDraw();

    /// <summary>
    /// Returns whether this renderer can submit the meshlet/task-mesh path.
    /// </summary>
    bool SupportsMeshletDispatch();
}
