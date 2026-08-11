using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Narrow command-authority entry points for renderer-owned producers.</summary>
internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>
    /// Enqueues a frozen texture-upload operation without giving the resource
    /// service a renderer callback or frame-loop authority.
    /// </summary>
    internal void EnqueuePreparedTextureUpload(
        VulkanFrameOperationQueue operations,
        in FrameOpContext context,
        VulkanImportedTexturePendingUpload upload)
        => operations.EnqueuePrepared(new TextureUploadFrameOp(upload, context));

    internal void CopyPreparedUploadBufferToImage(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        ref BufferImageCopy region)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyBufferToImage(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            1,
            ref region);
    }

    internal void RetireUploadBuffer(
        Silk.NET.Vulkan.Buffer buffer,
        DeviceMemory memory,
        string owner)
        => ResourceRuntime.Buffers.Retire(buffer, memory, owner);

    internal static bool FrameDiagnosticsTraceEnabled =>
        CommandRecordingDiagnosticsEnabled ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;
}
