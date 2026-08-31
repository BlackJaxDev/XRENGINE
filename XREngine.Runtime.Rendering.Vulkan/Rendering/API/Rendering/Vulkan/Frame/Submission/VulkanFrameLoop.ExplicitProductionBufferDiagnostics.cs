using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    internal bool TryDescribeCurrentNativeBuffer(
        XRDataBuffer sourceBuffer,
        out VulkanNativeBufferDiagnosticDescription description)
    {
        ArgumentNullException.ThrowIfNull(sourceBuffer);
        description = default;
        if (_resourceRuntime.WrapperLookup.GetOrCreate(sourceBuffer, generateNow: false) is not VkDataBuffer vkBuffer ||
            vkBuffer is not
            {
                IsGenerated: true,
                BufferHandle: { } nativeBuffer,
            } || nativeBuffer.Handle == 0)
        {
            return false;
        }

        VulkanBackendObjectContext context = _resourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
            "The Vulkan backend object context is not initialized.");
        description = new VulkanNativeBufferDiagnosticDescription(
            nativeBuffer.Handle,
            vkBuffer.AllocatedByteSize,
            context.GetResourceGeneration(ObjectType.Buffer, nativeBuffer.Handle),
            IsGenerated: true,
            context.IsDeviceOperational);
        return true;
    }
}
