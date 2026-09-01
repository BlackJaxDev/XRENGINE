using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Behavior-only port used by ImGui backend objects. It intentionally exposes no
/// Vulkan authority root, target context, or output service container.
/// </summary>
internal unsafe interface IVulkanImGuiOutputHost
{
    IWindow MainWindow { get; }
    IInputContext? Input { get; }
    bool MainWindowFocused { get; }
    bool TargetRequiresSwapchainOutput { get; }
    bool UseDynamicRenderingRenderTargets { get; }
    bool IsPlatformOutputReady { get; }

    void StoreDrawData(ImGuiNET.ImDrawDataPtr drawData);
    void RegisterPlatformWindow(VulkanImGuiPlatformWindow window);
    void UnregisterPlatformWindow(VulkanImGuiPlatformWindow window);
    void ThrowIfDeviceOperationNotAdmitted(string operation);
    bool TryAdmitDeviceOperation(string operation);
    SurfaceKHR CreatePlatformSurface(IWindow window);
    void DestroyPlatformSurface(ref SurfaceKHR surface);
    void ValidatePlatformPresentSupport(SurfaceKHR surface);
    bool TryCreatePlatformSwapchain(
        SurfaceKHR surface,
        Vector2D<int> framebufferSize,
        uint viewportId,
        SwapchainKHR oldSwapchain,
        out VulkanImGuiPlatformSwapchainGeneration generation);
    VulkanWsiPresentCompletion CreatePlatformPresentCompletion(int imageCount);
    VulkanImGuiPlatformWindowCommandResources CreatePlatformCommandResources(
        int frameCount,
        int imageCount,
        uint viewportId);
    VulkanImGuiDrawBufferResources CreatePlatformDrawBufferResources();
    PresentModeKHR[] GetPlatformPresentModes(SurfaceKHR surface);
    Result WaitForPlatformFence(Fence fence);
    Result WaitForPlatformFenceAtShutdown(Fence fence);
    Result AcquirePlatformImage(
        SwapchainKHR swapchain,
        Silk.NET.Vulkan.Semaphore imageAvailable,
        Fence acquireFence,
        out uint imageIndex);
    Result ResetPlatformFence(Fence fence);
    Result SubmitPlatformDraw(ref SubmitInfo submitInfo, Fence fence);
    Result PresentPlatformViewport(
        ref PresentInfoKHR presentInfo,
        in VulkanWsiPresentReservation reservation);
    bool RecordPlatformViewport(
        VulkanImGuiDrawBufferResources drawBuffers,
        CommandBuffer commandBuffer,
        uint imageIndex,
        int frameSlot,
        VulkanImGuiFrameSnapshot snapshot,
        Image[] images,
        ImageView[] imageViews,
        Extent2D extent,
        bool imagePresented);
    void WaitForPlatformQueuesIdle();
    void DestroyPlatformCommandResources(
        VulkanImGuiPlatformWindowCommandResources commandResources,
        uint viewportId);
    void DestroyPlatformSwapchain(
        SwapchainKHR swapchain,
        Image[] images,
        ImageView[] imageViews,
        uint viewportId);
    void MarkPlatformDeviceLost(string operation, Result result);
}
