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

/// <summary>
/// Tracks planned Vulkan image/buffer allocations for logical graph resources and aliases
/// transient-compatible resources when descriptors and usage are compatible.
/// </summary>
internal sealed class VulkanResourceAllocator
{
    private static long _nextOwnershipId;
    private int _retired;

    private readonly Dictionary<string, VulkanImageAllocation> _logicalTextureAllocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> _aliasGroups = new();
    private readonly Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup> _physicalGroups = new();
    private readonly Dictionary<string, VulkanPhysicalImageGroup> _resourceToPhysicalGroup = new(StringComparer.OrdinalIgnoreCase);
    // Logical aliases are generation metadata, not physical-image state. A physical
    // group can be borrowed by two allocator generations, so publishing a pending
    // generation must never rewrite metadata observed by the older generation.
    private readonly Dictionary<VulkanPhysicalImageGroup, VulkanImageAllocation[]> _logicalResourcesByPhysicalGroup = new();
    private readonly HashSet<VulkanPhysicalImageGroup> _borrowedPhysicalImageGroups = [];

    private readonly Dictionary<string, VulkanBufferAllocation> _logicalBufferAllocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<VulkanBufferAliasGroupKey, VulkanBufferAliasGroup> _bufferAliasGroups = new();
    private readonly Dictionary<VulkanBufferAliasGroupKey, VulkanPhysicalBufferGroup> _physicalBufferGroups = new();
    private readonly Dictionary<string, VulkanPhysicalBufferGroup> _resourceToPhysicalBufferGroup = new(StringComparer.OrdinalIgnoreCase);
    private VulkanTransientAttachmentPlan _transientAttachmentPlan = VulkanTransientAttachmentPlan.Baseline;

    public IReadOnlyDictionary<string, VulkanImageAllocation> LogicalTextureAllocations => _logicalTextureAllocations;
    public IReadOnlyDictionary<string, VulkanBufferAllocation> LogicalBufferAllocations => _logicalBufferAllocations;
    public IReadOnlyDictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> AliasGroups => _aliasGroups;
    public IReadOnlyDictionary<VulkanBufferAliasGroupKey, VulkanBufferAliasGroup> BufferAliasGroups => _bufferAliasGroups;
    public Dictionary<VulkanAliasGroupKey, VulkanImageAliasGroup>.ValueCollection EnumerateAliasGroups()
        => _aliasGroups.Values;

    public Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup>.ValueCollection EnumeratePhysicalGroups()
        => _physicalGroups.Values;

    public Dictionary<VulkanBufferAliasGroupKey, VulkanBufferAliasGroup>.ValueCollection EnumerateBufferAliasGroups()
        => _bufferAliasGroups.Values;

    public Dictionary<VulkanBufferAliasGroupKey, VulkanPhysicalBufferGroup>.ValueCollection EnumeratePhysicalBufferGroups()
        => _physicalBufferGroups.Values;
    public long OwnershipId { get; } = Interlocked.Increment(ref _nextOwnershipId);
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

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

    public void UpdatePlan(
        VulkanResourcePlan plan,
        VulkanTransientAttachmentPlan? transientAttachmentPlan = null)
    {
        ObjectDisposedException.ThrowIf(IsRetired, this);
        _transientAttachmentPlan = transientAttachmentPlan ?? VulkanTransientAttachmentPlan.Baseline;
        _logicalTextureAllocations.Clear();
        _aliasGroups.Clear();
        _physicalGroups.Clear();
        _resourceToPhysicalGroup.Clear();
        _logicalResourcesByPhysicalGroup.Clear();
        _borrowedPhysicalImageGroups.Clear();

        _logicalBufferAllocations.Clear();
        _bufferAliasGroups.Clear();
        _physicalBufferGroups.Clear();
        _resourceToPhysicalBufferGroup.Clear();

        foreach (VulkanAllocationRequest request in plan.AllTextures())
        {
            // Candidate analysis is not native lifetime authority. Preserve
            // dedicated images even for explicitly opted-in descriptors.
            VulkanAllocationRequest dedicatedRequest = request with
            {
                Descriptor = request.Descriptor with { SupportsAliasing = false },
            };
            VulkanAliasGroupKey key = VulkanAliasGroupKey.FromRequest(dedicatedRequest);
            if (!_aliasGroups.TryGetValue(key, out VulkanImageAliasGroup? group))
            {
                group = new VulkanImageAliasGroup(key);
                _aliasGroups.Add(key, group);
            }

            VulkanImageAllocation allocation = group.Add(dedicatedRequest);
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
        VulkanBackendObjectContext backendContext,
        bool supportsTransformFeedback,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner,
        VulkanResourceExtentContext extentContext)
    {
        DestroyPhysicalImages(backendContext);
        DestroyPhysicalBuffers(backendContext);

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
            // Do not remove required native usage bits to manufacture lazy
            // eligibility. Activation needs a validated native usage contract.
            VulkanTransientAttachmentPolicy transientAttachmentPolicy = VulkanTransientAttachmentPolicy.None;
            MemoryPropertyFlags memoryProperties = MemoryPropertyFlags.DeviceLocalBit;

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
            BufferUsageFlags usage = InferBufferUsage(group, supportsTransformFeedback);
            VulkanPhysicalBufferGroup physicalGroup = new(group, usage);

            foreach (VulkanBufferAllocation allocation in group.Allocations)
            {
                physicalGroup.AddLogical(allocation);
                _resourceToPhysicalBufferGroup[allocation.Name] = physicalGroup;
            }

            _physicalBufferGroups[group.Key] = physicalGroup;
        }

        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            _logicalResourcesByPhysicalGroup[group] = group.LogicalResources.ToArray();

        int activeAliasGroupCount = 0;
        int activeLazyGroupCount = 0;
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (group.LogicalResources.Count > 1 && group.AllowsAliasing)
                activeAliasGroupCount++;
            if (group.TransientAttachmentPolicy == VulkanTransientAttachmentPolicy.PreferLazilyAllocated)
                activeLazyGroupCount++;
        }
        Debug.VulkanEvery(
            $"Vulkan.TransientAttachmentPlan.{_transientAttachmentPlan.Mode}.{_transientAttachmentPlan.IsActive}.{activeAliasGroupCount}.{activeLazyGroupCount}",
            TimeSpan.FromSeconds(2),
            "[Vulkan.TransientAttachments] {0} activeAliasGroups={1} activeLazyGroups={2}.",
            _transientAttachmentPlan.Describe(),
            activeAliasGroupCount,
            activeLazyGroupCount);

        LogDeferredLightingPhysicalPlan(passMetadata, planner);
    }

    public void RebuildPhysicalPlan(
        VulkanBackendObjectContext backendContext,
        bool supportsTransformFeedback,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner)
        => RebuildPhysicalPlan(
            backendContext,
            supportsTransformFeedback,
            passMetadata,
            planner,
            new VulkanResourceExtentContext(1u, 1u, 1u, 1u));

    public int ReuseCompatiblePhysicalImagesFrom(
        VulkanResourceAllocator previousAllocator,
        out HashSet<VulkanPhysicalImageGroup>? reusedGroups)
    {
        reusedGroups = null;
        if (ReferenceEquals(previousAllocator, this))
            return 0;

        int reusedCount = 0;
        foreach (KeyValuePair<VulkanAliasGroupKey, VulkanPhysicalImageGroup> pair in _physicalGroups.ToArray())
        {
            VulkanPhysicalImageGroup pendingGroup = pair.Value;
            if (!previousAllocator._physicalGroups.TryGetValue(pair.Key, out VulkanPhysicalImageGroup? previousGroup) ||
                !pendingGroup.CanReusePhysicalAllocationFrom(previousGroup))
            {
                continue;
            }

            VulkanImageAllocation[] logicalResources = _logicalResourcesByPhysicalGroup.TryGetValue(
                pendingGroup,
                out VulkanImageAllocation[]? generationLogicalResources)
                ? generationLogicalResources
                : pendingGroup.LogicalResources.ToArray();
            _physicalGroups[pair.Key] = previousGroup;
            ReplacePhysicalGroupReferences(pendingGroup, previousGroup);
            _logicalResourcesByPhysicalGroup.Remove(pendingGroup);
            _logicalResourcesByPhysicalGroup[previousGroup] = logicalResources;
            _borrowedPhysicalImageGroups.Add(previousGroup);

            reusedGroups ??= new HashSet<VulkanPhysicalImageGroup>();
            reusedGroups.Add(previousGroup);
            reusedCount++;
        }

        return reusedCount;
    }

    public void CommitReusedPhysicalImageMetadata()
    {
        // The allocator owns generation-specific alias metadata. Physical groups
        // intentionally retain their original construction metadata while shared.
        _borrowedPhysicalImageGroups.Clear();
    }

    /// <summary>
    /// Captures the active physical groups borrowed by this pending allocator.
    /// A failed pending generation must exclude these groups from retirement: they
    /// are still owned by the published allocator until the generation commits.
    /// </summary>
    internal HashSet<VulkanPhysicalImageGroup>? CapturePendingReusedImageGroups()
        => _borrowedPhysicalImageGroups.Count == 0
            ? null
            : new HashSet<VulkanPhysicalImageGroup>(_borrowedPhysicalImageGroups);

    private void ReplacePhysicalGroupReferences(
        VulkanPhysicalImageGroup pendingGroup,
        VulkanPhysicalImageGroup reusedGroup)
    {
        if (ReferenceEquals(pendingGroup, reusedGroup))
            return;

        string[] resourceNames = _resourceToPhysicalGroup
            .Where(pair => ReferenceEquals(pair.Value, pendingGroup))
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string resourceName in resourceNames)
            _resourceToPhysicalGroup[resourceName] = reusedGroup;
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

    public bool TryEnsureImage(string resourceName, VulkanBackendObjectContext backendContext, out Image image)
    {
        if (TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group))
        {
            if (group is null || !group.TryEnsureAllocated(backendContext, out _))
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

    public bool TryEnsureBuffer(string resourceName, VulkanBackendObjectContext backendContext, out Buffer buffer, out ulong size)
    {
        if (TryGetPhysicalBufferGroupForResource(resourceName, out VulkanPhysicalBufferGroup? group))
        {
            group?.EnsureAllocated(backendContext);
            buffer = group?.Buffer ?? default;
            size = group?.SizeInBytes ?? 0;
            return buffer.Handle != 0;
        }

        buffer = default;
        size = 0;
        return false;
    }

    public void AllocatePhysicalImages(VulkanBackendObjectContext backendContext)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
            group.EnsureAllocated(backendContext);
    }

    public bool TryAllocatePhysicalImages(VulkanBackendObjectContext backendContext, out string failureReason)
    {
        failureReason = string.Empty;

        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (group.TryEnsureAllocated(backendContext, out failureReason))
                continue;

            failureReason = $"{failureReason}; failedGroup={DescribePhysicalGroupShort(group)} extent={group.ResolvedExtent.Width}x{group.ResolvedExtent.Height}x{group.ResolvedExtent.Depth} format={group.Format} usage={group.Usage} mips={group.MipLevels} samples={group.Samples}";
            return false;
        }

        return true;
    }

    public void AllocatePhysicalBuffers(VulkanBackendObjectContext backendContext)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.EnsureAllocated(backendContext);
    }

    public void DestroyPhysicalImages(
        VulkanBackendObjectContext backendContext,
        VulkanPhysicalImageGroup? exceptGroup = null,
        IReadOnlySet<VulkanPhysicalImageGroup>? exceptGroups = null)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (ReferenceEquals(group, exceptGroup) ||
                exceptGroups?.Contains(group) == true)
            {
                continue;
            }

            group.Destroy(backendContext);
        }
    }

    public void DestroyPhysicalImagesImmediate(
        VulkanBackendObjectContext backendContext,
        IReadOnlySet<VulkanPhysicalImageGroup>? exceptGroups = null)
    {
        foreach (VulkanPhysicalImageGroup group in _physicalGroups.Values)
        {
            if (exceptGroups?.Contains(group) == true)
                continue;

            group.DestroyImmediate(backendContext);
        }
    }

    public void DestroyPhysicalBuffers(VulkanBackendObjectContext backendContext)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.Destroy(backendContext);
    }

    public void DestroyPhysicalBuffersImmediate(VulkanBackendObjectContext backendContext)
    {
        foreach (VulkanPhysicalBufferGroup group in _physicalBufferGroups.Values)
            group.DestroyImmediate(backendContext);
    }

    public bool TryRetirePhysicalResources(
        VulkanBackendObjectContext backendContext,
        VulkanPhysicalImageGroup? exceptImageGroup = null,
        IReadOnlySet<VulkanPhysicalImageGroup>? exceptImageGroups = null,
        bool immediate = false)
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
            return false;

        if (immediate)
        {
            DestroyPhysicalImagesImmediate(backendContext, exceptImageGroups);
            DestroyPhysicalBuffersImmediate(backendContext);
        }
        else
        {
            DestroyPhysicalImages(backendContext, exceptImageGroup, exceptImageGroups);
            DestroyPhysicalBuffers(backendContext);
        }

        return true;
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
            hash.Add(pair.Value.SizePolicy.RoundUpDivisor);
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
        if (!VulkanResourceBindingKey.TryParse(usage.ResourceName, out VulkanResourceBindingKey binding))
            yield break;

        bool imageType = IsImageResourceType(usage.ResourceType);
        bool bufferType = IsBufferResourceType(usage.ResourceType);
        switch (binding.Kind)
        {
            case EVulkanResourceBindingKind.Output:
                foreach (string resourceName in ExpandOutputFrameBufferResources(usage, planner))
                    yield return resourceName;
                yield break;

            case EVulkanResourceBindingKind.FrameBuffer when imageType:
                if (!planner.TryGetFrameBufferDescriptor(binding.Name, out FrameBufferResourceDescriptor? descriptor))
                    yield break;

                foreach (FrameBufferAttachmentDescriptor attachment in descriptor?.Attachments ?? [])
                {
                    if (MatchesSlot(attachment.Attachment, binding.Slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
                        yield return planner.ResolveImageResourceName(attachment.ResourceName);
                }
                yield break;

            case EVulkanResourceBindingKind.Texture when imageType:
                yield return planner.ResolveImageResourceName(binding.Name);
                yield break;

            case EVulkanResourceBindingKind.Buffer when bufferType:
                yield return binding.Name;
                yield break;

            default:
                yield return imageType ? planner.ResolveImageResourceName(binding.Name) : binding.Name;
                yield break;
        }
    }
    private static IEnumerable<string> ExpandOutputFrameBufferResources(RenderPassResourceUsage usage, VulkanResourcePlanner planner)
    {
        if (!IsImageResourceType(usage.ResourceType) ||
            !planner.TryGetOutputFrameBufferDescriptor(out FrameBufferResourceDescriptor? descriptor) ||
            descriptor is null)
        {
            yield break;
        }

        string slot = ResolveOutputFrameBufferSlot(usage.ResourceType);
        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (MatchesSlot(attachment.Attachment, slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
                yield return planner.ResolveImageResourceName(attachment.ResourceName);
        }
    }

    private static string ResolveOutputFrameBufferSlot(ERenderPassResourceType resourceType)
        => resourceType switch
        {
            ERenderPassResourceType.DepthAttachment => "depth",
            ERenderPassResourceType.StencilAttachment => "stencil",
            _ => "color",
        };

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
                width = ResolveScaledExtent(internalWidth, sizePolicy.ScaleX, sizePolicy.RoundUpDivisor);
                height = ResolveScaledExtent(internalHeight, sizePolicy.ScaleY, sizePolicy.RoundUpDivisor);
                break;
            case RenderResourceSizeClass.WindowResolution:
                width = ResolveScaledExtent(windowWidth, sizePolicy.ScaleX, sizePolicy.RoundUpDivisor);
                height = ResolveScaledExtent(windowHeight, sizePolicy.ScaleY, sizePolicy.RoundUpDivisor);
                break;
            case RenderResourceSizeClass.Custom:
                width = ResolveScaledExtent(windowWidth, sizePolicy.ScaleX, sizePolicy.RoundUpDivisor);
                height = ResolveScaledExtent(windowHeight, sizePolicy.ScaleY, sizePolicy.RoundUpDivisor);
                break;
            default:
                width = windowWidth;
                height = windowHeight;
                break;
        }

        return new Extent3D(width, height, 1);
    }

    private static uint ResolveScaledExtent(uint extent, float scale, uint roundUpDivisor)
        => roundUpDivisor > 1u
            ? checked((Math.Max(extent, 1u) + roundUpDivisor - 1u) / roundUpDivisor)
            : (uint)Math.Max(1, (int)MathF.Round(extent * scale));

    private static Format ResolveFormat(VulkanImageCreateTemplate template)
    {
        if (template.SizedInternalFormat is ESizedInternalFormat sizedFormat)
            return VkFormatConversions.FromSizedFormat(sizedFormat);

        if (template.InternalFormat is EPixelInternalFormat internalFormat)
            return VkFormatConversions.FromPixelInternalFormat(internalFormat);

        string? formatLabel = template.FormatLabel;
        if (string.IsNullOrWhiteSpace(formatLabel))
            throw new InvalidOperationException("Vulkan image descriptor is missing a format.");

        if (Enum.TryParse(formatLabel, ignoreCase: true, out ESizedInternalFormat sizedFromLabel))
            return VkFormatConversions.FromSizedFormat(sizedFromLabel);

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
            EBufferTarget.ParameterBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            _ => BufferUsageFlags.StorageBufferBit
        };

    private static BufferUsageFlags ToVkUsageFlags(EBufferUsage usage)
        => usage switch
        {
            EBufferUsage.StaticDraw => BufferUsageFlags.TransferDstBit,
            EBufferUsage.StreamDraw or EBufferUsage.DynamicDraw => BufferUsageFlags.TransferDstBit,
            EBufferUsage.StreamRead or EBufferUsage.DynamicRead or EBufferUsage.StaticRead => BufferUsageFlags.TransferDstBit,
            EBufferUsage.StreamCopy or EBufferUsage.DynamicCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
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

