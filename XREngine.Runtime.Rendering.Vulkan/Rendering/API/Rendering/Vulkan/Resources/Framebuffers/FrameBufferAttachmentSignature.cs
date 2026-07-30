using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly struct FrameBufferAttachmentSignature : IEquatable<FrameBufferAttachmentSignature>
{
    public FrameBufferAttachmentSignature(
        Format format,
        SampleCountFlags samples,
        ImageAspectFlags aspectMask,
        AttachmentRole role,
        uint colorIndex,
        AttachmentLoadOp loadOp,
        AttachmentStoreOp storeOp,
        AttachmentLoadOp stencilLoadOp,
        AttachmentStoreOp stencilStoreOp,
        ImageLayout initialLayout,
        ImageLayout finalLayout,
        ImageLayout referenceLayout)
    {
        Format = format;
        Samples = samples;
        AspectMask = aspectMask;
        Role = role;
        ColorIndex = colorIndex;
        // Vulkan cannot preserve contents from UNDEFINED. Normalize stale or
        // first-use planner state here so both legacy render passes and dynamic
        // rendering receive a valid, deterministic load operation.
        LoadOp = initialLayout == ImageLayout.Undefined && loadOp == AttachmentLoadOp.Load
            ? AttachmentLoadOp.DontCare
            : loadOp;
        StoreOp = storeOp;
        StencilLoadOp = initialLayout == ImageLayout.Undefined && stencilLoadOp == AttachmentLoadOp.Load
            ? AttachmentLoadOp.DontCare
            : stencilLoadOp;
        StencilStoreOp = stencilStoreOp;
        InitialLayout = initialLayout;
        FinalLayout = finalLayout;
        ReferenceLayout = referenceLayout;
    }

    public Format Format { get; }
    public SampleCountFlags Samples { get; }
    public ImageAspectFlags AspectMask { get; }
    public AttachmentRole Role { get; }
    public uint ColorIndex { get; }
    public AttachmentLoadOp LoadOp { get; }
    public AttachmentStoreOp StoreOp { get; }
    public AttachmentLoadOp StencilLoadOp { get; }
    public AttachmentStoreOp StencilStoreOp { get; }
    public ImageLayout InitialLayout { get; }
    public ImageLayout FinalLayout { get; }
    public ImageLayout ReferenceLayout { get; }

    public AttachmentDescription ToAttachmentDescription()
        => new()
        {
            Format = Format,
            Samples = Samples,
            LoadOp = LoadOp,
            StoreOp = StoreOp,
            StencilLoadOp = StencilLoadOp,
            StencilStoreOp = StencilStoreOp,
            InitialLayout = InitialLayout,
            FinalLayout = FinalLayout,
        };

    public AttachmentReference ToAttachmentReference(uint attachmentIndex)
        => new()
        {
            Attachment = attachmentIndex,
            Layout = ReferenceLayout,
        };

    public bool Equals(FrameBufferAttachmentSignature other)
    {
        return Format == other.Format &&
               Samples == other.Samples &&
               AspectMask == other.AspectMask &&
               Role == other.Role &&
               ColorIndex == other.ColorIndex &&
               LoadOp == other.LoadOp &&
               StoreOp == other.StoreOp &&
               StencilLoadOp == other.StencilLoadOp &&
               StencilStoreOp == other.StencilStoreOp &&
               InitialLayout == other.InitialLayout &&
               FinalLayout == other.FinalLayout &&
               ReferenceLayout == other.ReferenceLayout;
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add((int)Format);
        hash.Add((int)Samples);
        hash.Add((int)AspectMask);
        hash.Add((int)Role);
        hash.Add(ColorIndex);
        hash.Add((int)LoadOp);
        hash.Add((int)StoreOp);
        hash.Add((int)StencilLoadOp);
        hash.Add((int)StencilStoreOp);
        hash.Add((int)InitialLayout);
        hash.Add((int)FinalLayout);
        hash.Add((int)ReferenceLayout);
        return hash.ToHashCode();
    }
}
