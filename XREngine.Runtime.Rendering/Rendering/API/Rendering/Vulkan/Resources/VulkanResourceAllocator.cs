using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
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
        VulkanResourcePlanner planner,
        VulkanResourceExtentContext extentContext)
    {
        DestroyPhysicalImages(renderer);
        DestroyPhysicalBuffers(renderer);

        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();
        _physicalBufferGroups.Clear();
        _resourceToPhysicalBufferGroup.Clear();

        foreach (VulkanImageAliasGroup group in _aliasGroups.Values)
        {
            Extent3D extent = ResolveExtent(group.CreateInfoTemplate.SizePolicy, extentContext);
            Format format = ResolveFormat(group.CreateInfoTemplate);
            ImageUsageFlags usage = InferImageUsage(group, format, planner);
            uint mipLevels = ResolveMipLevelCount(group, extent, usage, planner);
            SampleCountFlags samples = ResolveSampleCount(group.CreateInfoTemplate.Samples);
            VulkanTransientAttachmentPolicy transientAttachmentPolicy = ResolveTransientAttachmentPolicy(group);
            MemoryPropertyFlags memoryProperties = ResolveImageMemoryProperties(transientAttachmentPolicy);
            if (transientAttachmentPolicy == VulkanTransientAttachmentPolicy.PreferLazilyAllocated)
            {
                usage |= ImageUsageFlags.TransientAttachmentBit;
                usage &= ~(ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit);
            }

            VulkanPhysicalImageGroup physicalGroup = new(group, extent, format, usage, mipLevels, samples, memoryProperties, transientAttachmentPolicy);
            foreach (VulkanImageAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalGroup[allocation.Name] = physicalGroup;
            }

            _physicalGroups[group.Key] = physicalGroup;
        }

        foreach ((string viewName, TextureResourceDescriptor descriptor) in planner.TextureViewDescriptors)
        {
            string sourceName = string.IsNullOrWhiteSpace(descriptor.SourceTextureName)
                ? planner.ResolveImageResourceName(viewName)
                : planner.ResolveImageResourceName(descriptor.SourceTextureName!);

            if (_resourceToPhysicalGroup.TryGetValue(sourceName, out VulkanPhysicalImageGroup? sourceGroup))
                _resourceToPhysicalGroup[viewName] = sourceGroup;
        }

        foreach (VulkanBufferAliasGroup group in _bufferAliasGroups.Values)
        {
            BufferUsageFlags usage = InferBufferUsage(group, renderer.SupportsTransformFeedback);
            VulkanPhysicalBufferGroup physicalGroup = new(group, usage);

            foreach (VulkanBufferAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalBufferGroup[allocation.Name] = physicalGroup;
            }

            _physicalBufferGroups[group.Key] = physicalGroup;
        }

        LogDeferredLightingPhysicalPlan(passMetadata, planner);
    }

    public void RebuildPhysicalPlan(
        VulkanRenderer renderer,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner)
        => RebuildPhysicalPlan(renderer, passMetadata, planner, new VulkanResourceExtentContext(1u, 1u, 1u, 1u));

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
            if (group is null || !group.TryEnsureAllocated(renderer, out _))
            {
                image = default;
                return false;
            }

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

    public bool TryAllocatePhysicalImages(VulkanRenderer renderer, out string failureReason)
    {
        failureReason = string.Empty;

        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (group.TryEnsureAllocated(renderer, out failureReason))
                continue;

            return false;
        }

        return true;
    }

    public void AllocatePhysicalBuffers(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.EnsureAllocated(renderer);
    }

    public void DestroyPhysicalImages(VulkanRenderer renderer, VulkanPhysicalImageGroup? exceptGroup = null)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (ReferenceEquals(group, exceptGroup))
                continue;

            group.Destroy(renderer);
        }
    }

    public void DestroyPhysicalBuffers(VulkanRenderer renderer)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.Destroy(renderer);
    }

    internal static int ComputePhysicalPlanUsageSignature(
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        // Pass metadata can legitimately flap as optional passes enter and leave the active graph.
        // Physical allocations are descriptor-driven so those metadata changes rebuild barriers
        // without destroying persistent render targets.
        _ = passMetadata;

        HashCode hash = new();

        foreach (KeyValuePair<string, FrameBufferResourceDescriptor> pair in planner.FrameBufferDescriptors.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)pair.Value.Lifetime);
            hash.Add((int)pair.Value.SizePolicy.SizeClass);
            hash.Add(pair.Value.SizePolicy.ScaleX);
            hash.Add(pair.Value.SizePolicy.ScaleY);
            hash.Add(pair.Value.SizePolicy.Width);
            hash.Add(pair.Value.SizePolicy.Height);
            foreach (FrameBufferAttachmentDescriptor attachment in pair.Value.Attachments)
            {
                hash.Add(planner.ResolveImageResourceName(attachment.ResourceName), StringComparer.OrdinalIgnoreCase);
                hash.Add((int)attachment.Attachment);
                hash.Add(attachment.MipLevel);
                hash.Add(attachment.LayerIndex);
            }
        }

        return hash.ToHashCode();
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
                    yield return planner.ResolveImageResourceName(attachment.ResourceName);
            }

            yield break;
        }

        if (imageType && resourceBinding.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
        {
            string textureName = resourceBinding["tex::".Length..];
            if (!string.IsNullOrWhiteSpace(textureName))
                yield return planner.ResolveImageResourceName(textureName);
            yield break;
        }

        if (bufferType && resourceBinding.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
        {
            string bufferName = resourceBinding["buf::".Length..];
            if (!string.IsNullOrWhiteSpace(bufferName))
                yield return bufferName;
            yield break;
        }

        yield return imageType ? planner.ResolveImageResourceName(resourceBinding) : resourceBinding;
    }

    private static bool IsImageResourceType(ERenderPassResourceType type)
        => type is ERenderPassResourceType.ColorAttachment
            or ERenderPassResourceType.DepthAttachment
            or ERenderPassResourceType.StencilAttachment
            or ERenderPassResourceType.ResolveAttachment
            or ERenderPassResourceType.SampledTexture
            or ERenderPassResourceType.StorageTexture
            or ERenderPassResourceType.TransferSource
            or ERenderPassResourceType.TransferDestination;

    private static bool IsBufferResourceType(ERenderPassResourceType type)
        => type is ERenderPassResourceType.UniformBuffer
            or ERenderPassResourceType.StorageBuffer
            or ERenderPassResourceType.VertexBuffer
            or ERenderPassResourceType.IndexBuffer
            or ERenderPassResourceType.IndirectBuffer
            or ERenderPassResourceType.TransferSource
            or ERenderPassResourceType.TransferDestination;

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

    private static Extent3D ResolveExtent(
        RenderResourceSizePolicy sizePolicy,
        VulkanResourceExtentContext extentContext)
    {
        uint width;
        uint height;

        uint windowWidth = Math.Max(extentContext.WindowWidth, 1u);
        uint windowHeight = Math.Max(extentContext.WindowHeight, 1u);
        uint internalWidth = Math.Max(extentContext.InternalWidth, 1u);
        uint internalHeight = Math.Max(extentContext.InternalHeight, 1u);

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

    private static Format ResolveFormat(VulkanImageCreateTemplate template)
    {
        if (template.SizedInternalFormat is ESizedInternalFormat sizedFormat)
            return VulkanRenderer.VkFormatConversions.FromSizedFormat(sizedFormat);

        if (template.InternalFormat is EPixelInternalFormat internalFormat)
            return VulkanRenderer.VkFormatConversions.FromPixelInternalFormat(internalFormat);

        string? formatLabel = template.FormatLabel;
        if (string.IsNullOrWhiteSpace(formatLabel))
            throw new InvalidOperationException("Vulkan image descriptor is missing a format.");

        if (Enum.TryParse(formatLabel, ignoreCase: true, out ESizedInternalFormat sizedFromLabel))
            return VulkanRenderer.VkFormatConversions.FromSizedFormat(sizedFromLabel);

        if (Enum.TryParse(formatLabel, ignoreCase: true, out Format parsed))
            return parsed;

        return formatLabel.ToLowerInvariant() switch
        {
            "rgba16f" or "r16g16b16a16f" => Format.R16G16B16A16Sfloat,
            "rgba8" or "r8g8b8a8" => Format.R8G8B8A8Unorm,
            "rgb10a2" => Format.A2B10G10R10UnormPack32,
            "depth24stencil8" => Format.D24UnormS8Uint,
            "depth32" or "depth32f" => Format.D32Sfloat,
            _ => throw new InvalidOperationException($"Unsupported Vulkan image format label '{formatLabel}'.")
        };
    }

    private static uint ResolveMipLevelCount(
        VulkanImageAliasGroup group,
        Extent3D extent,
        ImageUsageFlags usage,
        VulkanResourcePlanner planner)
    {
        VulkanImageCreateTemplate template = group.CreateInfoTemplate;
        uint requested = Math.Max(1u, template.MipPolicy.MipLevelCount);
        foreach (VulkanImageAllocation allocation in group.Allocations)
            requested = Math.Max(requested, ResolveRequiredMipLevelsFromFrameBuffers(allocation.Name, planner));

        if (template.Samples > 1u)
            return 1u;

        uint maxLevels = 1u + (uint)BitOperations.Log2(Math.Max(Math.Max(extent.Width, extent.Height), extent.Depth));
        uint clamped = Math.Clamp(requested, 1u, Math.Max(1u, maxLevels));

        if (template.MipPolicy.AutoGenerateMipmaps
            && (usage & ImageUsageFlags.TransferDstBit) == 0)
        {
            return 1u;
        }

        return clamped;
    }

    private static uint ResolveRequiredMipLevelsFromFrameBuffers(string resourceName, VulkanResourcePlanner planner)
    {
        uint required = 1u;
        foreach (FrameBufferResourceDescriptor descriptor in planner.FrameBufferDescriptors.Values)
        {
            foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
            {
                string attachmentResourceName = planner.ResolveImageResourceName(attachment.ResourceName);
                if (!string.Equals(attachmentResourceName, resourceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (attachment.MipLevel >= 0)
                    required = Math.Max(required, (uint)attachment.MipLevel + 1u);
            }
        }

        return required;
    }

    private static SampleCountFlags ResolveSampleCount(uint samples)
        => samples switch
        {
            <= 1u => SampleCountFlags.Count1Bit,
            2u => SampleCountFlags.Count2Bit,
            3u or 4u => SampleCountFlags.Count4Bit,
            <= 8u => SampleCountFlags.Count8Bit,
            <= 16u => SampleCountFlags.Count16Bit,
            <= 32u => SampleCountFlags.Count32Bit,
            _ => SampleCountFlags.Count64Bit
        };

    private static ImageUsageFlags InferImageUsage(
        VulkanImageAliasGroup group,
        Format resolvedFormat,
        VulkanResourcePlanner planner)
    {
        ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        bool inferredFromDescriptor = false;

        foreach (VulkanImageAllocation allocation in group.Allocations)
        {
            if (allocation.Descriptor.Usage != RenderPipelineResourceUsage.None)
            {
                inferredFromDescriptor = true;
                usage |= ToVkUsageFlags(allocation.Descriptor.Usage);
            }
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
                    string attachmentResourceName = planner.ResolveImageResourceName(att.ResourceName);
                    if (!string.Equals(attachmentResourceName, allocation.Name, StringComparison.OrdinalIgnoreCase))
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

        if (!inferredFromDescriptor)
        {
            // Final fallback: use format analysis when no descriptor data is available.
            usage |= IsDepthStencilFormat(resolvedFormat)
                ? ImageUsageFlags.DepthStencilAttachmentBit
                : ImageUsageFlags.ColorAttachmentBit;
        }

        usage |= ImageUsageFlags.SampledBit;

        if (IsDepthStencilFormat(resolvedFormat))
        {
            usage &= ~ImageUsageFlags.ColorAttachmentBit;
            usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        return usage;
    }

    private static ImageUsageFlags ToVkUsageFlags(RenderPipelineResourceUsage usage)
    {
        ImageUsageFlags flags = 0;

        if ((usage & RenderPipelineResourceUsage.SampledTexture) != 0)
            flags |= ImageUsageFlags.SampledBit;
        if ((usage & RenderPipelineResourceUsage.ColorAttachment) != 0)
            flags |= ImageUsageFlags.ColorAttachmentBit;
        if ((usage & RenderPipelineResourceUsage.DepthStencilAttachment) != 0)
            flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        if ((usage & RenderPipelineResourceUsage.StorageImage) != 0)
            flags |= ImageUsageFlags.StorageBit;
        if ((usage & RenderPipelineResourceUsage.TransferSource) != 0)
            flags |= ImageUsageFlags.TransferSrcBit;
        if ((usage & RenderPipelineResourceUsage.TransferDestination) != 0)
            flags |= ImageUsageFlags.TransferDstBit;
        if ((usage & RenderPipelineResourceUsage.PresentSource) != 0)
            flags |= ImageUsageFlags.TransferSrcBit;

        return flags;
    }

    private static VulkanTransientAttachmentPolicy ResolveTransientAttachmentPolicy(VulkanImageAliasGroup group)
    {
        bool hasLazyCandidate = false;
        foreach (VulkanImageAllocation allocation in group.Allocations)
        {
            if (allocation.Request.TransientAttachmentPolicy != VulkanTransientAttachmentPolicy.PreferLazilyAllocated)
                return VulkanTransientAttachmentPolicy.None;

            hasLazyCandidate = true;
        }

        return hasLazyCandidate
            ? VulkanTransientAttachmentPolicy.PreferLazilyAllocated
            : VulkanTransientAttachmentPolicy.None;
    }

    private static MemoryPropertyFlags ResolveImageMemoryProperties(VulkanTransientAttachmentPolicy transientAttachmentPolicy)
        => transientAttachmentPolicy == VulkanTransientAttachmentPolicy.PreferLazilyAllocated
            ? MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.LazilyAllocatedBit
            : MemoryPropertyFlags.DeviceLocalBit;

    private static BufferUsageFlags InferBufferUsage(
        VulkanBufferAliasGroup group,
        bool supportsTransformFeedback)
    {
        BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;

        foreach (VulkanBufferAllocation allocation in group.Allocations)
        {
            usage |= ToVkUsageFlags(allocation.Target, supportsTransformFeedback);
            usage |= ToVkUsageFlags(allocation.Usage);
        }

        usage |= BufferUsageFlags.UniformBufferBit |
                 BufferUsageFlags.StorageBufferBit |
                 BufferUsageFlags.VertexBufferBit |
                 BufferUsageFlags.IndexBufferBit |
                 BufferUsageFlags.IndirectBufferBit;

        return usage;
    }

    private static BufferUsageFlags ToVkUsageFlags(EBufferTarget target, bool supportsTransformFeedback)
        => target switch
        {
            EBufferTarget.ArrayBuffer => BufferUsageFlags.VertexBufferBit,
            EBufferTarget.ElementArrayBuffer => BufferUsageFlags.IndexBufferBit,
            EBufferTarget.PixelPackBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.PixelUnpackBuffer => BufferUsageFlags.TransferSrcBit,
            EBufferTarget.UniformBuffer => BufferUsageFlags.UniformBufferBit,
            EBufferTarget.TextureBuffer => BufferUsageFlags.UniformTexelBufferBit | BufferUsageFlags.StorageTexelBufferBit,
            EBufferTarget.TransformFeedbackBuffer when supportsTransformFeedback =>
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransformFeedbackBufferBitExt |
                BufferUsageFlags.TransformFeedbackCounterBufferBitExt,
            EBufferTarget.TransformFeedbackBuffer => BufferUsageFlags.StorageBufferBit,
            EBufferTarget.CopyReadBuffer => BufferUsageFlags.TransferSrcBit,
            EBufferTarget.CopyWriteBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            EBufferTarget.ShaderStorageBuffer => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            EBufferTarget.QueryBuffer => BufferUsageFlags.TransferDstBit,
            EBufferTarget.AtomicCounterBuffer => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
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

    private void LogDeferredLightingPhysicalPlan(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner)
    {
        if (!DeferredLightingDiagnostics.Enabled)
            return;

        TryGetPhysicalGroupForResource(DefaultRenderPipeline.LightingAccumTextureName, out VulkanPhysicalImageGroup? accumGroup);
        TryGetPhysicalGroupForResource(DefaultRenderPipeline.DiffuseTextureName, out VulkanPhysicalImageGroup? finalGroup);
        bool sameLightingImageGroup = accumGroup is not null && ReferenceEquals(accumGroup, finalGroup);

        DeferredLightingDiagnostics.Write(
            "[VulkanResourceAllocator] Physical plan summary " +
            $"lightingAccumGroup={DescribePhysicalGroupShort(accumGroup)} " +
            $"lightingTextureGroup={DescribePhysicalGroupShort(finalGroup)} " +
            $"samePhysicalGroup={sameLightingImageGroup}");

        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (!ContainsWatchedDeferredLightingResource(group))
                continue;

            DeferredLightingDiagnostics.Write(
                "[VulkanResourceAllocator] Watched image group " +
                $"key={group.Key} allowsAliasing={group.AllowsAliasing} allocated={group.IsAllocated} " +
                $"image=0x{group.Image.Handle:X} extent={group.ResolvedExtent.Width}x{group.ResolvedExtent.Height}x{group.ResolvedExtent.Depth} " +
                $"format={group.Format} usage={group.Usage} mips={group.MipLevels} samples={group.Samples} lastLayout={group.LastKnownLayout} " +
                $"logical=[{DescribeLogicalImageAllocations(group.LogicalResources)}]");
        }

        if (passMetadata is null)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                foreach (string resource in ExpandLogicalResources(usage, planner))
                {
                    if (!DeferredLightingDiagnostics.IsWatchedTextureName(resource))
                        continue;

                    DeferredLightingDiagnostics.Write(
                        "[VulkanResourceAllocator] Watched render-pass usage " +
                        $"pass={pass.PassIndex} name='{pass.Name}' stage={pass.Stage} resource='{resource}' " +
                        $"declared='{usage.ResourceName}' type={usage.ResourceType} access={usage.Access} load={usage.LoadOp} store={usage.StoreOp}");
                }
            }
        }
    }

    private static bool ContainsWatchedDeferredLightingResource(VulkanPhysicalImageGroup group)
    {
        foreach (VulkanImageAllocation allocation in group.LogicalResources)
        {
            if (DeferredLightingDiagnostics.IsWatchedTextureName(allocation.Name))
                return true;
        }

        return false;
    }

    private static string DescribePhysicalGroupShort(VulkanPhysicalImageGroup? group)
    {
        if (group is null)
            return "<null>";

        return $"key={group.Key}; image=0x{group.Image.Handle:X}; logical=[{DescribeLogicalImageAllocations(group.LogicalResources)}]";
    }

    private static string DescribeLogicalImageAllocations(IReadOnlyList<VulkanImageAllocation> allocations)
    {
        if (allocations.Count == 0)
            return "<none>";

        StringBuilder builder = new();
        for (int i = 0; i < allocations.Count; i++)
        {
            if (i > 0)
                builder.Append("; ");

            VulkanImageAllocation allocation = allocations[i];
            builder
                .Append(allocation.Name)
                .Append("#").Append(allocation.GroupIndex)
                .Append(" lifetime=").Append(allocation.Lifetime)
                .Append(" alias=").Append(allocation.SupportsAliasing)
                .Append(" format=").Append(allocation.Descriptor.FormatLabel ?? "<null>")
                .Append(" usage=").Append(allocation.Descriptor.Usage)
                .Append(" samples=").Append(allocation.Descriptor.Samples)
                .Append(" mips=").Append(Math.Max(1u, allocation.Descriptor.MipPolicy.MipLevelCount))
                .Append(" size=").Append(allocation.SizePolicy);
        }

        return builder.ToString();
    }

    internal static bool IsDepthStencilFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.X8D24UnormPack32
            or Format.D16UnormS8Uint;

}

internal sealed class VulkanImageAliasGroup
{
    private readonly List<VulkanImageAllocation> _allocations = new();

    public VulkanImageAliasGroup(VulkanAliasGroupKey key)
    {
        Key = key;
        AllowsAliasing = true;
        CreateInfoTemplate = VulkanImageCreateTemplate.FromDescriptor(key.AliasKey);
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

internal readonly record struct VulkanResourceExtentContext(
    uint WindowWidth,
    uint WindowHeight,
    uint InternalWidth,
    uint InternalHeight);

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
    string? FormatLabel,
    ESizedInternalFormat? SizedInternalFormat,
    EPixelInternalFormat? InternalFormat,
    RenderPipelineResourceUsage Usage,
    uint Samples,
    RenderResourceMipPolicy MipPolicy)
{
    public static VulkanImageCreateTemplate FromDescriptor(VulkanAliasKey aliasKey)
        => new(
            aliasKey.SizePolicy,
            Math.Max(aliasKey.ArrayLayers, 1u),
            aliasKey.FormatLabel,
            aliasKey.SizedInternalFormat,
            aliasKey.InternalFormat,
            aliasKey.Usage,
            Math.Max(1u, aliasKey.Samples),
            new RenderResourceMipPolicy(0u, Math.Max(1u, aliasKey.MipLevelCount)));
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

    public void EnsureAllocated(VulkanRenderer renderer)
    {
        if (_allocated)
            return;

        renderer.AllocatePhysicalImage(this, ref _image, ref _memory);
        _allocated = true;
        LastKnownLayout = ImageLayout.Undefined;
    }

    public bool TryEnsureAllocated(VulkanRenderer renderer, out string failureReason)
    {
        failureReason = string.Empty;
        if (_allocated)
            return true;

        if (!renderer.TryAllocatePhysicalImage(this, ref _image, ref _memory, out failureReason))
            return false;

        _allocated = true;
        LastKnownLayout = ImageLayout.Undefined;
        return true;
    }

    public void Destroy(VulkanRenderer renderer)
    {
        if (!_allocated)
            return;

        renderer.DestroyPhysicalImage(ref _image, ref _memory);
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
