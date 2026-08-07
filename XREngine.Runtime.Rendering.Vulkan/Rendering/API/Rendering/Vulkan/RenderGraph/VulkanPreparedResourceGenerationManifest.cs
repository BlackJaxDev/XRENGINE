using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable native-object proof captured while a pending render-resource
/// generation is staged. Commit revalidates this exact proof before publishing.
/// </summary>
internal sealed class VulkanPreparedResourceGenerationManifest(
    RenderResourceRegistry registry,
    int descriptorSignature,
    VulkanPreparedResourceGenerationManifest.ImageEntry[] images,
    VulkanPreparedResourceGenerationManifest.FrameBufferEntry[] frameBuffers,
    VulkanPreparedResourceGenerationManifest.BufferEntry[] buffers)
{
    private readonly ImageEntry[] _images = images;
    private readonly FrameBufferEntry[] _frameBuffers = frameBuffers;
    private readonly BufferEntry[] _buffers = buffers;

    internal readonly record struct ImageEntry(
        string Name,
        XRTexture Texture,
        IVkImageDescriptorSource Source,
        VkImageDescriptorSnapshot Snapshot,
        ulong ImageGeneration,
        ulong ViewGeneration,
        ulong SamplerGeneration);

    internal readonly record struct FrameBufferEntry(
        string Name,
        XRFrameBuffer FrameBuffer,
        VkFrameBuffer Wrapper,
        VulkanRecordedRenderTargetSnapshot Snapshot);

    internal readonly record struct BufferEntry(
        Buffer Buffer,
        ulong Generation,
        ulong SizeInBytes);

    public RenderResourceRegistry Registry { get; } = registry;
    public int DescriptorSignature { get; } = descriptorSignature;
    public int ImageCount => _images.Length;
    public int FrameBufferCount => _frameBuffers.Length;
    public int BufferCount => _buffers.Length;
    public ImageEntry GetImage(int index) => _images[index];
    public FrameBufferEntry GetFrameBuffer(int index) => _frameBuffers[index];
    public BufferEntry GetBuffer(int index) => _buffers[index];
}
