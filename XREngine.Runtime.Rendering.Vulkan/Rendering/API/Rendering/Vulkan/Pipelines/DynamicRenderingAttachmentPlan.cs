using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct DynamicRenderingAttachmentPlan
{
    public DynamicRenderingAttachmentPlan(
        Image image,
        ImageView imageView,
        Format format,
        ImageAspectFlags aspectMask,
        ImageLayout initialLayout,
        ImageLayout renderingLayout,
        ImageLayout finalLayout,
        AttachmentLoadOp loadOp,
        AttachmentStoreOp storeOp,
        ClearValue clearValue,
        ImageView resolveImageView = default,
        ResolveModeFlags resolveMode = default,
        ImageLayout resolveImageLayout = ImageLayout.Undefined)
    {
        Image = image;
        ImageView = imageView;
        Format = format;
        AspectMask = aspectMask;
        InitialLayout = initialLayout;
        RenderingLayout = renderingLayout;
        FinalLayout = finalLayout;
        LoadOp = loadOp;
        StoreOp = storeOp;
        ClearValue = clearValue;
        ResolveImageView = resolveImageView;
        ResolveMode = resolveMode;
        ResolveImageLayout = resolveImageLayout;
    }

    public Image Image { get; }
    public ImageView ImageView { get; }
    public Format Format { get; }
    public ImageAspectFlags AspectMask { get; }
    public ImageLayout InitialLayout { get; }
    public ImageLayout RenderingLayout { get; }
    public ImageLayout FinalLayout { get; }
    public AttachmentLoadOp LoadOp { get; }
    public AttachmentStoreOp StoreOp { get; }
    public ClearValue ClearValue { get; }
    public ImageView ResolveImageView { get; }
    public ResolveModeFlags ResolveMode { get; }
    public ImageLayout ResolveImageLayout { get; }
    public bool HasResolveAttachment => ResolveMode != default && ResolveImageView.Handle != 0;

    public DynamicRenderingAttachmentPlan WithResolve(in DynamicRenderingAttachmentPlan resolveAttachment, ResolveModeFlags resolveMode)
        => new(
            Image,
            ImageView,
            Format,
            AspectMask,
            InitialLayout,
            RenderingLayout,
            FinalLayout,
            LoadOp,
            StoreOp,
            ClearValue,
            resolveAttachment.ImageView,
            resolveMode,
            resolveAttachment.RenderingLayout);

    public RenderingAttachmentInfo ToRenderingAttachmentInfo()
        => new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = ImageView,
            ImageLayout = RenderingLayout,
            ResolveMode = ResolveMode,
            ResolveImageView = ResolveImageView,
            ResolveImageLayout = ResolveImageLayout,
            LoadOp = LoadOp,
            StoreOp = StoreOp,
            ClearValue = ClearValue,
        };
}
