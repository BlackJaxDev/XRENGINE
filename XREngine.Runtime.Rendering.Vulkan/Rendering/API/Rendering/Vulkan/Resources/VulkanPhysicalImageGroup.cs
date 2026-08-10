using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanPhysicalImageGroup
{
    private readonly List<VulkanImageAllocation> _logicalResources = new();
    private Image _image;
    private DeviceMemory _memory;
    private bool _allocated;
    private ImageLayout _lastKnownLayout = ImageLayout.Undefined;
    private readonly Dictionary<SubresourceLayoutKey, ImageLayout> _subresourceLayouts = new();

    internal VulkanPhysicalImageGroup(
        VulkanImageAliasGroup logicalGroup,
        Extent3D extent,
        Format format,
        ImageUsageFlags usage,
        uint mipLevels,
        SampleCountFlags samples,
        MemoryPropertyFlags memoryProperties,
        VulkanTransientAttachmentPolicy transientAttachmentPolicy)
    {
        Key = logicalGroup.Key;
        AllowsAliasing = logicalGroup.AllowsAliasing;
        Template = logicalGroup.CreateInfoTemplate;
        ResolvedExtent = extent;
        Format = format;
        Usage = usage;
        MipLevels = Math.Max(1u, mipLevels);
        Samples = samples;
        MemoryProperties = memoryProperties;
        TransientAttachmentPolicy = transientAttachmentPolicy;
    }

    public VulkanAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; }
    public VulkanImageCreateTemplate Template { get; }
    public Extent3D ResolvedExtent { get; }
    public Format Format { get; }
    public ImageUsageFlags Usage { get; }
    public uint MipLevels { get; }
    public SampleCountFlags Samples { get; }
    public MemoryPropertyFlags MemoryProperties { get; }
    public VulkanTransientAttachmentPolicy TransientAttachmentPolicy { get; }
    public IReadOnlyList<VulkanImageAllocation> LogicalResources => _logicalResources;
    public bool IsAllocated => _allocated;
    public Image Image => _image;
    public DeviceMemory Memory => _memory;

    /// The last layout this image was transitioned to via a pipeline barrier or
    /// render pass. Used to provide the correct <c>oldLayout</c> in blit and
    /// transfer barriers so that the validation layer does not flag a mismatch
    /// with the actual GPU-side layout.
    /// </summary>
    public ImageLayout LastKnownLayout
    {
        get => _lastKnownLayout;
        internal set
        {
            _lastKnownLayout = value;
            _subresourceLayouts.Clear();
        }
    }

    public ImageLayout GetKnownLayout(uint baseMipLevel, uint levelCount, uint baseArrayLayer, uint layerCount)
    {
        ResolveSubresourceRange(
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            out uint resolvedBaseMip,
            out uint resolvedLevelCount,
            out uint resolvedBaseLayer,
            out uint resolvedLayerCount);

        if (_subresourceLayouts.Count == 0)
            return _lastKnownLayout;

        ImageLayout? common = null;
        for (uint mip = resolvedBaseMip; mip < resolvedBaseMip + resolvedLevelCount; mip++)
        {
            for (uint layer = resolvedBaseLayer; layer < resolvedBaseLayer + resolvedLayerCount; layer++)
            {
                if (!_subresourceLayouts.TryGetValue(new SubresourceLayoutKey(mip, layer), out ImageLayout layout) ||
                    layout == ImageLayout.Undefined)
                {
                    return ImageLayout.Undefined;
                }

                if (common.HasValue && common.Value != layout)
                    return ImageLayout.Undefined;

                common = layout;
            }
        }

        return common ?? ImageLayout.Undefined;
    }

    internal LayoutSnapshot CaptureLayoutSnapshot()
    {
        if (_subresourceLayouts.Count == 0)
            return new LayoutSnapshot(_lastKnownLayout, Array.Empty<SubresourceLayoutSnapshot>());

        SubresourceLayoutSnapshot[] subresources = new SubresourceLayoutSnapshot[_subresourceLayouts.Count];
        int index = 0;
        foreach (KeyValuePair<SubresourceLayoutKey, ImageLayout> pair in _subresourceLayouts)
        {
            subresources[index++] = new SubresourceLayoutSnapshot(
                pair.Key.MipLevel,
                pair.Key.ArrayLayer,
                pair.Value);
        }

        Array.Sort(
            subresources,
            static (left, right) =>
            {
                int mipCompare = left.MipLevel.CompareTo(right.MipLevel);
                return mipCompare != 0
                    ? mipCompare
                    : left.ArrayLayer.CompareTo(right.ArrayLayer);
            });

        return new LayoutSnapshot(_lastKnownLayout, subresources);
    }

    /// <summary>
    /// Appends the tracked layout state in deterministic mip/layer order without
    /// allocating the restoration snapshot used by explicit save/restore paths.
    /// </summary>
    internal void AppendLayoutSignature(ref FrameOpSignatureHasher hash)
    {
        hash.Add((int)_lastKnownLayout);
        hash.Add(_subresourceLayouts.Count);

        uint layers = Math.Max(Template.Layers, 1u);
        for (uint mipLevel = 0; mipLevel < MipLevels; mipLevel++)
        {
            for (uint arrayLayer = 0; arrayLayer < layers; arrayLayer++)
            {
                if (!_subresourceLayouts.TryGetValue(
                        new SubresourceLayoutKey(mipLevel, arrayLayer),
                        out ImageLayout layout))
                {
                    continue;
                }

                hash.Add(mipLevel);
                hash.Add(arrayLayer);
                hash.Add((int)layout);
            }
        }
    }

    internal void RestoreLayoutSnapshot(in LayoutSnapshot snapshot)
    {
        _lastKnownLayout = snapshot.LastKnownLayout;
        _subresourceLayouts.Clear();

        SubresourceLayoutSnapshot[] subresources = snapshot.Subresources;
        for (int i = 0; i < subresources.Length; i++)
        {
            SubresourceLayoutSnapshot subresource = subresources[i];
            _subresourceLayouts[new SubresourceLayoutKey(subresource.MipLevel, subresource.ArrayLayer)] =
                subresource.Layout;
        }
    }

    public void UpdateKnownLayout(ImageLayout layout, uint baseMipLevel, uint levelCount, uint baseArrayLayer, uint layerCount)
    {
        ResolveSubresourceRange(
            baseMipLevel,
            levelCount,
            baseArrayLayer,
            layerCount,
            out uint resolvedBaseMip,
            out uint resolvedLevelCount,
            out uint resolvedBaseLayer,
            out uint resolvedLayerCount);

        if (CoversWholeImage(resolvedBaseMip, resolvedLevelCount, resolvedBaseLayer, resolvedLayerCount))
        {
            LastKnownLayout = layout;
            return;
        }

        BeginPartialLayoutTracking();

        for (uint mip = resolvedBaseMip; mip < resolvedBaseMip + resolvedLevelCount; mip++)
        {
            for (uint layer = resolvedBaseLayer; layer < resolvedBaseLayer + resolvedLayerCount; layer++)
                _subresourceLayouts[new SubresourceLayoutKey(mip, layer)] = layout;
        }

        UpdateWholeLayoutFromSubresources();
    }

    internal void AddLogical(VulkanImageAllocation allocation)
        => _logicalResources.Add(allocation);

    internal void ReplaceLogicalResources(IReadOnlyList<VulkanImageAllocation> allocations)
    {
        _logicalResources.Clear();
        for (int i = 0; i < allocations.Count; i++)
            _logicalResources.Add(allocations[i]);
    }

    internal bool CanReusePhysicalAllocationFrom(VulkanPhysicalImageGroup previousGroup)
        => previousGroup.IsAllocated &&
           !_allocated &&
           Key.Equals(previousGroup.Key) &&
           AllowsAliasing == previousGroup.AllowsAliasing &&
           ResolvedExtent.Width == previousGroup.ResolvedExtent.Width &&
           ResolvedExtent.Height == previousGroup.ResolvedExtent.Height &&
           ResolvedExtent.Depth == previousGroup.ResolvedExtent.Depth &&
           Format == previousGroup.Format &&
           Usage == previousGroup.Usage &&
           MipLevels == previousGroup.MipLevels &&
           Samples == previousGroup.Samples &&
           MemoryProperties == previousGroup.MemoryProperties &&
           TransientAttachmentPolicy == previousGroup.TransientAttachmentPolicy &&
           Template.Layers == previousGroup.Template.Layers;

    public void EnsureAllocated(VulkanBackendObjectContext context)
    {
        if (_allocated)
            return;

        if (!context.Resources.Images.TryAllocatePhysicalImage(context, this, ref _image, ref _memory, out string failureReason))
            throw new VulkanOutOfMemoryException(failureReason, MemoryProperties);
        _allocated = true;
        LastKnownLayout = ImageLayout.Undefined;
    }

    internal bool TryEnsureAllocated(VulkanBackendObjectContext context, out string failureReason)
    {
        failureReason = string.Empty;
        if (_allocated)
            return true;
        if (!context.Resources.Images.TryAllocatePhysicalImage(context, this, ref _image, ref _memory, out failureReason))
            return false;

        _allocated = true;
        LastKnownLayout = ImageLayout.Undefined;
        return true;
    }

    public void Destroy(VulkanBackendObjectContext context)
    {
        if (!_allocated)
            return;

        context.Resources.Images.RetireOwnedResources(
            new RetiredImageResources(_image, _memory, default, [], default, 0),
            $"ResourcePlanner.{Key}");
        _image = default;
        _memory = default;
        _allocated = false;
        LastKnownLayout = ImageLayout.Undefined;
    }

    public void DestroyImmediate(VulkanBackendObjectContext context)
    {
        if (!_allocated)
            return;

        bool hasAllocation = context.Resources.Allocations.Images.Allocations.TryRemove(_image.Handle, out VulkanMemoryAllocation allocation);
        context.Resources.Images.DestroyUnpublishedOwnedImage(context, _image, $"ResourcePlanner.{Key}");
        if (hasAllocation)
            context.Resources.Images.FreeMemory(context, in allocation);
        _image = default;
        _memory = default;
        _allocated = false;
        LastKnownLayout = ImageLayout.Undefined;
    }

    private void BeginPartialLayoutTracking()
    {
        if (_subresourceLayouts.Count > 0 || _lastKnownLayout == ImageLayout.Undefined)
        {
            _lastKnownLayout = ImageLayout.Undefined;
            return;
        }

        uint mipLevels = Math.Max(MipLevels, 1u);
        uint layerCount = Math.Max(Template.Layers, 1u);
        for (uint mip = 0; mip < mipLevels; mip++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
                _subresourceLayouts[new SubresourceLayoutKey(mip, layer)] = _lastKnownLayout;
        }

        _lastKnownLayout = ImageLayout.Undefined;
    }

    private void UpdateWholeLayoutFromSubresources()
    {
        ImageLayout? common = null;
        uint mipLevels = Math.Max(MipLevels, 1u);
        uint layerCount = Math.Max(Template.Layers, 1u);
        for (uint mip = 0; mip < mipLevels; mip++)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                if (!_subresourceLayouts.TryGetValue(new SubresourceLayoutKey(mip, layer), out ImageLayout layout) ||
                    layout == ImageLayout.Undefined)
                {
                    _lastKnownLayout = ImageLayout.Undefined;
                    return;
                }

                if (common.HasValue && common.Value != layout)
                {
                    _lastKnownLayout = ImageLayout.Undefined;
                    return;
                }

                common = layout;
            }
        }

        if (common.HasValue && Math.Max(MipLevels, 1u) == 1u && Math.Max(Template.Layers, 1u) == 1u)
        {
            _lastKnownLayout = common.Value;
            _subresourceLayouts.Clear();
        }
        else
        {
            _lastKnownLayout = ImageLayout.Undefined;
        }
    }

    private bool CoversWholeImage(uint baseMipLevel, uint levelCount, uint baseArrayLayer, uint layerCount)
        => baseMipLevel == 0u &&
           levelCount >= Math.Max(MipLevels, 1u) &&
           baseArrayLayer == 0u &&
           layerCount >= Math.Max(Template.Layers, 1u);

    private void ResolveSubresourceRange(
        uint baseMipLevel,
        uint levelCount,
        uint baseArrayLayer,
        uint layerCount,
        out uint resolvedBaseMipLevel,
        out uint resolvedLevelCount,
        out uint resolvedBaseArrayLayer,
        out uint resolvedLayerCount)
    {
        uint mipLevels = Math.Max(MipLevels, 1u);
        uint layers = Math.Max(Template.Layers, 1u);
        resolvedBaseMipLevel = Math.Min(baseMipLevel, mipLevels - 1u);
        resolvedBaseArrayLayer = Math.Min(baseArrayLayer, layers - 1u);
        resolvedLevelCount = levelCount == uint.MaxValue
            ? mipLevels - resolvedBaseMipLevel
            : Math.Min(Math.Max(levelCount, 1u), mipLevels - resolvedBaseMipLevel);
        resolvedLayerCount = layerCount == uint.MaxValue
            ? layers - resolvedBaseArrayLayer
            : Math.Min(Math.Max(layerCount, 1u), layers - resolvedBaseArrayLayer);
    }

    private readonly record struct SubresourceLayoutKey(uint MipLevel, uint ArrayLayer);

    internal readonly record struct SubresourceLayoutSnapshot(uint MipLevel, uint ArrayLayer, ImageLayout Layout);

    internal readonly record struct LayoutSnapshot(
        ImageLayout LastKnownLayout,
        SubresourceLayoutSnapshot[] Subresources);
}

