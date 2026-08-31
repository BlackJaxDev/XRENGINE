using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool TryDescribeExplicitNativeTexture(
        XRTexture2D texture,
        out VulkanNativeTextureDiagnosticDescription description)
    {
        ArgumentNullException.ThrowIfNull(texture);
        description = default;
        if (!HasExplicitFrameTarget || !RuntimeEngine.IsRenderThread ||
            _resourceRuntime.WrapperLookup.GetOrCreate(texture, generateNow: false) is not IVkImageDescriptorSource source)
            return false;
        lock (source.DescriptorSnapshotSyncRoot)
            return TryDescribeExplicitNativeTextureSource(source, out description);
    }

    private bool TryDescribeExplicitNativeTextureSource(
        IVkImageDescriptorSource source,
        out VulkanNativeTextureDiagnosticDescription description)
    {
        description = default;
        if (!source.IsDescriptorReady || source.DescriptorFormat != Format.R8G8B8A8Unorm ||
            source.DescriptorImage.Handle == 0 || source.DescriptorArrayLayers != 1 ||
            source.DescriptorSamples != SampleCountFlags.Count1Bit ||
            (source.DescriptorUsage & ImageUsageFlags.TransferSrcBit) == 0 ||
            source is not IVkFrameBufferAttachmentSource attachment ||
            !attachment.TryGetAttachmentExtent(0, 0, out Extent2D extent))
            return false;
        ulong generation = _resourceRuntime.GetPublishedGeneration(ObjectType.Image, source.DescriptorImage.Handle);
        if (generation == 0 || extent.Width == 0 || extent.Height == 0)
            return false;
        description = new(source.DescriptorImage.Handle, generation, source.DescriptorGeneration,
            extent.Width, extent.Height, source.DescriptorMipLevels);
        return true;
    }

    internal bool TryReadbackExplicitTextureMipRows(
        in VulkanExplicitProductionSubmissionReceipt receipt,
        XRTexture2D texture,
        in VulkanNativeTextureDiagnosticDescription expected,
        uint mipLevel,
        int firstRow,
        int rowCount,
        out byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(texture);
        rgba = [];
        if (!HasExplicitFrameTarget || !RuntimeEngine.IsRenderThread ||
            _resourceRuntime.WrapperLookup.GetOrCreate(texture, generateNow: false) is not IVkImageDescriptorSource source)
            return false;

        if (!_frameLoop.TryEnterExplicitTextureDiagnostic(in receipt))
            return false;
        try
        {
            return TryReadbackExplicitTextureMipRowsCore(source, in expected, mipLevel, firstRow, rowCount, out rgba);
        }
        finally
        {
            _frameLoop.ExitExplicitTextureDiagnostic();
        }
    }

    private bool TryReadbackExplicitTextureMipRowsCore(
        IVkImageDescriptorSource source,
        in VulkanNativeTextureDiagnosticDescription expected,
        uint mipLevel,
        int firstRow,
        int rowCount,
        out byte[] rgba)
    {
        rgba = [];
        // Native generation ownership closes lookup-to-submit retirement; the
        // descriptor lock separately excludes replacement and layout changes.
        lock (source.DescriptorSnapshotSyncRoot)
        {
            if (!TryDescribeExplicitNativeTextureSource(source, out var current) ||
                current != expected || mipLevel >= current.MipLevels || mipLevel >= 32 ||
                source is not IVkFrameBufferAttachmentSource attachment ||
                !attachment.TryGetAttachmentExtent(checked((int)mipLevel), 0, out Extent2D extent) ||
                extent.Width > int.MaxValue || firstRow < 0 || rowCount <= 0 ||
                (ulong)firstRow + (ulong)rowCount > extent.Height ||
                (ulong)extent.Width * (ulong)rowCount * 4UL > 1024UL * 1024UL)
                return false;
            ImageLayout layout = attachment.GetAttachmentTrackedLayout(checked((int)mipLevel), 0);
            if (layout is not (ImageLayout.ShaderReadOnlyOptimal or ImageLayout.General))
                return false;
            Span<VulkanResidentTemplateDependencyRequest> dependencies = stackalloc VulkanResidentTemplateDependencyRequest[1];
            dependencies[0] = new(EVulkanResidentTemplateDependencyKind.Image, current.ImageHandle, current.PublishedGeneration);
            if (!_resourceRuntime.TryAcquireResidentTemplateDependencies(dependencies, out var nativeLease, out _))
                return false;
            using var ownedNativeLease = nativeLease;
            BlitImageInfo info = new(source.DescriptorImage, source.DescriptorFormat,
                ImageAspectFlags.ColorBit, 0, 1, mipLevel, extent, layout,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit, source);
            return _commandRuntime.TryReadColorRegionRgba8(
                in info, 0, firstRow, checked((int)extent.Width), rowCount, out rgba);
        }
    }
}
