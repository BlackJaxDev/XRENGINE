using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks planned Vulkan image allocations for logical graph resources.
/// Later work will use these plans to create/alias VkImage objects.
/// </summary>
internal sealed class VulkanResourceAllocator
{
    private readonly Dictionary<string, VulkanImageAllocation> _logicalTextureAllocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> _aliasGroups = new();
    private readonly Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup> _physicalGroups = new();
    private readonly Dictionary<string, VulkanPhysicalImageGroup> _resourceToPhysicalGroup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VulkanImageAllocation> LogicalTextureAllocations => _logicalTextureAllocations;
    public IReadOnlyDictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> AliasGroups => _aliasGroups;
    public IEnumerable<VulkanImageAliasGroup> EnumerateAliasGroups() => _aliasGroups.Values;
    public IEnumerable<VulkanPhysicalImageGroup> EnumeratePhysicalGroups() => _physicalGroups.Values;
    public IEnumerable<VulkanImageAllocation> EnumeratePersistentAllocations()
    {
        foreach (var pair in _logicalTextureAllocations)
            if (pair.Value.Lifetime == RenderResourceLifetime.Persistent)
                yield return pair.Value;
    }

    public void UpdatePlan(VulkanResourcePlan plan)
    {
        _logicalTextureAllocations.Clear();
        _aliasGroups.Clear();
        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();

        foreach (VulkanAllocationRequest request in plan.AllTextures())
        {
            VulkanAliasGroupKey key = new(request.AliasKey, request.Lifetime);
            if (!_aliasGroups.TryGetValue(key, out VulkanImageAliasGroup? group))
            {
                group = new VulkanImageAliasGroup(key);
                _aliasGroups.Add(key, group);
            }

            VulkanImageAllocation allocation = group.Add(request);
            _logicalTextureAllocations[request.Name] = allocation;
        }
    }

    public bool TryGetAllocation(string resourceName, out VulkanImageAllocation allocation)
        => _logicalTextureAllocations.TryGetValue(resourceName, out allocation);

    public void RebuildPhysicalPlan(VulkanRenderer renderer)
    {
        DestroyPhysicalImages(renderer);
        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();

        foreach (var group in _aliasGroups.Values)
        {
            Extent3D extent = ResolveExtent(group.CreateInfoTemplate.SizePolicy);
            Format format = ResolveFormat(group.CreateInfoTemplate.FormatLabel);
            ImageUsageFlags usage = InferUsage(group);

            VulkanPhysicalImageGroup physicalGroup = new(group, extent, format, usage);
            foreach (VulkanImageAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalGroup[allocation.Name] = physicalGroup;
            }

            _physicalGroups[group.Key] = physicalGroup;
        }
    }

    public bool TryGetPhysicalGroup(VulkanAliasGroupKey key, out VulkanPhysicalImageGroup group)
        => _physicalGroups.TryGetValue(key, out group);

    public bool TryGetPhysicalGroupForResource(string resourceName, out VulkanPhysicalImageGroup group)
        => _resourceToPhysicalGroup.TryGetValue(resourceName, out group);

    public bool TryGetImage(string resourceName, out Image image)
    {
        if (TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup group) && group.IsAllocated)
        {
            image = group.Image;
            return image.Handle != 0;
        }

        image = default;
        return false;
    }

    public bool TryEnsureImage(string resourceName, VulkanRenderer renderer, out Image image)
    {
        if (TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup group))
        {
            group.EnsureAllocated(renderer);
            image = group.Image;
            return image.Handle != 0;
        }

        image = default;
        return false;
    }

    public void AllocatePhysicalImages(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            group.EnsureAllocated(renderer);
    }

    public void DestroyPhysicalImages(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            group.Destroy(renderer);
    }

    private static Extent3D ResolveExtent(RenderResourceSizePolicy sizePolicy)
    {
        uint width;
        uint height;

        var viewport = Engine.Rendering.State.RenderingViewport;
        uint windowWidth = viewport is null ? 1u : (uint)Math.Max(1, viewport.Width);
        uint windowHeight = viewport is null ? 1u : (uint)Math.Max(1, viewport.Height);
        uint internalWidth = viewport is null ? windowWidth : (uint)Math.Max(1, viewport.InternalWidth);
        uint internalHeight = viewport is null ? windowHeight : (uint)Math.Max(1, viewport.InternalHeight);

        switch (sizePolicy.SizeClass)
        {
            case RenderResourceSizeClass.AbsolutePixels:
                width = Math.Max(sizePolicy.Width, 1u);
                height = Math.Max(sizePolicy.Height, 1u);
                break;
            case RenderResourceSizeClass.InternalResolution:
                width = (uint)Math.Max(1, (int)MathF.Round(internalWidth * sizePolicy.ScaleX));
                height = (uint)Math.Max(1, (int)MathF.Round(internalHeight * sizePolicy.ScaleY));
                break;
            case RenderResourceSizeClass.WindowResolution:
                width = (uint)Math.Max(1, (int)MathF.Round(windowWidth * sizePolicy.ScaleX));
                height = (uint)Math.Max(1, (int)MathF.Round(windowHeight * sizePolicy.ScaleY));
                break;
            case RenderResourceSizeClass.Custom:
                width = (uint)Math.Max(1, (int)MathF.Round(windowWidth * sizePolicy.ScaleX));
                height = (uint)Math.Max(1, (int)MathF.Round(windowHeight * sizePolicy.ScaleY));
                break;
            default:
                width = windowWidth;
                height = windowHeight;
                break;
        }

        return new Extent3D(width, height, 1);
    }

    private static Format ResolveFormat(string? formatLabel)
    {
        if (string.IsNullOrWhiteSpace(formatLabel))
            return Format.R8G8B8A8Unorm;

        if (Enum.TryParse(formatLabel, ignoreCase: true, out Format parsed))
            return parsed;

        return formatLabel.ToLowerInvariant() switch
        {
            "rgba16f" or "r16g16b16a16f" => Format.R16G16B16A16Sfloat,
            "rgba8" or "r8g8b8a8" => Format.R8G8B8A8Unorm,
            "rgb10a2" => Format.A2B10G10R10UnormPack32,
            "depth24stencil8" => Format.D24UnormS8Uint,
            "depth32" or "depth32f" => Format.D32Sfloat,
            _ => Format.R8G8B8A8Unorm,
        };
    }

    private static ImageUsageFlags InferUsage(VulkanImageAliasGroup group)
    {
        ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        foreach (var allocation in group.Allocations)
        {
            if (IsDepthTarget(allocation))
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            else
                usage |= ImageUsageFlags.ColorAttachmentBit;
        }
        return usage;
    }

    private static bool IsDepthTarget(VulkanImageAllocation allocation)
    {
        string? label = allocation.Descriptor.FormatLabel;
        if (!string.IsNullOrEmpty(label) && label.Contains("depth", StringComparison.OrdinalIgnoreCase))
            return true;
        return allocation.Name.Contains("depth", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class VulkanImageAliasGroup
{
    private readonly List<VulkanImageAllocation> _allocations = new();

    public VulkanImageAliasGroup(VulkanAliasGroupKey key)
    {
        Key = key;
        AllowsAliasing = true;
        CreateInfoTemplate = VulkanImageCreateTemplate.FromDescriptor(key.AliasKey.SizePolicy, key.AliasKey.FormatLabel, key.AliasKey.ArrayLayers);
    }

    public VulkanAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; private set; }
    public IReadOnlyList<VulkanImageAllocation> Allocations => _allocations;
    public VulkanImageCreateTemplate CreateInfoTemplate { get; }

    public VulkanImageAllocation Add(VulkanAllocationRequest request)
    {
        AllowsAliasing &= request.SupportsAliasing;
        VulkanImageAllocation allocation = new(request, Key, _allocations.Count);
        _allocations.Add(allocation);
        return allocation;
    }
}

internal readonly record struct VulkanImageAllocation(
    VulkanAllocationRequest Request,
    VulkanAliasGroupKey AliasGroup,
    int GroupIndex)
{
    public string Name => Request.Name;
    public TextureResourceDescriptor Descriptor => Request.Descriptor;
    public RenderResourceLifetime Lifetime => Request.Lifetime;
    public RenderResourceSizePolicy SizePolicy => Request.SizePolicy;
    public bool SupportsAliasing => Request.SupportsAliasing;
}

internal readonly record struct VulkanAliasGroupKey(
    VulkanAliasKey AliasKey,
    RenderResourceLifetime Lifetime);

internal readonly record struct VulkanImageCreateTemplate(
    RenderResourceSizePolicy SizePolicy,
    uint Layers,
    string? FormatLabel)
{
    public static VulkanImageCreateTemplate FromDescriptor(RenderResourceSizePolicy sizePolicy, string? formatLabel, uint layers)
        => new(sizePolicy, Math.Max(layers, 1u), formatLabel);
}

internal sealed class VulkanPhysicalImageGroup
{
    private readonly List<VulkanImageAllocation> _logicalResources = new();
    private Image _image;
    private DeviceMemory _memory;
    private bool _allocated;

    internal VulkanPhysicalImageGroup(
        VulkanImageAliasGroup logicalGroup,
        Extent3D extent,
        Format format,
        ImageUsageFlags usage)
    {
        Key = logicalGroup.Key;
        AllowsAliasing = logicalGroup.AllowsAliasing;
        Template = logicalGroup.CreateInfoTemplate;
        ResolvedExtent = extent;
        Format = format;
        Usage = usage;
    }

    public VulkanAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; }
    public VulkanImageCreateTemplate Template { get; }
    public Extent3D ResolvedExtent { get; }
    public Format Format { get; }
    public ImageUsageFlags Usage { get; }
    public IReadOnlyList<VulkanImageAllocation> LogicalResources => _logicalResources;
    public bool IsAllocated => _allocated;
    public Image Image => _image;
    public DeviceMemory Memory => _memory;

    internal void AddLogical(VulkanImageAllocation allocation)
        => _logicalResources.Add(allocation);

    public void EnsureAllocated(VulkanRenderer renderer)
    {
        if (_allocated)
            return;

        renderer.AllocatePhysicalImage(this, ref _image, ref _memory);
        _allocated = true;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalImage(ref _image, ref _memory);
        _allocated = false;
    }
}
