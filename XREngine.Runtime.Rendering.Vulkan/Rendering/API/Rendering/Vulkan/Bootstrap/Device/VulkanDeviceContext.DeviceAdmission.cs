using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
    {
        if (IsOperational)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = $"Cannot start Vulkan operation '{operation}' while device state is {State}.";
        return false;
    }

    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitVulkanDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    internal InvalidOperationException CreateDeviceLostException(
        string operation,
        Result result,
        Action<string?, string?, Result> coordinateDeviceLoss)
    {
        ArgumentNullException.ThrowIfNull(coordinateDeviceLoss);
        ObserveNativeResult(operation, result);
        coordinateDeviceLoss($"{operation} returned {result}", operation, result);
        return new InvalidOperationException(
            $"Vulkan device lost during {operation} ({result}). Reason={DeviceFaultFacility.DeviceLostReason ?? "<unknown>"}. The logical device is terminal and the renderer/window must be recreated before Vulkan can render again.");
    }
}
