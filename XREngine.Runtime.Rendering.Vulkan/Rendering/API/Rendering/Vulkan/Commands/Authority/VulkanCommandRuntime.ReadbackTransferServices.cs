using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracked native transfer adapters used by renderer-owned readback and
/// physical-resource producers after they freeze their native inputs.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal unsafe void FreeCommandBufferWithLifetime(
        int frameSlot,
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        fixed (CommandBuffer* commandBufferPointer = &commandBuffer)
        {
            FreeCommandBuffersWithLifetime(
                frameSlot,
                commandPool,
                1,
                commandBufferPointer,
                owner);
        }
    }

    internal unsafe void CopyImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageCopy* regions)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }
}
