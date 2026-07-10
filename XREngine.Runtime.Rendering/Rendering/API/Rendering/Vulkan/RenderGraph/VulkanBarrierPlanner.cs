using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanBarrierPlanner
{
    // Keep the swapchain pseudo-pass outside the engine render-pass namespace.
    // EDefaultRenderPass.PreRender is -1, so using -1 here makes real PreRender
    // frame ops indistinguishable from frame-start swapchain barriers.
    internal const int SwapchainPassIndex = int.MinValue + 1;
    private static readonly PlannedImageBarrier[] _emptyImageBarriers = [];
    private static readonly PlannedBufferBarrier[] _emptyBufferBarriers = [];
    private static readonly PlannedSwapchainBarrier[] _emptySwapchainBarriers = [];

    private readonly List<PlannedImageBarrier> _imageBarriers = [];
    private readonly Dictionary<int, List<PlannedImageBarrier>> _perPassImageBarriers = [];
    private readonly Dictionary<PhysicalImageStateKey, PlannedImageState> _lastImageStates = [];

    private readonly List<PlannedBufferBarrier> _bufferBarriers = [];
    private readonly Dictionary<int, List<PlannedBufferBarrier>> _perPassBufferBarriers = [];
    private readonly Dictionary<string, PlannedBufferState> _lastBufferStates = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<PlannedSwapchainBarrier> _swapchainBarriers = [];
    private readonly Dictionary<int, List<PlannedSwapchainBarrier>> _perPassSwapchainBarriers = [];
    private PlannedImageState _lastSwapchainState;
    private bool _hasLastSwapchainState;
    private uint _lastSwapchainQueueOwner;
    private bool _hasLastSwapchainQueueOwner;

    private readonly Dictionary<PhysicalImageStateKey, uint> _lastImageQueueOwners = [];
    private readonly Dictionary<PhysicalImageStateKey, PendingPassImageUsage> _pendingPassImageUsages = [];
    private readonly Dictionary<string, uint> _lastBufferQueueOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _knownPassIndices = [];

    public IReadOnlyList<PlannedImageBarrier> ImageBarriers => _imageBarriers;
    public IReadOnlyList<PlannedBufferBarrier> BufferBarriers => _bufferBarriers;
    public IReadOnlyList<PlannedSwapchainBarrier> SwapchainBarriers => _swapchainBarriers;

    public IReadOnlyList<PlannedImageBarrier> GetBarriersForPass(int passIndex)
        => _perPassImageBarriers.TryGetValue(passIndex, out var list) ? list : _emptyImageBarriers;

    public IReadOnlyList<PlannedBufferBarrier> GetBufferBarriersForPass(int passIndex)
        => _perPassBufferBarriers.TryGetValue(passIndex, out var list) ? list : _emptyBufferBarriers;

    public IReadOnlyList<PlannedSwapchainBarrier> GetSwapchainBarriersForPass(int passIndex)
        => _perPassSwapchainBarriers.TryGetValue(passIndex, out var list) ? list : _emptySwapchainBarriers;

    /// <summary>
    /// Returns true if <paramref name="passIndex"/> was present in the pass metadata
    /// used during the last <see cref="Rebuild"/>. Passes not known to the planner
    /// have no planned barriers, so callers should emit a conservative full-pipeline
    /// barrier to prevent GPU crashes from missing layout transitions.
    /// </summary>
    public bool HasKnownPass(int passIndex)
        => passIndex == SwapchainPassIndex || _knownPassIndices.Contains(passIndex);

    /// <summary>
    /// Returns the smallest known pass index from the last <see cref="Rebuild"/>,
    /// or <c>null</c> if no passes are known. Used to substitute a real pass's
    /// image/buffer barriers when an op falls back to an unknown pass index.
    /// </summary>
    public int? GetFirstKnownPassIndex()
    {
        if (_knownPassIndices.Count == 0)
            return null;

        int min = int.MaxValue;
        foreach (int idx in _knownPassIndices)
        {
            if (idx < min)
                min = idx;
        }
        return min;
    }

    internal readonly record struct QueueOwnershipConfig(
        uint GraphicsQueueFamilyIndex,
        uint? ComputeQueueFamilyIndex = null,
        uint? TransferQueueFamilyIndex = null)
    {
        public uint ResolveOwner(ERenderGraphPassStage passStage, ERenderPassResourceType resourceType)
        {
            if (resourceType is ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination)
                return TransferQueueFamilyIndex ?? GraphicsQueueFamilyIndex;

            if (passStage == ERenderGraphPassStage.Compute)
                return ComputeQueueFamilyIndex ?? GraphicsQueueFamilyIndex;

            return GraphicsQueueFamilyIndex;
        }
    }

    public void Rebuild(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner resourcePlanner,
        VulkanResourceAllocator resourceAllocator,
        RenderGraphSynchronizationInfo? synchronization = null,
        QueueOwnershipConfig? queueOwnership = null)
    {
        _imageBarriers.Clear();
        _perPassImageBarriers.Clear();
        _lastImageStates.Clear();
        _lastImageQueueOwners.Clear();

        _bufferBarriers.Clear();
        _perPassBufferBarriers.Clear();
        _lastBufferStates.Clear();
        _lastBufferQueueOwners.Clear();
        _swapchainBarriers.Clear();
        _perPassSwapchainBarriers.Clear();
        _lastSwapchainState = default;
        _hasLastSwapchainState = false;
        _lastSwapchainQueueOwner = 0u;
        _hasLastSwapchainQueueOwner = false;
        _knownPassIndices.Clear();

        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
            _knownPassIndices.Add(pass.PassIndex);

        RenderGraphSynchronizationInfo syncInfo = synchronization ?? RenderGraphSynchronizationPlanner.Build(passMetadata);
        QueueOwnershipConfig ownership = queueOwnership ?? new QueueOwnershipConfig(0u);

        foreach (RenderPassMetadata pass in RenderGraphSynchronizationPlanner.TopologicallySort(passMetadata))
        {
            IReadOnlyList<RenderGraphSynchronizationEdge> consumerEdges = syncInfo.GetEdgesForConsumer(pass.PassIndex);
            _pendingPassImageUsages.Clear();

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                RenderGraphSynchronizationEdge? edge = FindConsumerEdge(consumerEdges, usage);

                if (IsSwapchainTargetUsage(usage, resourcePlanner))
                {
                    TrackSwapchainUsage(pass, usage, edge, ownership);
                    continue;
                }

                if (ShouldTrackImage(usage.ResourceType))
                    AccumulateImageUsage(pass, usage, resourcePlanner, resourceAllocator, edge, ownership);

                if (ShouldTrackBuffer(usage.ResourceType))
                    TrackBufferUsage(pass, usage, resourcePlanner, edge, ownership);
            }

            foreach (PendingPassImageUsage pending in _pendingPassImageUsages.Values)
                TrackImageUsage(pass, pending);
        }
    }

    private static RenderGraphSynchronizationEdge? FindConsumerEdge(
        IReadOnlyList<RenderGraphSynchronizationEdge> consumerEdges,
        RenderPassResourceUsage usage)
    {
        RenderGraphSynchronizationEdge? match = null;
        for (int i = 0; i < consumerEdges.Count; i++)
        {
            RenderGraphSynchronizationEdge edge = consumerEdges[i];
            if (!edge.DependencyOnly &&
                edge.ResourceType == usage.ResourceType &&
                string.Equals(edge.ResourceName, usage.ResourceName, StringComparison.OrdinalIgnoreCase) &&
                edge.SubresourceRange.Equals(usage.SubresourceRange))
            {
                match = edge;
            }
        }

        return match;
    }

    private void TrackSwapchainUsage(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        RenderGraphSynchronizationEdge? syncEdge,
        QueueOwnershipConfig ownership)
    {
        PlannedImageState desiredState = syncEdge is null
            ? PlannedImageState.FromSwapchainUsage(usage, pass.Stage)
            : PlannedImageState.FromSwapchainSyncState(syncEdge.ConsumerState, usage.ResourceType, pass.Stage);

        uint desiredOwnerQueue = ownership.ResolveOwner(pass.Stage, usage.ResourceType);
        uint previousOwnerQueue = _hasLastSwapchainQueueOwner ? _lastSwapchainQueueOwner : desiredOwnerQueue;
        uint srcQueueFamily = previousOwnerQueue != desiredOwnerQueue ? previousOwnerQueue : Vk.QueueFamilyIgnored;
        uint dstQueueFamily = previousOwnerQueue != desiredOwnerQueue ? desiredOwnerQueue : Vk.QueueFamilyIgnored;

        PlannedSwapchainBarrier? plannedBarrier = null;
        if (_hasLastSwapchainState)
        {
            PlannedImageState previousState = _lastSwapchainState;
            if (syncEdge is not null)
                previousState = PlannedImageState.FromSwapchainSyncState(syncEdge.ProducerState, usage.ResourceType, pass.Stage);

            if (!previousState.Equals(desiredState))
            {
                plannedBarrier = new PlannedSwapchainBarrier(
                    pass.PassIndex,
                    usage.ResourceName,
                    usage.ResourceType,
                    previousState,
                    desiredState,
                    srcQueueFamily,
                    dstQueueFamily);
            }
        }
        else
        {
            plannedBarrier = new PlannedSwapchainBarrier(
                SwapchainPassIndex,
                usage.ResourceName,
                usage.ResourceType,
                PlannedImageState.SwapchainPresentInitial(),
                desiredState,
                srcQueueFamily,
                dstQueueFamily);
        }

        if (plannedBarrier.HasValue)
            AddSwapchainBarrier(plannedBarrier.Value);

        _lastSwapchainState = desiredState;
        _hasLastSwapchainState = true;
        _lastSwapchainQueueOwner = desiredOwnerQueue;
        _hasLastSwapchainQueueOwner = true;
    }

    private void AccumulateImageUsage(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        VulkanResourcePlanner resourcePlanner,
        VulkanResourceAllocator resourceAllocator,
        RenderGraphSynchronizationEdge? syncEdge,
        QueueOwnershipConfig ownership)
    {
        foreach (ImageResourceBinding binding in ExpandImageLogicalResources(usage, resourcePlanner))
        {
            string logicalResource = binding.ResourceName;
            if (!resourceAllocator.TryGetPhysicalGroupForResource(logicalResource, out VulkanPhysicalImageGroup? group) || group is null)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.BarrierPlanner.UnresolvedImage.{pass.PassIndex}.{logicalResource}",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan] Barrier planner could not resolve image resource '{0}' for pass {1} ({2}); dedicated/external consumers must register an explicit dependency before recording.",
                    logicalResource,
                    pass.PassIndex,
                    pass.Name);
                continue;
            }

            ResolvedImageSubresourceRange bindingRange = ResolveSubresourceRange(binding.Range, group);
            foreach (ResolvedImageSubresourceRange range in ExpandTrackingRanges(bindingRange))
            {
                PhysicalImageStateKey stateKey = BuildImageStateKey(range, group);
                PlannedImageState desiredState = syncEdge is null
                    ? PlannedImageState.FromUsage(usage, group, pass.Stage)
                    : PlannedImageState.FromSyncState(syncEdge.ConsumerState, usage.ResourceType, group, pass.Stage);
                uint desiredOwnerQueue = ownership.ResolveOwner(pass.Stage, usage.ResourceType);
                if (_pendingPassImageUsages.TryGetValue(stateKey, out PendingPassImageUsage? pending))
                {
                    pending.Merge(usage, desiredState, desiredOwnerQueue, pass);
                }
                else
                {
                    _pendingPassImageUsages[stateKey] = new PendingPassImageUsage(
                        stateKey,
                        logicalResource,
                        group,
                        range,
                        desiredState,
                        desiredOwnerQueue,
                        usage);
                }
            }
        }
    }

    private void TrackImageUsage(RenderPassMetadata pass, PendingPassImageUsage pending)
    {
        PhysicalImageStateKey stateKey = pending.Key;
        PlannedImageState desiredState = pending.State;
        uint desiredOwnerQueue = pending.OwnerQueue;
        uint previousOwnerQueue = _lastImageQueueOwners.TryGetValue(stateKey, out uint existingOwner)
            ? existingOwner
            : desiredOwnerQueue;
        uint srcQueueFamily = previousOwnerQueue != desiredOwnerQueue ? previousOwnerQueue : Vk.QueueFamilyIgnored;
        uint dstQueueFamily = previousOwnerQueue != desiredOwnerQueue ? desiredOwnerQueue : Vk.QueueFamilyIgnored;

        PlannedImageState previousState = _lastImageStates.TryGetValue(stateKey, out PlannedImageState tracked)
            ? tracked
            : PlannedImageState.Initial(desiredState.AspectMask);
        if (!previousState.Equals(desiredState) || srcQueueFamily != Vk.QueueFamilyIgnored)
        {
            AddImageBarrier(new PlannedImageBarrier(
                pass.PassIndex,
                pending.ResourceName,
                pending.Group,
                pending.Range,
                previousState,
                desiredState,
                srcQueueFamily,
                dstQueueFamily));
        }

        _lastImageStates[stateKey] = desiredState;
        _lastImageQueueOwners[stateKey] = desiredOwnerQueue;
    }

    private void TrackBufferUsage(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        VulkanResourcePlanner resourcePlanner,
        RenderGraphSynchronizationEdge? syncEdge,
        QueueOwnershipConfig ownership)
    {
        foreach (string logicalResource in ExpandBufferLogicalResources(usage.ResourceName, resourcePlanner))
        {
            if (string.IsNullOrWhiteSpace(logicalResource))
                continue;

            PlannedBufferState desiredState = syncEdge is null
                ? PlannedBufferState.FromUsage(usage, pass.Stage)
                : PlannedBufferState.FromSyncState(syncEdge.ConsumerState, usage.ResourceType, pass.Stage);
            PlannedBufferBarrier? plannedBarrier = null;
            uint desiredOwnerQueue = ownership.ResolveOwner(pass.Stage, usage.ResourceType);
            uint previousOwnerQueue = desiredOwnerQueue;
            if (_lastBufferQueueOwners.TryGetValue(logicalResource, out uint existingOwner))
                previousOwnerQueue = existingOwner;

            uint srcQueueFamily = previousOwnerQueue != desiredOwnerQueue ? previousOwnerQueue : Vk.QueueFamilyIgnored;
            uint dstQueueFamily = previousOwnerQueue != desiredOwnerQueue ? desiredOwnerQueue : Vk.QueueFamilyIgnored;

            if (_lastBufferStates.TryGetValue(logicalResource, out PlannedBufferState previousState))
            {
                if (syncEdge is not null)
                    previousState = PlannedBufferState.FromSyncState(syncEdge.ProducerState, usage.ResourceType, pass.Stage);

                if (!previousState.Equals(desiredState))
                    plannedBarrier = new PlannedBufferBarrier(pass.PassIndex, logicalResource, previousState, desiredState, srcQueueFamily, dstQueueFamily);
            }
            else
            {
                plannedBarrier = new PlannedBufferBarrier(
                    pass.PassIndex,
                    logicalResource,
                    PlannedBufferState.Initial(),
                    desiredState,
                    srcQueueFamily,
                    dstQueueFamily);
            }

            if (plannedBarrier.HasValue)
                AddBufferBarrier(plannedBarrier.Value);

            _lastBufferStates[logicalResource] = desiredState;
            _lastBufferQueueOwners[logicalResource] = desiredOwnerQueue;
        }
    }

    private void AddImageBarrier(PlannedImageBarrier barrier)
    {
        _imageBarriers.Add(barrier);

        if (!_perPassImageBarriers.TryGetValue(barrier.PassIndex, out var list))
        {
            list = [];
            _perPassImageBarriers[barrier.PassIndex] = list;
        }

        list.Add(barrier);
    }

    private void AddBufferBarrier(PlannedBufferBarrier barrier)
    {
        _bufferBarriers.Add(barrier);

        if (!_perPassBufferBarriers.TryGetValue(barrier.PassIndex, out var list))
        {
            list = [];
            _perPassBufferBarriers[barrier.PassIndex] = list;
        }

        list.Add(barrier);
    }

    private void AddSwapchainBarrier(PlannedSwapchainBarrier barrier)
    {
        _swapchainBarriers.Add(barrier);

        if (!_perPassSwapchainBarriers.TryGetValue(barrier.PassIndex, out var list))
        {
            list = [];
            _perPassSwapchainBarriers[barrier.PassIndex] = list;
        }

        list.Add(barrier);
    }

    private static bool IsSwapchainTargetUsage(RenderPassResourceUsage usage, VulkanResourcePlanner planner)
        => usage.ResourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase)
            && !planner.TryGetOutputFrameBufferDescriptor(out _)
            && ShouldTrackImage(usage.ResourceType);

    private static bool ShouldTrackImage(ERenderPassResourceType type)
        => type is ERenderPassResourceType.ColorAttachment
            or ERenderPassResourceType.DepthAttachment
            or ERenderPassResourceType.StencilAttachment
            or ERenderPassResourceType.ResolveAttachment
            or ERenderPassResourceType.SampledTexture
            or ERenderPassResourceType.StorageTexture
            or ERenderPassResourceType.TransferSource
            or ERenderPassResourceType.TransferDestination;

    private static bool ShouldTrackBuffer(ERenderPassResourceType type)
        => type is ERenderPassResourceType.UniformBuffer
            or ERenderPassResourceType.StorageBuffer
            or ERenderPassResourceType.VertexBuffer
            or ERenderPassResourceType.IndexBuffer
            or ERenderPassResourceType.IndirectBuffer
            or ERenderPassResourceType.TransferSource
            or ERenderPassResourceType.TransferDestination;

    private static IEnumerable<ImageResourceBinding> ExpandImageLogicalResources(RenderPassResourceUsage usage, VulkanResourcePlanner planner)
    {
        string resourceBinding = usage.ResourceName;
        if (string.IsNullOrWhiteSpace(resourceBinding))
            yield break;

        if (resourceBinding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            foreach (ImageResourceBinding binding in ExpandOutputFrameBufferResources(usage, planner))
                yield return binding;

            yield break; // swapchain target handled separately when no offscreen output FBO exists
        }

        if (resourceBinding.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
            yield break;

        if (resourceBinding.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = resourceBinding.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                yield break;

            string fboName = segments[1];
            string slot = segments.Length >= 3 ? segments[2] : "color";
            if (!planner.TryGetFrameBufferDescriptor(fboName, out FrameBufferResourceDescriptor? descriptor) || descriptor is null)
                yield break;

            foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
            {
                if (MatchesSlot(attachment.Attachment, slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
                    yield return new ImageResourceBinding(
                        planner.ResolveImageResourceName(attachment.ResourceName),
                        ResolveAttachmentRange(attachment, usage.SubresourceRange));
            }

            yield break;
        }

        if (resourceBinding.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
        {
            string textureName = resourceBinding["tex::".Length..];
            if (!string.IsNullOrWhiteSpace(textureName))
                yield return new ImageResourceBinding(planner.ResolveImageResourceName(textureName), usage.SubresourceRange);
            yield break;
        }

        // For transfer usages, avoid routing named data buffers through image barriers.
        if (planner.TryGetBufferDescriptor(resourceBinding, out _))
            yield break;

        yield return new ImageResourceBinding(planner.ResolveImageResourceName(resourceBinding), usage.SubresourceRange);
    }

    private static IEnumerable<ImageResourceBinding> ExpandOutputFrameBufferResources(RenderPassResourceUsage usage, VulkanResourcePlanner planner)
    {
        if (!planner.TryGetOutputFrameBufferDescriptor(out FrameBufferResourceDescriptor? descriptor) ||
            descriptor is null)
        {
            yield break;
        }

        string slot = ResolveOutputFrameBufferSlot(usage.ResourceType);
        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (MatchesSlot(attachment.Attachment, slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
            {
                yield return new ImageResourceBinding(
                    planner.ResolveImageResourceName(attachment.ResourceName),
                    ResolveAttachmentRange(attachment, usage.SubresourceRange));
            }
        }
    }

    private static string ResolveOutputFrameBufferSlot(ERenderPassResourceType resourceType)
        => resourceType switch
        {
            ERenderPassResourceType.DepthAttachment => "depth",
            ERenderPassResourceType.StencilAttachment => "stencil",
            _ => "color",
        };

    private static IEnumerable<string> ExpandBufferLogicalResources(string resourceBinding, VulkanResourcePlanner planner)
    {
        if (string.IsNullOrWhiteSpace(resourceBinding))
            yield break;

        if (resourceBinding.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
        {
            string bufferName = resourceBinding["buf::".Length..];
            if (!string.IsNullOrWhiteSpace(bufferName))
                yield return bufferName;
            yield break;
        }

        if (resourceBinding.StartsWith("tex::", StringComparison.OrdinalIgnoreCase) ||
            resourceBinding.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase) ||
            resourceBinding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        // If a descriptor exists, this is a tracked logical buffer.
        if (planner.TryGetBufferDescriptor(resourceBinding, out _))
        {
            yield return resourceBinding;
            yield break;
        }

        // Fallback for metadata that references raw names but uses no explicit registry descriptor.
        yield return resourceBinding;
    }

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

    private static RenderGraphSubresourceRange ResolveAttachmentRange(
        FrameBufferAttachmentDescriptor attachment,
        RenderGraphSubresourceRange usageRange)
    {
        if (!usageRange.IsWholeResource)
            return usageRange;

        uint mipLevel = (uint)Math.Max(attachment.MipLevel, 0);
        if (attachment.LayerIndex < 0)
            return new RenderGraphSubresourceRange(mipLevel, 1u, 0u, RenderGraphSubresourceRange.Remaining);

        return new RenderGraphSubresourceRange(mipLevel, 1u, (uint)attachment.LayerIndex, 1u);
    }

    private static ResolvedImageSubresourceRange ResolveSubresourceRange(
        RenderGraphSubresourceRange range,
        VulkanPhysicalImageGroup group)
    {
        uint mipLevels = Math.Max(group.MipLevels, 1u);
        uint layers = Math.Max(group.Template.Layers, 1u);
        if (range.BaseMipLevel >= mipLevels || range.BaseArrayLayer >= layers)
            return new ResolvedImageSubresourceRange(range.BaseMipLevel, 0u, range.BaseArrayLayer, 0u);

        uint baseMip = range.BaseMipLevel;
        uint baseLayer = range.BaseArrayLayer;
        uint levelCount = range.MipLevelCount == RenderGraphSubresourceRange.Remaining
            ? mipLevels - baseMip
            : Math.Min(Math.Max(range.MipLevelCount, 1u), mipLevels - baseMip);
        uint layerCount = range.ArrayLayerCount == RenderGraphSubresourceRange.Remaining
            ? layers - baseLayer
            : Math.Min(Math.Max(range.ArrayLayerCount, 1u), layers - baseLayer);

        return new ResolvedImageSubresourceRange(baseMip, levelCount, baseLayer, layerCount);
    }

    private static PhysicalImageStateKey BuildImageStateKey(
        ResolvedImageSubresourceRange range,
        VulkanPhysicalImageGroup group)
        => new(group, range);

    private static IEnumerable<ResolvedImageSubresourceRange> ExpandTrackingRanges(
        ResolvedImageSubresourceRange range)
    {
        for (uint mip = range.BaseMipLevel; mip < range.BaseMipLevel + range.LevelCount; mip++)
        {
            for (uint layer = range.BaseArrayLayer; layer < range.BaseArrayLayer + range.LayerCount; layer++)
                yield return new ResolvedImageSubresourceRange(mip, 1u, layer, 1u);
        }
    }

    private readonly record struct PhysicalImageStateKey(
        VulkanPhysicalImageGroup Group,
        ResolvedImageSubresourceRange Range);

    private sealed class PendingPassImageUsage
    {
        private bool _sampled;
        private bool _depthAttachment;
        private bool _depthWrites;
        private bool _colorAttachment;
        private bool _storage;
        private bool _storageWrites;

        public PendingPassImageUsage(
            PhysicalImageStateKey key,
            string resourceName,
            VulkanPhysicalImageGroup group,
            ResolvedImageSubresourceRange range,
            PlannedImageState state,
            uint ownerQueue,
            RenderPassResourceUsage usage)
        {
            Key = key;
            ResourceName = resourceName;
            Group = group;
            Range = range;
            State = state;
            OwnerQueue = ownerQueue;
            AccumulateUsageFlags(usage);
        }

        public PhysicalImageStateKey Key { get; }
        public string ResourceName { get; }
        public VulkanPhysicalImageGroup Group { get; }
        public ResolvedImageSubresourceRange Range { get; }
        public PlannedImageState State { get; private set; }
        public uint OwnerQueue { get; }

        public void Merge(
            RenderPassResourceUsage usage,
            PlannedImageState desired,
            uint ownerQueue,
            RenderPassMetadata pass)
        {
            if (ownerQueue != OwnerQueue)
            {
                throw new InvalidOperationException(
                    $"Pass {pass.PassIndex} ('{pass.Name}') uses physical image 0x{Group.Image.Handle:X} " +
                    $"from multiple queue families ({OwnerQueue} and {ownerQueue}) in one pass.");
            }

            AccumulateUsageFlags(usage);
            PipelineStageFlags stages = State.StageMask | desired.StageMask;
            AccessFlags access = State.AccessMask | desired.AccessMask;
            ImageAspectFlags aspect = State.AspectMask | desired.AspectMask;

            if (_sampled && _depthAttachment)
            {
                if (_depthWrites)
                {
                    throw new InvalidOperationException(
                        $"Pass {pass.PassIndex} ('{pass.Name}') samples and writes depth image 0x{Group.Image.Handle:X} " +
                        $"mip={Range.BaseMipLevel} layer={Range.BaseArrayLayer}. Split the pass or use an explicit supported feedback-loop path.");
                }

                State = new PlannedImageState(
                    ImageLayout.DepthStencilReadOnlyOptimal,
                    stages |
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                    access |
                        AccessFlags.ShaderReadBit |
                        AccessFlags.DepthStencilAttachmentReadBit,
                    aspect);
                return;
            }

            if (_sampled && _colorAttachment)
            {
                throw new InvalidOperationException(
                    $"Pass {pass.PassIndex} ('{pass.Name}') samples and attaches color image 0x{Group.Image.Handle:X} " +
                    $"mip={Range.BaseMipLevel} layer={Range.BaseArrayLayer}. Use a separate source or an explicit local-read/feedback-loop path.");
            }

            if (_sampled && _storage)
            {
                if (_storageWrites)
                {
                    throw new InvalidOperationException(
                        $"Pass {pass.PassIndex} ('{pass.Name}') samples and storage-writes image 0x{Group.Image.Handle:X} " +
                        $"mip={Range.BaseMipLevel} layer={Range.BaseArrayLayer}. Split the pass or declare an explicit feedback-loop path.");
                }

                State = new PlannedImageState(ImageLayout.General, stages, access | AccessFlags.ShaderReadBit, aspect);
                return;
            }

            if (State.Layout != desired.Layout)
            {
                throw new InvalidOperationException(
                    $"Pass {pass.PassIndex} ('{pass.Name}') requires incompatible layouts {State.Layout} and {desired.Layout} " +
                    $"for physical image 0x{Group.Image.Handle:X} mip={Range.BaseMipLevel} layer={Range.BaseArrayLayer}.");
            }

            State = new PlannedImageState(State.Layout, stages, access, aspect);
        }

        private void AccumulateUsageFlags(RenderPassResourceUsage usage)
        {
            bool writes = usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;
            switch (usage.ResourceType)
            {
                case ERenderPassResourceType.SampledTexture:
                    _sampled = true;
                    break;
                case ERenderPassResourceType.DepthAttachment:
                case ERenderPassResourceType.StencilAttachment:
                    _depthAttachment = true;
                    _depthWrites |= writes;
                    break;
                case ERenderPassResourceType.ColorAttachment:
                case ERenderPassResourceType.ResolveAttachment:
                    _colorAttachment = true;
                    break;
                case ERenderPassResourceType.StorageTexture:
                    _storage = true;
                    _storageWrites |= writes;
                    break;
            }
        }
    }

    private readonly record struct ImageResourceBinding(
        string ResourceName,
        RenderGraphSubresourceRange Range);

    internal readonly record struct ResolvedImageSubresourceRange(
        uint BaseMipLevel,
        uint LevelCount,
        uint BaseArrayLayer,
        uint LayerCount)
    {
        public bool CoversWholeImage(VulkanPhysicalImageGroup group)
            => BaseMipLevel == 0u &&
               LevelCount >= Math.Max(group.MipLevels, 1u) &&
               BaseArrayLayer == 0u &&
               LayerCount >= Math.Max(group.Template.Layers, 1u);
    }

    internal readonly record struct PlannedImageBarrier(
        int PassIndex,
        string ResourceName,
        VulkanPhysicalImageGroup Group,
        ResolvedImageSubresourceRange Range,
        PlannedImageState Previous,
        PlannedImageState Next,
        uint SrcQueueFamilyIndex,
        uint DstQueueFamilyIndex);

    internal readonly record struct PlannedBufferBarrier(
        int PassIndex,
        string ResourceName,
        PlannedBufferState Previous,
        PlannedBufferState Next,
        uint SrcQueueFamilyIndex,
        uint DstQueueFamilyIndex);

    internal readonly record struct PlannedSwapchainBarrier(
        int PassIndex,
        string ResourceName,
        ERenderPassResourceType ResourceType,
        PlannedImageState Previous,
        PlannedImageState Next,
        uint SrcQueueFamilyIndex,
        uint DstQueueFamilyIndex);

    internal readonly struct PlannedImageState(ImageLayout layout, PipelineStageFlags stageMask, AccessFlags accessMask, ImageAspectFlags aspectMask) : IEquatable<PlannedImageState>
    {
        public ImageLayout Layout { get; } = layout;
        public PipelineStageFlags StageMask { get; } = stageMask;
        public AccessFlags AccessMask { get; } = accessMask;
        public ImageAspectFlags AspectMask { get; } = aspectMask;

        public static PlannedImageState Initial(ImageAspectFlags aspect)
            => new(ImageLayout.Undefined, PipelineStageFlags.TopOfPipeBit, AccessFlags.None, aspect);

        public static PlannedImageState SwapchainPresentInitial()
            => new(ImageLayout.PresentSrcKhr, PipelineStageFlags.BottomOfPipeBit, AccessFlags.None, ImageAspectFlags.ColorBit);

        public static PlannedImageState FromSwapchainUsage(RenderPassResourceUsage usage, ERenderGraphPassStage passStage)
        {
            ImageLayout layout = ResolveLayout(usage.ResourceType);
            PipelineStageFlags stages = ResolveStage(usage.ResourceType, passStage);
            AccessFlags access = ResolveAccess(usage.ResourceType, usage.Access);
            return new(layout, stages, access, ImageAspectFlags.ColorBit);
        }

        public static PlannedImageState FromSwapchainSyncState(
            RenderGraphSyncState state,
            ERenderPassResourceType resourceType,
            ERenderGraphPassStage fallbackStage)
        {
            ImageLayout layout = ResolveLayoutFromSync(state.Layout, resourceType, group: null);
            PipelineStageFlags stages = ResolveStageFromSync(state.StageMask, resourceType, fallbackStage);
            AccessFlags access = ResolveAccessFromSync(state.AccessMask, resourceType);
            return new(layout, stages, access, ImageAspectFlags.ColorBit);
        }

        public static PlannedImageState FromUsage(RenderPassResourceUsage usage, VulkanPhysicalImageGroup group, ERenderGraphPassStage passStage)
        {
            ImageAspectFlags aspect = ResolveAspect(group, usage.ResourceType);
            ImageLayout layout = ResolveLayout(usage.ResourceType, group);
            PipelineStageFlags stages = ResolveStage(usage.ResourceType, passStage);
            AccessFlags access = ResolveAccess(usage.ResourceType, usage.Access);
            return new(layout, stages, access, aspect);
        }

        public static PlannedImageState FromSyncState(
            RenderGraphSyncState state,
            ERenderPassResourceType resourceType,
            VulkanPhysicalImageGroup group,
            ERenderGraphPassStage fallbackStage)
        {
            ImageAspectFlags aspect = ResolveAspect(group, resourceType);
            ImageLayout layout = ResolveLayoutFromSync(state.Layout, resourceType, group);
            PipelineStageFlags stages = ResolveStageFromSync(state.StageMask, resourceType, fallbackStage);
            AccessFlags access = ResolveAccessFromSync(state.AccessMask, resourceType);
            return new(layout, stages, access, aspect);
        }

        public bool Equals(PlannedImageState other)
            => Layout == other.Layout && StageMask == other.StageMask && AccessMask == other.AccessMask && AspectMask == other.AspectMask;

        public override bool Equals(object? obj)
            => obj is PlannedImageState other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)Layout, (int)StageMask, (int)AccessMask, (int)AspectMask);
    }

    internal readonly struct PlannedBufferState(PipelineStageFlags stageMask, AccessFlags accessMask) : IEquatable<PlannedBufferState>
    {
        public PipelineStageFlags StageMask { get; } = stageMask;
        public AccessFlags AccessMask { get; } = accessMask;

        public static PlannedBufferState Initial()
            => new(PipelineStageFlags.TopOfPipeBit, AccessFlags.None);

        public static PlannedBufferState FromUsage(RenderPassResourceUsage usage, ERenderGraphPassStage passStage)
        {
            PipelineStageFlags stage = ResolveStage(usage.ResourceType, passStage);
            AccessFlags access = ResolveAccess(usage.ResourceType, usage.Access);
            return new(stage, access);
        }

        public static PlannedBufferState FromSyncState(
            RenderGraphSyncState state,
            ERenderPassResourceType resourceType,
            ERenderGraphPassStage fallbackStage)
        {
            PipelineStageFlags stage = ResolveStageFromSync(state.StageMask, resourceType, fallbackStage);
            AccessFlags access = ResolveAccessFromSync(state.AccessMask, resourceType);
            return new(stage, access);
        }

        public bool Equals(PlannedBufferState other)
            => StageMask == other.StageMask && AccessMask == other.AccessMask;

        public override bool Equals(object? obj)
            => obj is PlannedBufferState other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)StageMask, (int)AccessMask);
    }

    private static ImageLayout ResolveLayout(ERenderPassResourceType type, VulkanPhysicalImageGroup? group = null)
        => type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => ImageLayout.ColorAttachmentOptimal,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => ImageLayout.DepthStencilAttachmentOptimal,
            ERenderPassResourceType.SampledTexture => ResolveSampledTextureLayout(group),
            ERenderPassResourceType.StorageTexture => ImageLayout.General,
            ERenderPassResourceType.TransferSource => ImageLayout.TransferSrcOptimal,
            ERenderPassResourceType.TransferDestination => ImageLayout.TransferDstOptimal,
            _ => ImageLayout.General
        };

    private static PipelineStageFlags ResolveStage(ERenderPassResourceType type, ERenderGraphPassStage passStage)
    {
        return type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => PipelineStageFlags.ColorAttachmentOutputBit,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination => PipelineStageFlags.TransferBit,
            ERenderPassResourceType.VertexBuffer or ERenderPassResourceType.IndexBuffer => PipelineStageFlags.VertexInputBit,
            ERenderPassResourceType.IndirectBuffer => PipelineStageFlags.DrawIndirectBit,
            ERenderPassResourceType.UniformBuffer => SampleStage(passStage),
            ERenderPassResourceType.StorageBuffer => StorageStage(passStage),
            ERenderPassResourceType.SampledTexture => SampleStage(passStage),
            ERenderPassResourceType.StorageTexture => StorageStage(passStage),
            _ => DefaultStage(passStage)
        };

        static PipelineStageFlags SampleStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit
            };

        static PipelineStageFlags StorageStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit
            };

        static PipelineStageFlags DefaultStage(ERenderGraphPassStage stage)
            => stage switch
            {
                ERenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                ERenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.AllGraphicsBit
            };
    }

    private static AccessFlags ResolveAccess(ERenderPassResourceType type, ERenderGraphAccess accessIntent)
    {
        bool reads = accessIntent is ERenderGraphAccess.Read or ERenderGraphAccess.ReadWrite;
        bool writes = accessIntent is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;

        AccessFlags flags = AccessFlags.None;

        switch (type)
        {
            case ERenderPassResourceType.ColorAttachment:
            case ERenderPassResourceType.ResolveAttachment:
                if (reads)
                    flags |= AccessFlags.ColorAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.ColorAttachmentWriteBit;
                break;
            case ERenderPassResourceType.DepthAttachment:
            case ERenderPassResourceType.StencilAttachment:
                if (reads)
                    flags |= AccessFlags.DepthStencilAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.DepthStencilAttachmentWriteBit;
                break;
            case ERenderPassResourceType.SampledTexture:
            case ERenderPassResourceType.UniformBuffer:
                flags |= AccessFlags.ShaderReadBit;
                if (type == ERenderPassResourceType.UniformBuffer)
                    flags |= AccessFlags.UniformReadBit;
                break;
            case ERenderPassResourceType.StorageTexture:
            case ERenderPassResourceType.StorageBuffer:
                if (reads)
                    flags |= AccessFlags.ShaderReadBit;
                if (writes)
                    flags |= AccessFlags.ShaderWriteBit;
                break;
            case ERenderPassResourceType.VertexBuffer:
                flags |= AccessFlags.VertexAttributeReadBit;
                break;
            case ERenderPassResourceType.IndexBuffer:
                flags |= AccessFlags.IndexReadBit;
                break;
            case ERenderPassResourceType.IndirectBuffer:
                flags |= AccessFlags.IndirectCommandReadBit;
                break;
            case ERenderPassResourceType.TransferSource:
                flags |= AccessFlags.TransferReadBit;
                break;
            case ERenderPassResourceType.TransferDestination:
                flags |= AccessFlags.TransferWriteBit;
                break;
            default:
                if (reads)
                    flags |= AccessFlags.MemoryReadBit;
                if (writes)
                    flags |= AccessFlags.MemoryWriteBit;
                break;
        }

        return flags == AccessFlags.None ? AccessFlags.MemoryReadBit : flags;
    }

    private static PipelineStageFlags ResolveStageFromSync(
        RenderGraphStageMask stageMask,
        ERenderPassResourceType resourceType,
        ERenderGraphPassStage fallbackStage)
    {
        if (stageMask == RenderGraphStageMask.None)
            return ResolveStage(resourceType, fallbackStage);

        PipelineStageFlags flags = 0;
        if (stageMask.HasFlag(RenderGraphStageMask.TopOfPipe))
            flags |= PipelineStageFlags.TopOfPipeBit;
        if (stageMask.HasFlag(RenderGraphStageMask.VertexInput))
            flags |= PipelineStageFlags.VertexInputBit;
        if (stageMask.HasFlag(RenderGraphStageMask.VertexShader))
            flags |= PipelineStageFlags.VertexShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.FragmentShader))
            flags |= PipelineStageFlags.FragmentShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.EarlyFragmentTests))
            flags |= PipelineStageFlags.EarlyFragmentTestsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.LateFragmentTests))
            flags |= PipelineStageFlags.LateFragmentTestsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.ColorAttachmentOutput))
            flags |= PipelineStageFlags.ColorAttachmentOutputBit;
        if (stageMask.HasFlag(RenderGraphStageMask.ComputeShader))
            flags |= PipelineStageFlags.ComputeShaderBit;
        if (stageMask.HasFlag(RenderGraphStageMask.Transfer))
            flags |= PipelineStageFlags.TransferBit;
        if (stageMask.HasFlag(RenderGraphStageMask.DrawIndirect))
            flags |= PipelineStageFlags.DrawIndirectBit;
        if (stageMask.HasFlag(RenderGraphStageMask.Host))
            flags |= PipelineStageFlags.HostBit;
        if (stageMask.HasFlag(RenderGraphStageMask.AllGraphics))
            flags |= PipelineStageFlags.AllGraphicsBit;
        if (stageMask.HasFlag(RenderGraphStageMask.AllCommands))
            flags |= PipelineStageFlags.AllCommandsBit;

        return flags == 0
            ? ResolveStage(resourceType, fallbackStage)
            : flags;
    }

    private static AccessFlags ResolveAccessFromSync(RenderGraphAccessMask accessMask, ERenderPassResourceType resourceType)
    {
        if (accessMask == RenderGraphAccessMask.None)
            return ResolveAccess(resourceType, ERenderGraphAccess.ReadWrite);

        AccessFlags flags = AccessFlags.None;
        if (accessMask.HasFlag(RenderGraphAccessMask.MemoryRead))
            flags |= AccessFlags.MemoryReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.MemoryWrite))
            flags |= AccessFlags.MemoryWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ShaderRead))
            flags |= AccessFlags.ShaderReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ShaderWrite))
            flags |= AccessFlags.ShaderWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.UniformRead))
            flags |= AccessFlags.UniformReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ColorAttachmentRead))
            flags |= AccessFlags.ColorAttachmentReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.ColorAttachmentWrite))
            flags |= AccessFlags.ColorAttachmentWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.DepthStencilRead))
            flags |= AccessFlags.DepthStencilAttachmentReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.DepthStencilWrite))
            flags |= AccessFlags.DepthStencilAttachmentWriteBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.VertexAttributeRead))
            flags |= AccessFlags.VertexAttributeReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.IndexRead))
            flags |= AccessFlags.IndexReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.IndirectCommandRead))
            flags |= AccessFlags.IndirectCommandReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.TransferRead))
            flags |= AccessFlags.TransferReadBit;
        if (accessMask.HasFlag(RenderGraphAccessMask.TransferWrite))
            flags |= AccessFlags.TransferWriteBit;

        return flags == AccessFlags.None
            ? ResolveAccess(resourceType, ERenderGraphAccess.ReadWrite)
            : flags;
    }

    private static ImageLayout ResolveLayoutFromSync(
        RenderGraphImageLayout? layout,
        ERenderPassResourceType resourceType,
        VulkanPhysicalImageGroup? group)
    {
        if (!layout.HasValue)
            return ResolveLayout(resourceType, group);

        return layout.Value switch
        {
            RenderGraphImageLayout.Undefined => ImageLayout.Undefined,
            RenderGraphImageLayout.ColorAttachment => ImageLayout.ColorAttachmentOptimal,
            RenderGraphImageLayout.DepthStencilAttachment => ImageLayout.DepthStencilAttachmentOptimal,
            RenderGraphImageLayout.RenderingLocalRead => ImageLayout.RenderingLocalRead,
            RenderGraphImageLayout.ShaderReadOnly => ResolveSampledTextureLayout(group),
            RenderGraphImageLayout.General => ImageLayout.General,
            RenderGraphImageLayout.TransferSource => ImageLayout.TransferSrcOptimal,
            RenderGraphImageLayout.TransferDestination => ImageLayout.TransferDstOptimal,
            RenderGraphImageLayout.Present => ImageLayout.PresentSrcKhr,
            _ => ResolveLayout(resourceType, group)
        };
    }

    private static ImageAspectFlags ResolveAspect(VulkanPhysicalImageGroup group, ERenderPassResourceType type)
    {
        if (IsDepthFormat(group.Format) || type is ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment)
        {
            bool hasStencil = FormatHasStencil(group.Format) || type == ERenderPassResourceType.StencilAttachment;
            return hasStencil ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
        }

        return ImageAspectFlags.ColorBit;
    }

    private static ImageLayout ResolveSampledTextureLayout(VulkanPhysicalImageGroup? group)
    {
        if (IsDepthOrStencilGroup(group))
            return ImageLayout.DepthStencilReadOnlyOptimal;

        ImageUsageFlags usage = group?.Usage ?? ImageUsageFlags.None;
        bool sampled = (usage & ImageUsageFlags.SampledBit) != 0;
        bool storage = (usage & ImageUsageFlags.StorageBit) != 0;
        return sampled && storage
            ? ImageLayout.General
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    private static bool IsDepthFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.X8D24UnormPack32
            or Format.D16UnormS8Uint;

    private static bool IsDepthOrStencilGroup(VulkanPhysicalImageGroup? group)
        => group is not null && IsDepthFormat(group.Format);

    private static bool FormatHasStencil(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
}
