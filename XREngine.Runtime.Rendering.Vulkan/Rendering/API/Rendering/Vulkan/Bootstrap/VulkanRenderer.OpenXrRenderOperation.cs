using System;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Enters an explicit OpenXR render operation for this renderer. The scope
    /// confines legacy current-renderer compatibility to the render thread and
    /// never mutates the process-wide current renderer.
    /// </summary>
    internal IDisposable EnterOpenXrRenderOperation()
        => new OpenXrRenderOperation(this);

    private readonly struct OpenXrRenderOperation : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly bool _previousActive;
        private readonly IDisposable _currentRendererScope;

        public OpenXrRenderOperation(VulkanRenderer renderer)
        {
            _renderer = renderer;
            _previousActive = renderer.Active;
            renderer.Active = true;
            _currentRendererScope = AbstractRenderer.PushThreadCurrent(renderer);
        }

        public void Dispose()
        {
            _currentRendererScope.Dispose();
            _renderer.Active = _previousActive;
        }
    }
}
