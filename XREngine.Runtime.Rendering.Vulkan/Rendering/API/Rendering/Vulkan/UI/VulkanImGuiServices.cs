using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Supplies detached ImGui viewport output with the narrow runtime authorities
/// it needs, without retaining the renderer facade.
/// </summary>
internal sealed unsafe class VulkanImGuiServices
{
    internal VulkanImGuiServices(
        XRWindow windowHost,
        VulkanOutputRuntime output,
        VulkanDeviceContext device,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry)
    {
        WindowHost = windowHost;
        Output = output;
        Device = device;
        Commands = commands;
        Resources = resources;
        Telemetry = telemetry;
    }

    internal XRWindow WindowHost { get; }
    internal IWindow MainWindow => WindowHost.Window;
    internal IInputContext? Input => WindowHost.Input;
    internal bool MainWindowFocused => WindowHost.IsFocused;
    internal VulkanOutputRuntime Output { get; }
    internal VulkanDeviceContext Device { get; }
    internal VulkanCommandRuntime Commands { get; }
    internal VulkanResourceRuntime Resources { get; }
    internal VulkanFrameTelemetry Telemetry { get; }
    internal VulkanTargetOutputContext Target => Output.TargetOutputContext;
    internal Vk Api => Target.VulkanApi;
    internal bool TargetRequiresSwapchainOutput => Output.TargetDriver.RequiresSwapchainOutput;
    internal bool UseDynamicRenderingRenderTargets => Device.MutableCapabilities._useDynamicRenderingRenderTargets;

    internal Result SubmitToGraphicsQueue(ref SubmitInfo submitInfo, Fence fence, string operation)
        => Commands.SubmitToQueueTracked(
            Api,
            Device,
            Telemetry,
            Device.GraphicsQueue,
            ref submitInfo,
            fence,
            operation);

    internal Result WaitForQueueIdle(Queue queue, string operation)
    {
        if (!Target.TryAdmitVulkanDeviceOperation(operation, out _))
            return Result.ErrorDeviceLost;

        using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
            Commands.CommandBuffers.OneTimeSubmitGate,
            Device.StateMachine,
            Telemetry);
        if (!queueOperation.Acquired)
            return Result.ErrorDeviceLost;

        Result result = Api.QueueWaitIdle(queue);
        Device.ObserveNativeResult(operation, result);
        Commands.Synchronization.RecordQueueOperation(
            Device.State,
            "wait-idle",
            queue,
            result,
            0,
            operation);
        if (result == Result.Success)
            Commands.CompleteTrackedQueue(queue);
        return result;
    }

    internal Result PresentViewport(ref PresentInfoKHR presentInfo)
    {
        if (!Target.TryAdmitVulkanDeviceOperation("ImGuiViewport.Present", out _))
            return Result.ErrorDeviceLost;

        using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
            Commands.CommandBuffers.OneTimeSubmitGate,
            Device.StateMachine,
            Telemetry);
        if (!queueOperation.Acquired)
            return Result.ErrorDeviceLost;

        Result result = Output.Desktop.SwapchainExtension!.QueuePresent(
            Device.PresentQueue,
            ref presentInfo);
        Device.ObserveNativeResult("vkQueuePresentKHR.ImGuiViewport", result);
        Commands.Synchronization.RecordQueueOperation(
            Device.State,
            "present-imgui-viewport",
            Device.PresentQueue,
            result,
            0,
            nameof(PresentViewport));
        return result;
    }
}
