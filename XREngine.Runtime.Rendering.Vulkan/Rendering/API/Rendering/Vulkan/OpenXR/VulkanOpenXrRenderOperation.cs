using System;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Temporarily publishes a renderer as the thread-current OpenXR renderer
/// without retaining or calling the Vulkan facade.
/// </summary>
internal readonly struct VulkanOpenXrRenderOperation : IDisposable
{
    private readonly AbstractRenderer _renderer;
    private readonly bool _previousActive;
    private readonly IDisposable _currentRendererScope;

    public VulkanOpenXrRenderOperation(AbstractRenderer renderer)
    {
        _renderer = renderer;
        _previousActive = renderer.Active;
        renderer.Active = true;
        _currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);
        OcclusionGpuElapsedTiming.Instance.Resolve(renderer, RuntimeEngine.Rendering.State.RenderFrameId);
    }

    public void Dispose()
    {
        _currentRendererScope.Dispose();
        _renderer.Active = _previousActive;
    }
}
