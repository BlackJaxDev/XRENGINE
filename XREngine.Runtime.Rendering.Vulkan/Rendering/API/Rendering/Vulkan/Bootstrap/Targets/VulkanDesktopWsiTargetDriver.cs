using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

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
    public IReadOnlyList<string> RequiredDeviceExtensions { get; } = [KhrSwapchain.ExtensionName];
    public Vector2D<int> EffectiveFramebufferSize => _window.EffectiveFramebufferSize;
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

    public void CreateInstanceResources(VulkanRenderer renderer)
        => renderer.CreateDesktopSurface();

    public void InitializeFinalOutput(VulkanRenderer renderer)
        => renderer.CreateDesktopFinalOutput();

    public void DestroyFinalOutput(VulkanRenderer renderer)
        => renderer.DestroyDesktopFinalOutput();

    public void DestroyInstanceResources(VulkanRenderer renderer)
        => renderer.DestroyDesktopSurface();

    public bool RecreateFinalOutput(VulkanRenderer renderer)
        => renderer.RecreateDesktopSwapchainCore();

    public bool ShouldKeepPresentScalingSwapchain(
        VulkanRenderer renderer,
        Result result,
        bool interactiveResize)
        => renderer.ShouldKeepDesktopPresentScalingSwapchainCore(result, interactiveResize);

    public VulkanDesktopPreflightOutcome ClassifyPreflight(EVulkanDesktopPreflightStatus status)
        => VulkanDesktopFramePolicy.ClassifyPreflight(status);

    public VulkanDesktopAcquireOutcome ClassifyAcquire(Result result)
        => VulkanDesktopFramePolicy.ClassifyAcquire(result);

    public VulkanDesktopPresentOutcome ClassifyPresent(Result result)
        => VulkanDesktopFramePolicy.ClassifyPresent(result);

    public EDesktopFrameFlow AcquireFrameTarget(
        VulkanRenderer renderer,
        ref VulkanFrameAttempt attempt)
        => renderer.AcquireDesktopSwapchainImageCore(ref attempt);

    public VulkanDesktopPresentDispatchOutcome PresentFrameTarget(
        VulkanRenderer renderer,
        ref VulkanFrameAttempt attempt,
        string profileScope,
        string? disableFrameGenerationReason)
        => renderer.QueueDesktopPresentCore(
            ref attempt,
            profileScope,
            disableFrameGenerationReason);
}
