using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

public delegate bool RenderPipelineResourcePredicate(RenderPipelineResourceProfile profile);

public sealed class RenderPipelineResourceLayout
{
    private readonly IReadOnlyDictionary<string, RenderPipelineResourceSpec> _byName;

    internal RenderPipelineResourceLayout(
        RenderPipelineResourceProfile profile,
        IReadOnlyList<RenderPipelineResourceSpec> orderedSpecs,
        IReadOnlyDictionary<string, RenderPipelineResourceSpec> byName)
    {
        Profile = profile;
        OrderedSpecs = orderedSpecs;
        _byName = byName;
    }

    public static RenderPipelineResourceLayout Empty { get; } = new(
        RenderPipelineResourceProfile.Empty,
        Array.Empty<RenderPipelineResourceSpec>(),
        new ReadOnlyDictionary<string, RenderPipelineResourceSpec>(
            new Dictionary<string, RenderPipelineResourceSpec>(StringComparer.OrdinalIgnoreCase)));

    public RenderPipelineResourceProfile Profile { get; }
    public IReadOnlyList<RenderPipelineResourceSpec> OrderedSpecs { get; }
    public IReadOnlyDictionary<string, RenderPipelineResourceSpec> ResourcesByName => _byName;

    public bool TryGet(string name, [NotNullWhen(true)] out RenderPipelineResourceSpec? spec)
        => _byName.TryGetValue(name, out spec);

    public bool IsStructurallyEquivalentTo(RenderPipelineResourceLayout? other)
    {
        if (other is null ||
            Profile != other.Profile ||
            OrderedSpecs.Count != other.OrderedSpecs.Count)
        {
            return false;
        }

        for (int i = 0; i < OrderedSpecs.Count; i++)
            if (!AreSpecsStructurallyEquivalent(OrderedSpecs[i], other.OrderedSpecs[i]))
                return false;

        return true;
    }

    public IEnumerable<TextureResourceDescriptor> LowerTextureDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
        {
            if (spec is TextureSpec textureSpec)
                yield return textureSpec.ToDescriptor();
            else if (spec is TextureViewSpec viewSpec)
                yield return viewSpec.ToDescriptor();
        }
    }

    public IEnumerable<FrameBufferResourceDescriptor> LowerFrameBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is FrameBufferSpec frameBufferSpec)
                yield return frameBufferSpec.ToDescriptor();
    }

    public IEnumerable<RenderBufferResourceDescriptor> LowerRenderBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is RenderBufferSpec renderBufferSpec)
                yield return renderBufferSpec.ToDescriptor();
    }

    public IEnumerable<BufferResourceDescriptor> LowerBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is BufferSpec bufferSpec)
                yield return bufferSpec.ToDescriptor();
    }

    private static bool AreSpecsStructurallyEquivalent(RenderPipelineResourceSpec left, RenderPipelineResourceSpec right)
    {
        if (!AreBaseSpecFieldsEquivalent(left, right))
            return false;

        return left switch
        {
            TextureSpec leftTexture when right is TextureSpec rightTexture => AreTextureSpecsEquivalent(leftTexture, rightTexture),
            TextureViewSpec leftView when right is TextureViewSpec rightView => AreTextureViewSpecsEquivalent(leftView, rightView),
            RenderBufferSpec leftRenderBuffer when right is RenderBufferSpec rightRenderBuffer => AreRenderBufferSpecsEquivalent(leftRenderBuffer, rightRenderBuffer),
            BufferSpec leftBuffer when right is BufferSpec rightBuffer => AreBufferSpecsEquivalent(leftBuffer, rightBuffer),
            FrameBufferSpec leftFrameBuffer when right is FrameBufferSpec rightFrameBuffer => AreFrameBufferSpecsEquivalent(leftFrameBuffer, rightFrameBuffer),
            QuadMaterialSpec when right is QuadMaterialSpec => true,
            _ => left.GetType() == right.GetType(),
        };
    }

    private static bool AreBaseSpecFieldsEquivalent(RenderPipelineResourceSpec left, RenderPipelineResourceSpec right)
    {
        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
            left.Kind == right.Kind &&
            left.Lifetime == right.Lifetime &&
            left.SizePolicy == right.SizePolicy &&
            left.Usage == right.Usage &&
            left.HistoryPolicy == right.HistoryPolicy &&
            left.Required == right.Required &&
            string.Equals(left.DebugLabel, right.DebugLabel, StringComparison.Ordinal) &&
            AreStringListsEquivalent(left.Dependencies, right.Dependencies);
    }

    private static bool AreTextureSpecsEquivalent(TextureSpec left, TextureSpec right)
        => left.InternalFormat == right.InternalFormat &&
           left.PixelFormat == right.PixelFormat &&
           left.PixelType == right.PixelType &&
           left.SizedInternalFormat == right.SizedInternalFormat &&
           left.Samples == right.Samples &&
           left.Layers == right.Layers &&
           left.MipPolicy == right.MipPolicy &&
           left.StereoCompatible == right.StereoCompatible &&
           left.RequiresStorageUsage == right.RequiresStorageUsage;

    private static bool AreTextureViewSpecsEquivalent(TextureViewSpec left, TextureViewSpec right)
        => string.Equals(left.SourceTextureName, right.SourceTextureName, StringComparison.OrdinalIgnoreCase) &&
           left.BaseMipLevel == right.BaseMipLevel &&
           left.MipLevelCount == right.MipLevelCount &&
           left.BaseLayer == right.BaseLayer &&
           left.LayerCount == right.LayerCount &&
           left.SizedInternalFormat == right.SizedInternalFormat &&
           left.DepthStencilAspect == right.DepthStencilAspect &&
           left.ArrayTarget == right.ArrayTarget &&
           left.Multisample == right.Multisample;

    private static bool AreRenderBufferSpecsEquivalent(RenderBufferSpec left, RenderBufferSpec right)
        => left.StorageFormat == right.StorageFormat &&
           left.Samples == right.Samples &&
           left.DefaultAttachment == right.DefaultAttachment;

    private static bool AreBufferSpecsEquivalent(BufferSpec left, BufferSpec right)
        => left.SizeInBytes == right.SizeInBytes &&
           left.Target == right.Target &&
           left.BufferUsage == right.BufferUsage &&
           left.ElementStride == right.ElementStride &&
           left.ElementCount == right.ElementCount &&
           left.AccessPattern == right.AccessPattern;

    private static bool AreFrameBufferSpecsEquivalent(FrameBufferSpec left, FrameBufferSpec right)
    {
        if (left.Attachments.Count != right.Attachments.Count)
            return false;

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreStringListsEquivalent(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}
