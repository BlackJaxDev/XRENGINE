using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks planned Vulkan image/buffer allocations for logical graph resources and aliases
/// transient-compatible resources when descriptors and usage are compatible.
/// </summary>
internal sealed class VulkanResourceAllocator
{
    private readonly Dictionary<string, VulkanImageAllocation> _logicalTextureAllocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> _aliasGroups = new();
    private readonly Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup> _physicalGroups = new();
    private readonly Dictionary<string, VulkanPhysicalImageGroup> _resourceToPhysicalGroup = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VulkanBufferAllocation> _logicalBufferAllocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VulkanBufferAliasGroupKey, VulkanBufferAliasGroup> _bufferAliasGroups = new();
    private readonly Dictionary<VulkanBufferAliasGroupKey, VulkanPhysicalBufferGroup> _physicalBufferGroups = new();
    private readonly Dictionary<string, VulkanPhysicalBufferGroup> _resourceToPhysicalBufferGroup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, VulkanImageAllocation> LogicalTextureAllocations => _logicalTextureAllocations;
    public IReadOnlyDictionary<string, VulkanBufferAllocation> LogicalBufferAllocations => _logicalBufferAllocations;
    public IReadOnlyDictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> AliasGroups => _aliasGroups;
    public IReadOnlyDictionary<VulkanBufferAliasGroupKey, VulkanBufferAliasGroup> BufferAliasGroups => _bufferAliasGroups;
    public IEnumerable<VulkanImageAliasGroup> EnumerateAliasGroups() => _aliasGroups.Values;
    public IEnumerable<VulkanPhysicalImageGroup> EnumeratePhysicalGroups() => _physicalGroups.Values;
    public IEnumerable<VulkanBufferAliasGroup> EnumerateBufferAliasGroups() => _bufferAliasGroups.Values;
    public IEnumerable<VulkanPhysicalBufferGroup> EnumeratePhysicalBufferGroups() => _physicalBufferGroups.Values;

    public IEnumerable<VulkanImageAllocation> EnumeratePersistentAllocations()
    {
        foreach (var pair in _logicalTextureAllocations)
            if (pair.Value.Lifetime == RenderResourceLifetime.Persistent)
                yield return pair.Value;
    }

    public IEnumerable<VulkanBufferAllocation> EnumeratePersistentBufferAllocations()
    {
        foreach (var pair in _logicalBufferAllocations)
            if (pair.Value.Lifetime == RenderResourceLifetime.Persistent)
                yield return pair.Value;
    }

    public void UpdatePlan(VulkanResourcePlan plan)
    {
        _logicalTextureAllocations.Clear();
        _aliasGroups.Clear();
        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();

        _logicalBufferAllocations.Clear();
        _bufferAliasGroups.Clear();
        _physicalBufferGroups.Clear();
        _resourceToPhysicalBufferGroup.Clear();

        foreach (VulkanAllocationRequest request in plan.AllTextures())
        {
            VulkanAliasGroupKey key = VulkanAliasGroupKey.FromRequest(request);
            if (!_aliasGroups.TryGetValue(key, out VulkanImageAliasGroup? group))
            {
                group = new VulkanImageAliasGroup(key);
                _aliasGroups.Add(key, group);
            }

            VulkanImageAllocation allocation = group.Add(request);
            _logicalTextureAllocations[request.Name] = allocation;
        }

        foreach (VulkanBufferAllocationRequest request in plan.AllBuffers())
        {
            VulkanBufferAliasGroupKey key = VulkanBufferAliasGroupKey.FromRequest(request);
            if (!_bufferAliasGroups.TryGetValue(key, out VulkanBufferAliasGroup? group))
            {
                group = new VulkanBufferAliasGroup(key);
                _bufferAliasGroups.Add(key, group);
            }

            VulkanBufferAllocation allocation = group.Add(request);
            _logicalBufferAllocations[request.Name] = allocation;
        }
    }

    public bool TryGetAllocation(string resourceName, out VulkanImageAllocation allocation)
        => _logicalTextureAllocations.TryGetValue(resourceName, out allocation);

    public bool TryGetBufferAllocation(string resourceName, out VulkanBufferAllocation allocation)
        => _logicalBufferAllocations.TryGetValue(resourceName, out allocation);

    public void RebuildPhysicalPlan(
        VulkanRenderer renderer,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner)
    {
        DestroyPhysicalImages(renderer);
        DestroyPhysicalBuffers(renderer);

        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();
        _physicalBufferGroups.Clear();
        _resourceToPhysicalBufferGroup.Clear();

        Dictionary<string, VulkanUsageProfile> usageProfiles = BuildUsageProfiles(passMetadata, planner);

        foreach (VulkanImageAliasGroup group in _aliasGroups.Values)
        {
            Extent3D extent = ResolveExtent(group.CreateInfoTemplate.SizePolicy);
            Format format = ResolveFormat(group.CreateInfoTemplate.FormatLabel);
            ImageUsageFlags usage = InferImageUsage(group, format, usageProfiles, planner);

            VulkanPhysicalImageGroup physicalGroup = new(group, extent, format, usage);
            foreach (VulkanImageAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalGroup[allocation.Name] = physicalGroup;
            }

            _physicalGroups[group.Key] = physicalGroup;
        }

        foreach (VulkanBufferAliasGroup group in _bufferAliasGroups.Values)
        {
            BufferUsageFlags usage = InferBufferUsage(group, usageProfiles);
            VulkanPhysicalBufferGroup physicalGroup = new(group, usage);

            foreach (VulkanBufferAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalBufferGroup[allocation.Name] = physicalGroup;
            }

            _physicalBufferGroups[group.Key] = physicalGroup;
        }
    }

    public bool TryGetPhysicalGroup(VulkanAliasGroupKey key, out VulkanPhysicalImageGroup? group)
        => _physicalGroups.TryGetValue(key, out group);

    public bool TryGetPhysicalBufferGroup(VulkanBufferAliasGroupKey key, out VulkanPhysicalBufferGroup? group)
        => _physicalBufferGroups.TryGetValue(key, out group);

    public bool TryGetPhysicalGroupForResource(string resourceName, out VulkanPhysicalImageGroup? group)
        => _resourceToPhysicalGroup.TryGetValue(resourceName, out group);

    public bool TryGetPhysicalBufferGroupForResource(string resourceName, out VulkanPhysicalBufferGroup? group)
        => _resourceToPhysicalBufferGroup.TryGetValue(resourceName, out group);

    public bool TryGetImage(string resourceName, out Image image)
    {
        if (TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) && group?.IsAllocated == true)
        {
            image = group?.Image ?? default;
            return image.Handle != 0;
        }

        image = default;
        return false;
    }

    public bool TryGetBuffer(string resourceName, out Buffer buffer, out ulong size)
    {
        if (TryGetPhysicalBufferGroupForResource(resourceName, out VulkanPhysicalBufferGroup? group) && group?.IsAllocated == true)
        {
            buffer = group?.Buffer ?? default;
            size = group?.SizeInBytes ?? 0;
            return buffer.Handle != 0;
        }

        buffer = default;
        size = 0;
        return false;
    }

    public bool TryEnsureImage(string resourceName, VulkanRenderer renderer, out Image image)
    {
        if (TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group))
        {
            group?.EnsureAllocated(renderer);
            image = group?.Image ?? default;
            return image.Handle != 0;
        }

        image = default;
        return false;
    }

    public bool TryEnsureBuffer(string resourceName, VulkanRenderer renderer, out Buffer buffer, out ulong size)
    {
        if (TryGetPhysicalBufferGroupForResource(resourceName, out VulkanPhysicalBufferGroup? group))
        {
            group?.EnsureAllocated(renderer);
            buffer = group?.Buffer ?? default;
            size = group?.SizeInBytes ?? 0;
            return buffer.Handle != 0;
        }

        buffer = default;
        size = 0;
        return false;
    }

    public void AllocatePhysicalImages(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            group.EnsureAllocated(renderer);
    }

    public void AllocatePhysicalBuffers(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.EnsureAllocated(renderer);
    }

    public void DestroyPhysicalImages(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            group.Destroy(renderer);
    }

    public void DestroyPhysicalBuffers(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.Destroy(renderer);
    }

    private static Dictionary<string, VulkanUsageProfile> BuildUsageProfiles(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner)
    {
        Dictionary<string, VulkanUsageProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
        if (passMetadata is null || passMetadata.Count == 0)
            return profiles;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                foreach (string resource in ExpandLogicalResources(usage, planner))
                {
                    if (!profiles.TryGetValue(resource, out VulkanUsageProfile? profile))
                    {
                        profile = new VulkanUsageProfile();
                        profiles[resource] = profile;
                    }

                    profile.Add(usage.ResourceType);
                }
            }
        }

        return profiles;
    }

    private static IEnumerable<string> ExpandLogicalResources(RenderPassResourceUsage usage, VulkanResourcePlanner planner)
    {
        string resourceBinding = usage.ResourceName;
        if (string.IsNullOrWhiteSpace(resourceBinding))
            yield break;

        bool imageType = IsImageResourceType(usage.ResourceType);
        bool bufferType = IsBufferResourceType(usage.ResourceType);

        if (resourceBinding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            yield break;

        if (imageType && resourceBinding.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = resourceBinding.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                yield break;

            string fboName = segments[1];
            string slot = segments.Length >= 3 ? segments[2] : "color";
            if (!planner.TryGetFrameBufferDescriptor(fboName, out FrameBufferResourceDescriptor? descriptor))
                yield break;

            foreach (FrameBufferAttachmentDescriptor attachment in descriptor?.Attachments ?? [])
            {
                if (MatchesSlot(attachment.Attachment, slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
                    yield return attachment.ResourceName;
            }

            yield break;
        }

        if (imageType && resourceBinding.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
        {
            string textureName = resourceBinding["tex::".Length..];
            if (!string.IsNullOrWhiteSpace(textureName))
                yield return textureName;
            yield break;
        }

        if (bufferType && resourceBinding.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
        {
            string bufferName = resourceBinding["buf::".Length..];
            if (!string.IsNullOrWhiteSpace(bufferName))
                yield return bufferName;
            yield break;
        }

        yield return resourceBinding;
    }

    private static bool IsImageResourceType(RenderPassResourceType type)
        => type is RenderPassResourceType.ColorAttachment
            or RenderPassResourceType.DepthAttachment
            or RenderPassResourceType.StencilAttachment
            or RenderPassResourceType.ResolveAttachment
            or RenderPassResourceType.SampledTexture
            or RenderPassResourceType.StorageTexture
            or RenderPassResourceType.TransferSource
            or RenderPassResourceType.TransferDestination;

    private static bool IsBufferResourceType(RenderPassResourceType type)
        => type is RenderPassResourceType.UniformBuffer
            or RenderPassResourceType.StorageBuffer
            or RenderPassResourceType.VertexBuffer
            or RenderPassResourceType.IndexBuffer
            or RenderPassResourceType.IndirectBuffer
            or RenderPassResourceType.TransferSource
            or RenderPassResourceType.TransferDestination;

    private static bool MatchesSlot(EFrameBufferAttachment attachment, string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return false;

        if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Length > 5 && int.TryParse(slot.AsSpan(5), out int colorIndex))
            {
                EFrameBufferAttachment expected = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
                return attachment == expected;
            }

            return attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;
        }

        if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        return false;
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

    private static ImageUsageFlags InferImageUsage(
        VulkanImageAliasGroup group,
        Format resolvedFormat,
        IReadOnlyDictionary<string, VulkanUsageProfile> usageProfiles,
        VulkanResourcePlanner planner)
    {
        ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        bool matchedProfile = false;
        bool inferredFromDescriptor = false;

        foreach (VulkanImageAllocation allocation in group.Allocations)
        {
            if (!usageProfiles.TryGetValue(allocation.Name, out VulkanUsageProfile? profile))
                continue;

            matchedProfile = true;
            if (profile.Has(RenderPassResourceType.SampledTexture))
                usage |= ImageUsageFlags.SampledBit;
            if (profile.Has(RenderPassResourceType.StorageTexture))
                usage |= ImageUsageFlags.StorageBit;
            if (profile.Has(RenderPassResourceType.ColorAttachment) || profile.Has(RenderPassResourceType.ResolveAttachment))
                usage |= ImageUsageFlags.ColorAttachmentBit;
            if (profile.Has(RenderPassResourceType.DepthAttachment) || profile.Has(RenderPassResourceType.StencilAttachment))
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            if (profile.Has(RenderPassResourceType.TransferSource))
                usage |= ImageUsageFlags.TransferSrcBit;
            if (profile.Has(RenderPassResourceType.TransferDestination))
                usage |= ImageUsageFlags.TransferDstBit;
        }

        // Always infer attachment usage from FBO descriptors as an additive source.
        // A resource can be both profiled as sampled/storage and used as an FBO attachment;
        // in that case we still must advertise attachment usage bits on VkImage creation.
        foreach (VulkanImageAllocation allocation in group.Allocations)
        {
            foreach (FrameBufferResourceDescriptor fboDescriptor in planner.FrameBufferDescriptors.Values)
            {
                foreach (FrameBufferAttachmentDescriptor att in fboDescriptor.Attachments)
                {
                    if (!string.Equals(att.ResourceName, allocation.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    inferredFromDescriptor = true;
                    if (att.Attachment is EFrameBufferAttachment.DepthAttachment
                        or EFrameBufferAttachment.DepthStencilAttachment
                        or EFrameBufferAttachment.StencilAttachment)
                    {
                        usage |= ImageUsageFlags.DepthStencilAttachmentBit;
                    }
                    else
                    {
                        usage |= ImageUsageFlags.ColorAttachmentBit;
                    }
                }
            }
        }

        // Include storage usage when any allocation's texture descriptor requires it,
        // regardless of whether the render-pass usage profile declared StorageTexture.
        // This ensures the physical VkImage is created with VK_IMAGE_USAGE_STORAGE_BIT
        // so that compute shaders can bind the image view as a storage image.
        foreach (VulkanImageAllocation allocation in group.Allocations)
        {
            if (allocation.Descriptor.RequiresStorageUsage)
            {
                usage |= ImageUsageFlags.StorageBit;
                break;
            }
        }

        if ((usage & ImageUsageFlags.DepthStencilAttachmentBit) != 0)
            usage |= ImageUsageFlags.SampledBit;

        if (!matchedProfile)
        {
            if (!inferredFromDescriptor)
            {
                // Final fallback: use format analysis when no descriptor data is available.
                usage |= IsDepthStencilFormat(resolvedFormat)
                    ? ImageUsageFlags.DepthStencilAttachmentBit
                    : ImageUsageFlags.ColorAttachmentBit;
            }

            usage |= ImageUsageFlags.SampledBit;
        }

        return usage;
    }

    private static BufferUsageFlags InferBufferUsage(
        VulkanBufferAliasGroup group,
        IReadOnlyDictionary<string, VulkanUsageProfile> usageProfiles)
    {
        BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
        bool matchedProfile = false;

        foreach (VulkanBufferAllocation allocation in group.Allocations)
        {
            usage |= ToVkUsageFlags(allocation.Target);
            usage |= ToVkUsageFlags(allocation.Usage);

            if (!usageProfiles.TryGetValue(allocation.Name, out VulkanUsageProfile? profile))
                continue;

            matchedProfile = true;
            if (profile.Has(RenderPassResourceType.UniformBuffer))
                usage |= BufferUsageFlags.UniformBufferBit;
            if (profile.Has(RenderPassResourceType.StorageBuffer))
                usage |= BufferUsageFlags.StorageBufferBit;
            if (profile.Has(RenderPassResourceType.VertexBuffer))
                usage |= BufferUsageFlags.VertexBufferBit;
            if (profile.Has(RenderPassResourceType.IndexBuffer))
                usage |= BufferUsageFlags.IndexBufferBit;
            if (profile.Has(RenderPassResourceType.IndirectBuffer))
                usage |= BufferUsageFlags.IndirectBufferBit;
            if (profile.Has(RenderPassResourceType.TransferSource))
                usage |= BufferUsageFlags.TransferSrcBit;
            if (profile.Has(RenderPassResourceType.TransferDestination))
                usage |= BufferUsageFlags.TransferDstBit;
        }

        if (!matchedProfile && usage == (BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit))
            usage |= BufferUsageFlags.StorageBufferBit;

        return usage;
    }

    private static BufferUsageFlags ToVkUsageFlags(EBufferTarget target)
        => target switch
        {
            EBufferTarget.ArrayBuffer => BufferUsageFlags.VertexBufferBit,
            EBufferTarget.ElementArrayBuffer => BufferUsageFlags.IndexBufferBit,
            EBufferTarget.PixelPackBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.PixelUnpackBuffer => BufferUsageFlags.TransferSrcBit,
            EBufferTarget.UniformBuffer => BufferUsageFlags.UniformBufferBit,
            EBufferTarget.TextureBuffer => BufferUsageFlags.UniformTexelBufferBit | BufferUsageFlags.StorageTexelBufferBit,
            EBufferTarget.TransformFeedbackBuffer => BufferUsageFlags.StorageBufferBit,
            EBufferTarget.CopyReadBuffer => BufferUsageFlags.TransferSrcBit,
            EBufferTarget.CopyWriteBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit,
            EBufferTarget.ShaderStorageBuffer => BufferUsageFlags.StorageBufferBit,
            EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit,
            EBufferTarget.QueryBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.AtomicCounterBuffer => BufferUsageFlags.StorageBufferBit,
            EBufferTarget.ParameterBuffer => BufferUsageFlags.UniformBufferBit,
            _ => BufferUsageFlags.StorageBufferBit
        };

    private static BufferUsageFlags ToVkUsageFlags(EBufferUsage usage)
        => usage switch
        {
            EBufferUsage.StaticDraw => BufferUsageFlags.TransferDstBit,
            EBufferUsage.StreamDraw or EBufferUsage.DynamicDraw => BufferUsageFlags.TransferDstBit,
            EBufferUsage.StreamRead or EBufferUsage.DynamicRead => BufferUsageFlags.TransferSrcBit,
            EBufferUsage.StreamCopy or EBufferUsage.DynamicCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            EBufferUsage.StaticRead => BufferUsageFlags.TransferSrcBit,
            EBufferUsage.StaticCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            _ => 0
        };

    internal static bool IsDepthStencilFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.X8D24UnormPack32
            or Format.D16UnormS8Uint;

    private sealed class VulkanUsageProfile
    {
        private readonly HashSet<RenderPassResourceType> _types = [];

        public void Add(RenderPassResourceType type)
            => _types.Add(type);

        public bool Has(RenderPassResourceType type)
            => _types.Contains(type);
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
        AllowsAliasing &= request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        VulkanImageAllocation allocation = new(request, Key, _allocations.Count);
        _allocations.Add(allocation);
        return allocation;
    }
}

internal sealed class VulkanBufferAliasGroup
{
    private readonly List<VulkanBufferAllocation> _allocations = new();

    public VulkanBufferAliasGroup(VulkanBufferAliasGroupKey key)
    {
        Key = key;
        AllowsAliasing = true;
        CreateInfoTemplate = VulkanBufferCreateTemplate.FromDescriptor(key.AliasKey.SizeInBytes, key.AliasKey.Target, key.AliasKey.Usage);
    }

    public VulkanBufferAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; private set; }
    public IReadOnlyList<VulkanBufferAllocation> Allocations => _allocations;
    public VulkanBufferCreateTemplate CreateInfoTemplate { get; }

    public VulkanBufferAllocation Add(VulkanBufferAllocationRequest request)
    {
        AllowsAliasing &= request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        VulkanBufferAllocation allocation = new(request, Key, _allocations.Count);
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

internal readonly record struct VulkanBufferAllocation(
    VulkanBufferAllocationRequest Request,
    VulkanBufferAliasGroupKey AliasGroup,
    int GroupIndex)
{
    public string Name => Request.Name;
    public BufferResourceDescriptor Descriptor => Request.Descriptor;
    public RenderResourceLifetime Lifetime => Request.Lifetime;
    public ulong SizeInBytes => Request.SizeInBytes;
    public EBufferTarget Target => Request.Target;
    public EBufferUsage Usage => Request.Usage;
    public bool SupportsAliasing => Request.SupportsAliasing;
}

internal readonly record struct VulkanAliasGroupKey(
    VulkanAliasKey AliasKey,
    RenderResourceLifetime Lifetime,
    string GroupDiscriminator)
{
    public static VulkanAliasGroupKey FromRequest(VulkanAllocationRequest request)
    {
        bool aliasable = request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        string discriminator = aliasable ? "TransientAlias" : request.Name;
        return new VulkanAliasGroupKey(request.AliasKey, request.Lifetime, discriminator);
    }
}

internal readonly record struct VulkanBufferAliasGroupKey(
    VulkanBufferAliasKey AliasKey,
    RenderResourceLifetime Lifetime,
    string GroupDiscriminator)
{
    public static VulkanBufferAliasGroupKey FromRequest(VulkanBufferAllocationRequest request)
    {
        bool aliasable = request.SupportsAliasing && request.Lifetime == RenderResourceLifetime.Transient;
        string discriminator = aliasable ? "TransientAlias" : request.Name;
        return new VulkanBufferAliasGroupKey(request.AliasKey, request.Lifetime, discriminator);
    }
}

internal readonly record struct VulkanImageCreateTemplate(
    RenderResourceSizePolicy SizePolicy,
    uint Layers,
    string? FormatLabel)
{
    public static VulkanImageCreateTemplate FromDescriptor(RenderResourceSizePolicy sizePolicy, string? formatLabel, uint layers)
        => new(sizePolicy, Math.Max(layers, 1u), formatLabel);
}

internal readonly record struct VulkanBufferCreateTemplate(
    ulong SizeInBytes,
    EBufferTarget Target,
    EBufferUsage Usage)
{
    public static VulkanBufferCreateTemplate FromDescriptor(ulong sizeInBytes, EBufferTarget target, EBufferUsage usage)
        => new(Math.Max(sizeInBytes, 1UL), target, usage);
}

internal sealed class VulkanPhysicalImageGroup
{
    private readonly List<VulkanImageAllocation> _logicalResources = new();
    private Image _image;
    private DeviceMemory _memory;
    private bool _allocated;
    private ImageLayout _lastKnownLayout = ImageLayout.Undefined;

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

    /// The last layout this image was transitioned to via a pipeline barrier or
    /// render pass. Used to provide the correct <c>oldLayout</c> in blit and
    /// transfer barriers so that the validation layer does not flag a mismatch
    /// with the actual GPU-side layout.
    /// </summary>
    public ImageLayout LastKnownLayout
    {
        get => _lastKnownLayout;
        internal set => _lastKnownLayout = value;
    }

    internal void AddLogical(VulkanImageAllocation allocation)
        => _logicalResources.Add(allocation);

    public void EnsureAllocated(VulkanRenderer renderer)
    {
        if (_allocated)
            return;

        renderer.AllocatePhysicalImage(this, ref _image, ref _memory);
        _allocated = true;
        _lastKnownLayout = ImageLayout.Undefined;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalImage(ref _image, ref _memory);
        _allocated = false;
        _lastKnownLayout = ImageLayout.Undefined;
    }
}

internal sealed class VulkanPhysicalBufferGroup
{
    private readonly List<VulkanBufferAllocation> _logicalResources = new();
    private Buffer _buffer;
    private DeviceMemory _memory;
    private bool _allocated;

    internal VulkanPhysicalBufferGroup(
        VulkanBufferAliasGroup logicalGroup,
        BufferUsageFlags usage)
    {
        Key = logicalGroup.Key;
        AllowsAliasing = logicalGroup.AllowsAliasing;
        Template = logicalGroup.CreateInfoTemplate;
        Usage = usage;
    }

    public VulkanBufferAliasGroupKey Key { get; }
    public bool AllowsAliasing { get; }
    public VulkanBufferCreateTemplate Template { get; }
    public BufferUsageFlags Usage { get; }
    public ulong SizeInBytes => Template.SizeInBytes;
    public IReadOnlyList<VulkanBufferAllocation> LogicalResources => _logicalResources;
    public bool IsAllocated => _allocated;
    public Buffer Buffer => _buffer;
    public DeviceMemory Memory => _memory;

    internal void AddLogical(VulkanBufferAllocation allocation)
        => _logicalResources.Add(allocation);

    public void EnsureAllocated(VulkanRenderer renderer)
    {
        if (_allocated)
            return;

        renderer.AllocatePhysicalBuffer(this, ref _buffer, ref _memory);
        _allocated = true;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalBuffer(ref _buffer, ref _memory);
        _allocated = false;
    }
}
