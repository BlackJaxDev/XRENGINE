using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Resources;

/// <summary>
/// Defines a predicate delegate for filtering render pipeline resource profiles based on custom criteria.
/// </summary>
/// <param name="profile">The render pipeline resource profile to evaluate.</param>
/// <returns>True if the profile matches the criteria; otherwise, false.</returns>
public delegate bool RenderPipelineResourcePredicate(RenderPipelineResourceProfile profile);

/// <summary>
/// Represents the layout of resources used in a render pipeline, including their specifications and organization.
/// </summary>
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

    /// <summary>
    /// Gets an empty render pipeline resource layout with no resources or specifications.
    /// </summary>
    public static RenderPipelineResourceLayout Empty { get; } = new(
        RenderPipelineResourceProfile.Empty,
        Array.Empty<RenderPipelineResourceSpec>(),
        new ReadOnlyDictionary<string, RenderPipelineResourceSpec>(
            new Dictionary<string, RenderPipelineResourceSpec>(StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// Gets the render pipeline resource profile associated with this layout, which defines the characteristics and constraints of the resources used in the render pipeline.
    /// </summary>
    public RenderPipelineResourceProfile Profile { get; }
    /// <summary>
    /// Gets the ordered list of resource specifications that define the resources used in the render pipeline, including their types, usage patterns, and other relevant details.
    /// </summary>
    public IReadOnlyList<RenderPipelineResourceSpec> OrderedSpecs { get; }
    /// <summary>
    /// Gets a read-only dictionary that maps resource names to their corresponding specifications, allowing for efficient lookup of resource specifications by name.
    /// </summary>
    public IReadOnlyDictionary<string, RenderPipelineResourceSpec> ResourcesByName => _byName;

    /// <summary>
    /// Attempts to retrieve the resource specification associated with the specified resource name. Returns true if the resource specification is found; otherwise, false.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="spec">When this method returns, contains the resource specification associated with the specified name, if found; otherwise, null.</param>
    /// <returns>True if the resource specification is found; otherwise, false.</returns>
    public bool TryGet(string name, [NotNullWhen(true)] out RenderPipelineResourceSpec? spec)
        => _byName.TryGetValue(name, out spec);

    /// <summary>
    /// Determines whether the current render pipeline resource layout is structurally equivalent to another layout, based on their profiles and resource specifications. Two layouts are considered structurally equivalent if they have the same profile and the same number of resource specifications, and each corresponding specification is structurally equivalent.
    /// </summary>
    /// <param name="other">The other render pipeline resource layout to compare with.</param>
    /// <returns>True if the layouts are structurally equivalent; otherwise, false.</returns>
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

    public string DescribeStructuralDifferenceTo(RenderPipelineResourceLayout? other)
    {
        if (other is null)
            return "other layout is null";
        if (Profile != other.Profile)
            return $"profile differs: {Profile} -> {other.Profile}";
        if (OrderedSpecs.Count != other.OrderedSpecs.Count)
            return $"spec count differs: {OrderedSpecs.Count} -> {other.OrderedSpecs.Count}";

        for (int i = 0; i < OrderedSpecs.Count; i++)
        {
            RenderPipelineResourceSpec left = OrderedSpecs[i];
            RenderPipelineResourceSpec right = other.OrderedSpecs[i];
            string? difference = DescribeSpecStructuralDifference(left, right);
            if (difference is not null)
                return $"spec[{i}] '{left.Name}' -> '{right.Name}': {difference}";
        }

        return "none";
    }

    /// <summary>
    /// Gets the descriptors for all texture resources in the render pipeline.
    /// </summary>
    /// <returns>An enumerable of texture resource descriptors.</returns>
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

    /// <summary>
    /// Gets the descriptors for all frame buffer resources in the render pipeline.
    /// </summary>
    /// <returns>An enumerable of frame buffer resource descriptors.</returns>
    public IEnumerable<FrameBufferResourceDescriptor> LowerFrameBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
        {
            if (spec is FrameBufferSpec frameBufferSpec)
                yield return frameBufferSpec.ToDescriptor();
            else if (spec is QuadMaterialSpec quadMaterialSpec)
                yield return new FrameBufferResourceDescriptor(
                    quadMaterialSpec.Name,
                    quadMaterialSpec.Lifetime,
                    RenderResourceSizePolicy.Absolute(0u, 0u),
                    Array.Empty<FrameBufferAttachmentDescriptor>());
        }
    }

    /// <summary>
    /// Gets the descriptors for all render buffer resources in the render pipeline.
    /// </summary>
    /// <returns>An enumerable of render buffer resource descriptors.</returns>
    public IEnumerable<RenderBufferResourceDescriptor> LowerRenderBufferDescriptors()
    {
        foreach (RenderPipelineResourceSpec spec in OrderedSpecs)
            if (spec is RenderBufferSpec renderBufferSpec)
                yield return renderBufferSpec.ToDescriptor();
    }

    /// <summary>
    /// Gets the descriptors for all buffer resources in the render pipeline.
    /// </summary>
    /// <returns>An enumerable of buffer resource descriptors.</returns>
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
            ExternalResourceSpec leftExternal when right is ExternalResourceSpec rightExternal =>
                leftExternal.ExternalKind == rightExternal.ExternalKind &&
                leftExternal.Ownership == rightExternal.Ownership &&
                leftExternal.Synchronization == rightExternal.Synchronization,
            _ => left.GetType() == right.GetType(),
        };
    }

    private static string? DescribeSpecStructuralDifference(RenderPipelineResourceSpec left, RenderPipelineResourceSpec right)
    {
        string? baseDifference = DescribeBaseSpecFieldDifference(left, right);
        if (baseDifference is not null)
            return baseDifference;

        if (left.GetType() != right.GetType())
            return $"type differs: {left.GetType().Name} -> {right.GetType().Name}";

        return left switch
        {
            TextureSpec leftTexture when right is TextureSpec rightTexture => DescribeTextureSpecDifference(leftTexture, rightTexture),
            TextureViewSpec leftView when right is TextureViewSpec rightView => DescribeTextureViewSpecDifference(leftView, rightView),
            RenderBufferSpec leftRenderBuffer when right is RenderBufferSpec rightRenderBuffer => DescribeRenderBufferSpecDifference(leftRenderBuffer, rightRenderBuffer),
            BufferSpec leftBuffer when right is BufferSpec rightBuffer => DescribeBufferSpecDifference(leftBuffer, rightBuffer),
            FrameBufferSpec leftFrameBuffer when right is FrameBufferSpec rightFrameBuffer => DescribeFrameBufferSpecDifference(leftFrameBuffer, rightFrameBuffer),
            _ => null,
        };
    }

    private static string? DescribeBaseSpecFieldDifference(RenderPipelineResourceSpec left, RenderPipelineResourceSpec right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase))
            return $"name differs: {left.Name} -> {right.Name}";
        if (left.Kind != right.Kind)
            return $"kind differs: {left.Kind} -> {right.Kind}";
        if (left.Lifetime != right.Lifetime)
            return $"lifetime differs: {left.Lifetime} -> {right.Lifetime}";
        if (left.SizePolicy != right.SizePolicy)
            return $"size policy differs: {left.SizePolicy} -> {right.SizePolicy}";
        if (left.Usage != right.Usage)
            return $"usage differs: {left.Usage} -> {right.Usage}";
        if (left.HistoryPolicy != right.HistoryPolicy)
            return $"history policy differs: {left.HistoryPolicy} -> {right.HistoryPolicy}";
        if (left.Required != right.Required)
            return $"required differs: {left.Required} -> {right.Required}";
        if (!string.Equals(left.DebugLabel, right.DebugLabel, StringComparison.Ordinal))
            return $"debug label differs: {left.DebugLabel ?? "<null>"} -> {right.DebugLabel ?? "<null>"}";
        if (!AreStringListsEquivalent(left.Dependencies, right.Dependencies))
            return $"dependencies differ: {string.Join("|", left.Dependencies)} -> {string.Join("|", right.Dependencies)}";

        return null;
    }

    private static string? DescribeTextureSpecDifference(TextureSpec left, TextureSpec right)
    {
        if (left.InternalFormat != right.InternalFormat)
            return $"internal format differs: {left.InternalFormat} -> {right.InternalFormat}";
        if (left.PixelFormat != right.PixelFormat)
            return $"pixel format differs: {left.PixelFormat} -> {right.PixelFormat}";
        if (left.PixelType != right.PixelType)
            return $"pixel type differs: {left.PixelType} -> {right.PixelType}";
        if (left.SizedInternalFormat != right.SizedInternalFormat)
            return $"sized format differs: {left.SizedInternalFormat} -> {right.SizedInternalFormat}";
        if (left.Samples != right.Samples)
            return $"samples differ: {left.Samples} -> {right.Samples}";
        if (left.Layers != right.Layers)
            return $"layers differ: {left.Layers} -> {right.Layers}";
        if (left.MipPolicy != right.MipPolicy)
            return $"mip policy differs: {left.MipPolicy} -> {right.MipPolicy}";
        if (left.StereoCompatible != right.StereoCompatible)
            return $"stereo compatible differs: {left.StereoCompatible} -> {right.StereoCompatible}";
        if (left.RequiresStorageUsage != right.RequiresStorageUsage)
            return $"storage usage differs: {left.RequiresStorageUsage} -> {right.RequiresStorageUsage}";

        return null;
    }

    private static string? DescribeTextureViewSpecDifference(TextureViewSpec left, TextureViewSpec right)
    {
        if (!string.Equals(left.SourceTextureName, right.SourceTextureName, StringComparison.OrdinalIgnoreCase))
            return $"source texture differs: {left.SourceTextureName} -> {right.SourceTextureName}";
        if (left.BaseMipLevel != right.BaseMipLevel)
            return $"base mip differs: {left.BaseMipLevel} -> {right.BaseMipLevel}";
        if (left.MipLevelCount != right.MipLevelCount)
            return $"mip count differs: {left.MipLevelCount} -> {right.MipLevelCount}";
        if (left.BaseLayer != right.BaseLayer)
            return $"base layer differs: {left.BaseLayer} -> {right.BaseLayer}";
        if (left.LayerCount != right.LayerCount)
            return $"layer count differs: {left.LayerCount} -> {right.LayerCount}";
        if (left.SizedInternalFormat != right.SizedInternalFormat)
            return $"sized format differs: {left.SizedInternalFormat} -> {right.SizedInternalFormat}";
        if (left.DepthStencilAspect != right.DepthStencilAspect)
            return $"depth/stencil aspect differs: {left.DepthStencilAspect} -> {right.DepthStencilAspect}";
        if (left.ArrayTarget != right.ArrayTarget)
            return $"array target differs: {left.ArrayTarget} -> {right.ArrayTarget}";
        if (left.Multisample != right.Multisample)
            return $"multisample differs: {left.Multisample} -> {right.Multisample}";

        return null;
    }

    private static string? DescribeRenderBufferSpecDifference(RenderBufferSpec left, RenderBufferSpec right)
    {
        if (left.StorageFormat != right.StorageFormat)
            return $"storage format differs: {left.StorageFormat} -> {right.StorageFormat}";
        if (left.Samples != right.Samples)
            return $"samples differ: {left.Samples} -> {right.Samples}";
        if (left.DefaultAttachment != right.DefaultAttachment)
            return $"default attachment differs: {left.DefaultAttachment} -> {right.DefaultAttachment}";

        return null;
    }

    private static string? DescribeBufferSpecDifference(BufferSpec left, BufferSpec right)
    {
        if (left.SizeInBytes != right.SizeInBytes)
            return $"size differs: {left.SizeInBytes} -> {right.SizeInBytes}";
        if (left.Target != right.Target)
            return $"target differs: {left.Target} -> {right.Target}";
        if (left.BufferUsage != right.BufferUsage)
            return $"buffer usage differs: {left.BufferUsage} -> {right.BufferUsage}";
        if (left.ElementStride != right.ElementStride)
            return $"element stride differs: {left.ElementStride} -> {right.ElementStride}";
        if (left.ElementCount != right.ElementCount)
            return $"element count differs: {left.ElementCount} -> {right.ElementCount}";
        if (left.AccessPattern != right.AccessPattern)
            return $"access pattern differs: {left.AccessPattern} -> {right.AccessPattern}";

        return null;
    }

    private static string? DescribeFrameBufferSpecDifference(FrameBufferSpec left, FrameBufferSpec right)
    {
        if (left.Attachments.Count != right.Attachments.Count)
            return $"attachment count differs: {left.Attachments.Count} -> {right.Attachments.Count}";

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
            {
                return $"attachment[{i}] differs: {DescribeAttachment(leftAttachment)} -> {DescribeAttachment(rightAttachment)}";
            }
        }

        return null;
    }

    private static string DescribeAttachment(FrameBufferAttachmentDescriptor attachment)
        => $"{attachment.Attachment}:{attachment.ResourceName}:mip={attachment.MipLevel}:layer={attachment.LayerIndex}";

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
