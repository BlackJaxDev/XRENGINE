using System.Threading;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop : IVulkanImGuiOutputHost
{
    private readonly VulkanImGuiPlatformViewportRecorder _imguiPlatformViewportRecorder = new();

    IWindow IVulkanImGuiOutputHost.MainWindow => _imguiWindowHost!.Window;
    IInputContext? IVulkanImGuiOutputHost.Input => _imguiWindowHost!.Input;
    bool IVulkanImGuiOutputHost.MainWindowFocused => _imguiWindowHost!.IsFocused;
    bool IVulkanImGuiOutputHost.TargetRequiresSwapchainOutput => TargetRequiresSwapchainOutput;
    bool IVulkanImGuiOutputHost.UseDynamicRenderingRenderTargets
        => _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets;
    bool IVulkanImGuiOutputHost.IsPlatformOutputReady
        => _outputRuntime.SurfaceApi is not null &&
           _outputRuntime.Desktop.SwapchainExtension is not null &&
           _deviceContext.IsReady;

    private XRWindow? _imguiWindowHost;

    internal VulkanImGuiBackend GetOrCreateImGuiBackend(
        XRWindow windowHost,
        Action resetFrameMarker)
    {
        if (_outputRuntime.ConsumeImGuiFrameMarkerResetRequest())
            resetFrameMarker();
        _imguiWindowHost = windowHost;
        return GetOrCreateImGuiBackendCore(this, windowHost);
    }

    void IVulkanImGuiOutputHost.StoreDrawData(ImGuiNET.ImDrawDataPtr drawData)
        => _outputRuntime.StoreImGuiDrawData(drawData);
    void IVulkanImGuiOutputHost.RegisterPlatformWindow(VulkanImGuiPlatformWindow window)
        => ImGuiPlatformWindows.Register(window);
    void IVulkanImGuiOutputHost.UnregisterPlatformWindow(VulkanImGuiPlatformWindow window)
        => ImGuiPlatformWindows.Unregister(window);
    void IVulkanImGuiOutputHost.ThrowIfDeviceOperationNotAdmitted(string operation)
        => TargetOutputSession.ThrowIfVulkanDeviceOperationNotAdmitted(operation);
    bool IVulkanImGuiOutputHost.TryAdmitDeviceOperation(string operation)
        => TargetOutputSession.TryAdmitVulkanDeviceOperation(operation, out _);
    SurfaceKHR IVulkanImGuiOutputHost.CreatePlatformSurface(IWindow window)
        => ImGuiPlatformWindows.CreateSurface(_deviceContext, window);
    void IVulkanImGuiOutputHost.DestroyPlatformSurface(ref SurfaceKHR surface)
        => ImGuiPlatformWindows.DestroySurface(_deviceContext, _outputRuntime.SurfaceApi, ref surface);

    void IVulkanImGuiOutputHost.ValidatePlatformPresentSupport(SurfaceKHR surface)
    {
        uint presentFamily = _deviceContext.QueueFamilies.PresentFamilyIndex
            ?? throw new InvalidOperationException("The Vulkan renderer has no presentation queue family.");
        Result result = _outputRuntime.SurfaceApi!.GetPhysicalDeviceSurfaceSupport(
            _deviceContext.PhysicalDevice,
            presentFamily,
            surface,
            out Bool32 supported);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to query detached-window presentation support: {result}.");
        if (!supported)
            throw new NotSupportedException(
                $"The renderer's presentation queue family {presentFamily} cannot present this detached ImGui window.");
    }

    bool IVulkanImGuiOutputHost.TryCreatePlatformSwapchain(
        SurfaceKHR surface,
        Vector2D<int> framebufferSize,
        uint viewportId,
        out VulkanImGuiPlatformSwapchainGeneration generation)
    {
        KhrSurface? surfaceApi = _outputRuntime.SurfaceApi;
        KhrSwapchain? swapchainApi = _outputRuntime.Desktop.SwapchainExtension;
        if (surfaceApi is null || swapchainApi is null)
            throw new InvalidOperationException(
                "Detached ImGui viewport output requires initialized Vulkan surface and swapchain extensions.");
        return ImGuiPlatformWindows.TryCreateSwapchainGeneration(
            _deviceContext,
            _commandRuntime,
            TargetOutputSession,
            surfaceApi,
            swapchainApi,
            surface,
            framebufferSize,
            _outputRuntime.Desktop.ImageFormat,
            _outputRuntime.Desktop.ImageColorSpace,
            viewportId,
            out generation);
    }

    VulkanImGuiPlatformWindowCommandResources IVulkanImGuiOutputHost.CreatePlatformCommandResources(
        int frameCount,
        int imageCount,
        uint viewportId)
        => _commandRuntime.CreateImGuiPlatformWindowResources(
            _deviceContext,
            TargetOutputSession,
            _deviceContext.QueueFamilies.GraphicsFamilyIndex!.Value,
            frameCount,
            imageCount,
            viewportId);

    unsafe PresentModeKHR[] IVulkanImGuiOutputHost.GetPlatformPresentModes(SurfaceKHR surface)
    {
        KhrSurface surfaceApi = _outputRuntime.SurfaceApi!;
        uint count = 0;
        Result result = surfaceApi.GetPhysicalDeviceSurfacePresentModes(
            _deviceContext.PhysicalDevice, surface, ref count, null);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to query detached-window present mode count: {result}.");
        if (count == 0)
            return [];
        PresentModeKHR[] modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            result = surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                _deviceContext.PhysicalDevice, surface, ref count, modesPtr);
        }
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to query detached-window present modes: {result}.");
        return modes;
    }

    Result IVulkanImGuiOutputHost.WaitForPlatformFence(Fence fence)
    {
        TargetOutputSession.ThrowIfVulkanDeviceOperationNotAdmitted("vkWaitForFences.ImGuiViewport");
        ulong timeout = DesktopWsiOutput.IsInteractiveResizeInProgress ||
            RuntimeRenderingHostServices.Presentation.IsOpenXRActive
                ? 0UL
                : ulong.MaxValue;
        Result result = Api.WaitForFences(_deviceContext.Device, 1, in fence, true, timeout);
        if (result == Result.Success)
            TargetOutputSession.NotifyVulkanFenceCompleted(fence);
        return result;
    }

    unsafe Result IVulkanImGuiOutputHost.AcquirePlatformImage(
        SwapchainKHR swapchain,
        Silk.NET.Vulkan.Semaphore imageAvailable,
        out uint imageIndex)
    {
        uint acquiredImageIndex = 0;
        ulong timeout = DesktopWsiOutput.IsInteractiveResizeInProgress ||
            RuntimeRenderingHostServices.Presentation.IsOpenXRActive
                ? 0UL
                : ulong.MaxValue;
        Result result = _outputRuntime.Desktop.SwapchainExtension!.AcquireNextImage(
            _deviceContext.Device, swapchain, timeout, imageAvailable, default, &acquiredImageIndex);
        imageIndex = acquiredImageIndex;
        return result;
    }

    Result IVulkanImGuiOutputHost.ResetPlatformFence(Fence fence)
        => Api.ResetFences(_deviceContext.Device, 1, in fence);

    Result IVulkanImGuiOutputHost.SubmitPlatformDraw(ref SubmitInfo submitInfo, Fence fence)
        => SubmitImGuiPlatformToGraphicsQueue(ref submitInfo, fence);

    Result IVulkanImGuiOutputHost.PresentPlatformViewport(ref PresentInfoKHR presentInfo)
        => PresentImGuiPlatformViewport(ref presentInfo);

    bool IVulkanImGuiOutputHost.RecordPlatformViewport(
        CommandBuffer commandBuffer,
        uint imageIndex,
        int frameSlot,
        VulkanImGuiFrameSnapshot snapshot,
        Image[] images,
        ImageView[] imageViews,
        Extent2D extent,
        bool imagePresented)
    {
        ImGuiFontAtlasResources.EnsureCreated();
        ImGuiOutputPipelineService.EnsureCreated();
        VulkanTrackedCommandEncoder encoder = new(_commandRuntime);
        VulkanDynamicUiOverlayTarget target = new(
            images[imageIndex], imageViews[imageIndex], extent, false, default, default, ImageLayout.Undefined);
        VulkanImGuiOverlayRecordingInput input = new(
            (uint)frameSlot,
            commandBuffer,
            default,
            imagePresented ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined,
            _deviceContext.InstanceApiVersion < Vk.Version13,
            target,
            _outputRuntime._imguiResources,
            _outputRuntime._imguiTextureRegistry.DescriptorSets,
            true,
            snapshot);
        return _imguiPlatformViewportRecorder.TryRecord(
            encoder, _telemetry, ImGuiDrawBufferResources, in input, out _);
    }

    void IVulkanImGuiOutputHost.WaitForPlatformQueuesIdle()
    {
        if (!_deviceContext.IsReady || !_deviceContext.IsOperational)
            return;
        _ = WaitForImGuiPlatformQueueCompletion(_deviceContext.GraphicsQueue, "ImGuiViewportDestroy.Graphics");
        if (_deviceContext.PresentQueue.Handle != _deviceContext.GraphicsQueue.Handle)
            _ = WaitForImGuiPlatformQueueCompletion(_deviceContext.PresentQueue, "ImGuiViewportDestroy.Present");
    }

    void IVulkanImGuiOutputHost.DestroyPlatformCommandResources(
        VulkanImGuiPlatformWindowCommandResources commandResources,
        uint viewportId)
    {
        if (!_deviceContext.IsReady)
            return;
        _commandRuntime.DestroyImGuiPlatformWindowResources(
            _deviceContext, TargetOutputSession, commandResources, viewportId);
    }

    void IVulkanImGuiOutputHost.DestroyPlatformSwapchain(
        SwapchainKHR swapchain,
        Image[] images,
        ImageView[] imageViews,
        uint viewportId)
    {
        KhrSwapchain? swapchainApi = _outputRuntime.Desktop.SwapchainExtension;
        if (swapchainApi is null)
            return;
        ImGuiPlatformWindows.DestroySwapchainGeneration(
            _deviceContext,
            _commandRuntime,
            TargetOutputSession,
            swapchainApi,
            swapchain,
            images,
            imageViews,
            viewportId);
    }

    void IVulkanImGuiOutputHost.MarkPlatformDeviceLost(string operation, Result result)
        => TargetOutputSession.MarkDeviceLost(
            $"Detached ImGui viewport failed to {operation}", operation, result);

    private Result SubmitImGuiPlatformToGraphicsQueue(ref SubmitInfo submitInfo, Fence fence)
        => _commandRuntime.SubmitToQueueTracked(
            Api,
            _deviceContext,
            _telemetry,
            _deviceContext.GraphicsQueue,
            ref submitInfo,
            fence,
            "ImGuiViewport");

    private unsafe Result WaitForImGuiPlatformQueueCompletion(Queue queue, string operation)
    {
        if (!TargetOutputSession.TryAdmitVulkanDeviceOperation(operation, out _))
            return Result.ErrorDeviceLost;

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result = Api.CreateFence(
            _deviceContext.Device,
            in fenceInfo,
            null,
            out Fence completionFence);
        if (result != Result.Success)
            return result;

        try
        {
            SubmitInfo markerSubmit = new() { SType = StructureType.SubmitInfo };
            result = _commandRuntime.SubmitToQueueTracked(
                Api,
                _deviceContext,
                _telemetry,
                queue,
                ref markerSubmit,
                completionFence,
                operation);
            if (result != Result.Success)
                return result;

            // The native queue lease ended with QueueSubmit. Waiting for the
            // queue-ordered completion marker must never hold that lease.
            result = Api.WaitForFences(
                _deviceContext.Device,
                1,
                in completionFence,
                true,
                ulong.MaxValue);
            _deviceContext.ObserveNativeResult(operation, result);
            if (result == Result.Success)
            {
                TargetOutputSession.NotifyVulkanFenceCompleted(completionFence);
                _commandRuntime.CompleteTrackedQueue(queue);
            }
            return result;
        }
        finally
        {
            if (_deviceContext.IsReady)
                Api.DestroyFence(_deviceContext.Device, completionFence, null);
        }
    }

    private Result PresentImGuiPlatformViewport(ref PresentInfoKHR presentInfo)
    {
        const string operation = "ImGuiViewport.Present";
        if (!TargetOutputSession.TryAdmitVulkanDeviceOperation(operation, out _))
            return Result.ErrorDeviceLost;
        ReaderWriterLockSlim admissionGate =
            _commandRuntime.CommandBuffers.DeviceQueueAdmissionGate;
        admissionGate.EnterReadLock();
        Result result;
        try
        {
            using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
                _commandRuntime.CommandBuffers.OneTimeSubmitGate,
                _deviceContext.StateMachine,
                _telemetry);
            if (!queueOperation.Acquired)
                return Result.ErrorDeviceLost;
            result = _outputRuntime.Desktop.SwapchainExtension!.QueuePresent(
                _deviceContext.PresentQueue, ref presentInfo);
        }
        finally
        {
            admissionGate.ExitReadLock();
        }
        _deviceContext.ObserveNativeResult("vkQueuePresentKHR.ImGuiViewport", result);
        _commandRuntime.Synchronization.RecordQueueOperation(
            _deviceContext.State,
            "present-imgui-viewport",
            _deviceContext.PresentQueue,
            result,
            0,
            nameof(PresentImGuiPlatformViewport));
        return result;
    }
}
