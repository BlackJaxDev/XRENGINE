using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow native services used by secondary-command recording.  The recorder
/// owns command-buffer policy while device and resource authorities retain
/// native lifetime and diagnostic ownership.
/// </summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    private static DynamicRenderingFormatSignature
        CreateSwapchainColorOnlyDynamicRenderingFormatSignature(
            Format colorFormat)
    {
        Span<Format> colorFormats = stackalloc Format[1];
        colorFormats[0] = colorFormat;
        return new DynamicRenderingFormatSignature(
            colorFormats,
            Format.Undefined,
            Format.Undefined);
    }

    private static ulong ComputeCommandBufferDataBufferSignature(
        VkDataBuffer? buffer)
    {
        FrameOpSignatureHasher hash = new();
        if (buffer is null)
        {
            hash.Add(0UL);
            return hash.ToHash();
        }

        hash.Add(buffer.GetHashCode());
        hash.Add(buffer.BufferHandle?.Handle ?? 0UL);
        hash.Add(buffer.AllocatedByteSize);
        hash.Add(buffer.UploadedByteCount);
        hash.Add(buffer.HasPendingUpload);
        hash.Add(buffer.Data.Length);
        hash.Add((int)buffer.Data.Target);
        hash.Add((ulong)buffer.LastUsageFlags);
        return hash.ToHash();
    }

    private bool CanResetSecondaryCommandBuffer(CommandBuffer commandBuffer)
        => ResourceRuntime.CanResetCommandBuffer(this, commandBuffer);

    private bool SupportsSecondaryDebugNames => DeviceContext.DebugUtils is not null;

    private void SetSecondaryDebugObjectName(
        ObjectType objectType,
        ulong objectHandle,
        string name)
    {
        if (!SupportsSecondaryDebugNames ||
            DeviceContext.Device.Handle == 0 ||
            objectHandle == 0 ||
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        nint namePointer = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsObjectNameInfoEXT nameInfo = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = objectType,
                ObjectHandle = objectHandle,
                PObjectName = (byte*)namePointer,
            };
            _ = DeviceContext.DebugUtils!.SetDebugUtilsObjectName(
                DeviceContext.Device,
                in nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePointer);
        }
    }
}
