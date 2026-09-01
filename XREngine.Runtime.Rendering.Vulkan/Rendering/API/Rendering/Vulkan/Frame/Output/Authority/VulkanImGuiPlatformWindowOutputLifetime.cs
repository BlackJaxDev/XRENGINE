using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan WSI lifetime state for one detached ImGui platform window. Native
/// window/input ownership remains with the ImGui platform adapter.
/// </summary>
internal abstract class VulkanImGuiPlatformWindowOutputLifetime
{
    protected const int FramesInFlight = 2;
    protected SurfaceKHR _surface;
    protected SwapchainKHR _swapchain;
    protected Format _format;
    protected ColorSpaceKHR _colorSpace;
    protected Extent2D _extent;
    protected Image[] _images = [];
    protected ImageView[] _imageViews = [];
    protected bool[] _imagePresented = [];
    protected CommandPool _commandPool;
    protected CommandBuffer[] _commandBuffers = [];
    protected Fence[] _frameFences = [];
    protected bool[] _frameFenceSubmitted = [];
    protected Fence[] _acquireFences = [];
    protected bool[] _acquireFenceSubmitted = [];
    protected Semaphore[] _imageAvailableSemaphores = [];
    protected Semaphore[] _renderFinishedSemaphores = [];
    protected VulkanWsiPresentCompletion? _presentCompletion;
    protected int _frameSlot;
    protected bool _rendererReady;
    protected bool _resizeRequested;

    /// <summary>Runs only after the global shutdown boundary owns GPU idleness.</summary>
    internal virtual void WaitForPresentationReleaseAtShutdown(bool deviceLost)
    {
    }
}
