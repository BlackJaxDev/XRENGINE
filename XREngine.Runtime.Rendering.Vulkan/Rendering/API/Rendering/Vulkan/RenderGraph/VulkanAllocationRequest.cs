using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanAllocationRequest(TextureResourceDescriptor Descriptor)
{
    public string Name => Descriptor.Name;
    public RenderResourceLifetime Lifetime => Descriptor.Lifetime;
    public RenderResourceSizePolicy SizePolicy => Descriptor.SizePolicy;
    public RenderPipelineResourceUsage Usage => Descriptor.Usage;
    public ESizedInternalFormat? SizedInternalFormat => Descriptor.SizedInternalFormat;
    public EPixelInternalFormat? InternalFormat => Descriptor.InternalFormat;
    public EPixelFormat? PixelFormat => Descriptor.PixelFormat;
    public EPixelType? PixelType => Descriptor.PixelType;
    public uint Samples => Math.Max(1u, Descriptor.Samples);
    public RenderResourceMipPolicy MipPolicy
        => Descriptor.MipPolicy with { MipLevelCount = Math.Max(1u, Descriptor.MipPolicy.MipLevelCount) };
    public bool IsStereoCompatible => Descriptor.StereoCompatible;
    public VulkanTransientAttachmentPolicy TransientAttachmentPolicy => ResolveTransientAttachmentPolicy(Descriptor);

    // Physical image aliasing is temporarily disabled because aliased transient
    // images can carry incompatible layout expectations between logical resources.
    public bool SupportsAliasing => false;
    public VulkanAliasKey AliasKey => new(
        Descriptor.SizePolicy,
        Descriptor.FormatLabel,
        Descriptor.SizedInternalFormat,
        Descriptor.InternalFormat,
        Descriptor.Usage,
        Math.Max(1u, Descriptor.Samples),
        Math.Max(1u, Descriptor.MipPolicy.MipLevelCount),
        Descriptor.ArrayLayers,
        Descriptor.StereoCompatible,
        Descriptor.RequiresStorageUsage);

    private static VulkanTransientAttachmentPolicy ResolveTransientAttachmentPolicy(TextureResourceDescriptor descriptor)
    {
        if (descriptor.Lifetime != RenderResourceLifetime.Transient)
            return VulkanTransientAttachmentPolicy.None;

        RenderPipelineResourceUsage usage = descriptor.Usage;
        bool isAttachment =
            (usage & (RenderPipelineResourceUsage.ColorAttachment |
                      RenderPipelineResourceUsage.DepthStencilAttachment)) != 0;
        bool requiresPersistentShaderOrTransferAccess =
            (usage & (RenderPipelineResourceUsage.SampledTexture |
                      RenderPipelineResourceUsage.StorageImage |
                      RenderPipelineResourceUsage.TransferSource |
                      RenderPipelineResourceUsage.TransferDestination |
                      RenderPipelineResourceUsage.PresentSource)) != 0;

        return isAttachment && !requiresPersistentShaderOrTransferAccess
            ? VulkanTransientAttachmentPolicy.PreferLazilyAllocated
            : VulkanTransientAttachmentPolicy.None;
    }
}
