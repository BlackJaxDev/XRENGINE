using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Desktop WSI bootstrap policy backed by an <see cref="XRWindow"/>.</summary>
internal sealed unsafe class VulkanDesktopWsiTargetDriver : IVulkanRendererTargetDriver
{
    private readonly XRWindow _window;

    public VulkanDesktopWsiTargetDriver(RendererHostContext hostContext)
        => _window = hostContext.RequireDesktopWindow<XRWindow>();

    public RenderExecutionMode ExecutionMode => RenderExecutionMode.DesktopWsi;
    public bool RequiresPresentQueue => true;
    public bool RequiresSwapchainOutput => true;
    public bool SupportsStreamlinePresentation => true;
    internal XRWindow Window => _window;
    public IReadOnlyList<string> RequiredDeviceExtensions { get; } = [KhrSwapchain.ExtensionName];
    public Vector2D<int> EffectiveFramebufferSize => _window.RenderFramebufferSize;
    public WindowResizeExtents ResizeExtents => _window.ResizeExtents;
    public bool IsInteractiveResizeInProgress => _window.IsInteractiveResizeInProgress;
    public bool PreferHdrOutput => _window.PreferHDROutput;

    public string[] GetRequiredInstanceExtensions()
    {
        if (_window.Window.VkSurface is null)
            throw new InvalidOperationException("The desktop windowing platform does not provide Vulkan surface services.");

        byte** extensionNames = _window.Window.VkSurface.GetRequiredExtensions(out uint extensionCount);
        return SilkMarshal.PtrToStringArray((nint)extensionNames, checked((int)extensionCount));
    }

    public void CreateInstanceResources(VulkanTargetSurfaceAuthority surfaces)
        => surfaces.CreateDesktopSurface();

    public void InitializeFinalOutput(VulkanTargetOutputContext output)
    {
    }

    public void DestroyFinalOutput(VulkanTargetOutputContext output)
    {
    }

    public void DestroyInstanceResources(VulkanTargetSurfaceAuthority surfaces)
        => surfaces.DestroyDesktopSurface();

    public VulkanDesktopPreflightOutcome ClassifyPreflight(EVulkanDesktopPreflightStatus status)
        => VulkanDesktopFramePolicy.ClassifyPreflight(status);

    public VulkanDesktopAcquireOutcome ClassifyAcquire(Result result)
        => VulkanDesktopFramePolicy.ClassifyAcquire(result);

    public VulkanDesktopPresentOutcome ClassifyPresent(Result result)
        => VulkanDesktopFramePolicy.ClassifyPresent(result);

    /// <summary>
    /// Freezes the acquired desktop output into the same lease consumed by all
    /// other Vulkan execution modes. WSI recovery remains desktop policy, but
    /// recording and queue submission no longer query mutable swapchain state.
    /// </summary>
    internal VulkanFrameTargetLease CreateFrameTargetLease(
        VulkanOutputRuntime output,
        uint imageIndex,
        uint frameSlotIndex,
        Result acquireResult,
        Semaphore acquireSemaphore,
        Semaphore presentSemaphore)
    {
        VulkanDesktopOutputState desktop = output.Desktop;
        VulkanSwapchainDepthResources? depth = output.DesktopDepthResources;
        if (desktop.Images is null || desktop.ImageViews is null ||
            imageIndex >= desktop.Images.Length || imageIndex >= desktop.ImageViews.Length ||
            depth is null)
        {
            throw new InvalidOperationException(
                $"Desktop WSI image {imageIndex} has no complete target generation to lease.");
        }

        VulkanRenderFrameTarget target = new(
            desktop.Images[imageIndex],
            desktop.ImageViews[imageIndex],
            depth.Image,
            depth.View,
            desktop.Extent,
            Layers: 1,
            desktop.IsImageEverPresented(imageIndex)
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined,
            ImageLayout.PresentSrcKhr,
            desktop.Generation,
            frameSlotIndex);
        return new VulkanFrameTargetLease(
            target,
            desktop.ImageFormat,
            depth.Format,
            SampleCountFlags.Count1Bit,
            imageIndex,
            acquireResult,
            acquireSemaphore,
            PipelineStageFlags.ColorAttachmentOutputBit,
            presentSemaphore,
            default,
            VulkanFrameTargetCompletionKind.WsiPresent,
            ImagesExternallyOwned: true,
            ViewIndex: 0,
            SupportsHiddenAreaMask: false);
    }

}
