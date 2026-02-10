using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanBarrierPlanner
{
    internal const int SwapchainPassIndex = -1;
    private static readonly PlannedImageBarrier[] _emptyBarriers = [];

    private readonly List<PlannedImageBarrier> _imageBarriers = [];
    private readonly Dictionary<int, List<PlannedImageBarrier>> _perPassBarriers = [];
    private readonly Dictionary<string, PlannedImageState> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlannedImageBarrier> ImageBarriers => _imageBarriers;

    public IReadOnlyList<PlannedImageBarrier> GetBarriersForPass(int passIndex)
        => _perPassBarriers.TryGetValue(passIndex, out var list) ? list : _emptyBarriers;

    public void Rebuild(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner resourcePlanner,
        VulkanResourceAllocator resourceAllocator)
    {
        _imageBarriers.Clear();
        _perPassBarriers.Clear();
        _lastStates.Clear();

        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in SortPassesByDependencies(passMetadata))
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!ShouldTrack(usage.ResourceType))
                    continue;

                foreach (string logicalResource in ExpandLogicalResources(usage.ResourceName, resourcePlanner))
                {
                    if (!resourceAllocator.TryGetPhysicalGroupForResource(logicalResource, out VulkanPhysicalImageGroup group))
                        continue;

                    PlannedImageState desiredState = PlannedImageState.FromUsage(usage, group, pass.Stage);

                    PlannedImageBarrier? plannedBarrier = null;

                    if (_lastStates.TryGetValue(logicalResource, out PlannedImageState previousState))
                    {
                        if (!previousState.Equals(desiredState))
                            plannedBarrier = new PlannedImageBarrier(pass.PassIndex, logicalResource, group, previousState, desiredState);
                    }
                    else
                    {
                        plannedBarrier = new PlannedImageBarrier(
                            pass.PassIndex,
                            logicalResource,
                            group,
                            PlannedImageState.Initial(desiredState.AspectMask),
                            desiredState);
                    }

                    if (plannedBarrier.HasValue)
                        AddBarrier(plannedBarrier.Value);

                    _lastStates[logicalResource] = desiredState;
                }
            }
        }
    }

    private static IReadOnlyList<RenderPassMetadata> SortPassesByDependencies(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        Dictionary<int, RenderPassMetadata> lookup = passMetadata.ToDictionary(p => p.PassIndex);
        Dictionary<int, int> inDegree = lookup.Keys.ToDictionary(k => k, _ => 0);
        Dictionary<int, List<int>> edges = lookup.Keys.ToDictionary(k => k, _ => new List<int>());

        foreach (RenderPassMetadata pass in lookup.Values)
        {
            foreach (int dep in pass.ExplicitDependencies)
            {
                if (!lookup.ContainsKey(dep))
                    continue;

                edges[dep].Add(pass.PassIndex);
                inDegree[pass.PassIndex] = inDegree[pass.PassIndex] + 1;
            }
        }

        SortedSet<int> ready = new(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
        List<RenderPassMetadata> ordered = new(lookup.Count);

        while (ready.Count > 0)
        {
            int passIndex = ready.Min;
            ready.Remove(passIndex);
            ordered.Add(lookup[passIndex]);

            foreach (int consumer in edges[passIndex])
            {
                int next = inDegree[consumer] - 1;
                inDegree[consumer] = next;
                if (next == 0)
                    ready.Add(consumer);
            }
        }

        if (ordered.Count == lookup.Count)
            return ordered;

        // Dependency cycles or malformed metadata: retain deterministic order for remaining passes.
        HashSet<int> included = ordered.Select(p => p.PassIndex).ToHashSet();
        foreach (RenderPassMetadata pass in lookup.Values.OrderBy(p => p.PassIndex))
        {
            if (!included.Contains(pass.PassIndex))
                ordered.Add(pass);
        }

        return ordered;
    }

    private void AddBarrier(PlannedImageBarrier barrier)
    {
        _imageBarriers.Add(barrier);

        if (!_perPassBarriers.TryGetValue(barrier.PassIndex, out var list))
        {
            list = [];
            _perPassBarriers[barrier.PassIndex] = list;
        }

        list.Add(barrier);
    }

    private static bool ShouldTrack(RenderPassResourceType type)
        => type is RenderPassResourceType.ColorAttachment
            or RenderPassResourceType.DepthAttachment
            or RenderPassResourceType.StencilAttachment
            or RenderPassResourceType.ResolveAttachment
            or RenderPassResourceType.SampledTexture
            or RenderPassResourceType.StorageTexture
            or RenderPassResourceType.TransferSource
            or RenderPassResourceType.TransferDestination;

    private static IEnumerable<string> ExpandLogicalResources(string resourceBinding, VulkanResourcePlanner planner)
    {
        if (string.IsNullOrWhiteSpace(resourceBinding))
            yield break;

        if (resourceBinding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            yield break; // swapchain target handled separately

        if (resourceBinding.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = resourceBinding.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                yield break;

            string fboName = segments[1];
            string slot = segments.Length >= 3 ? segments[2] : "color";
            if (!planner.TryGetFrameBufferDescriptor(fboName, out FrameBufferResourceDescriptor descriptor))
                yield break;

            foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
            {
                if (MatchesSlot(attachment.Attachment, slot) && !string.IsNullOrWhiteSpace(attachment.ResourceName))
                    yield return attachment.ResourceName;
            }
            yield break;
        }

        if (resourceBinding.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
        {
            string textureName = resourceBinding["tex::".Length..];
            if (!string.IsNullOrWhiteSpace(textureName))
                yield return textureName;
            yield break;
        }

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

    internal readonly record struct PlannedImageBarrier(
        int PassIndex,
        string ResourceName,
        VulkanPhysicalImageGroup Group,
        PlannedImageState Previous,
        PlannedImageState Next);

    internal readonly struct PlannedImageState(ImageLayout layout, PipelineStageFlags stageMask, AccessFlags accessMask, ImageAspectFlags aspectMask) : IEquatable<PlannedImageState>
    {
        public ImageLayout Layout { get; } = layout;
        public PipelineStageFlags StageMask { get; } = stageMask;
        public AccessFlags AccessMask { get; } = accessMask;
        public ImageAspectFlags AspectMask { get; } = aspectMask;

        public static PlannedImageState Initial(ImageAspectFlags aspect)
            => new(ImageLayout.Undefined, PipelineStageFlags.TopOfPipeBit, AccessFlags.None, aspect);

        public static PlannedImageState FromUsage(RenderPassResourceUsage usage, VulkanPhysicalImageGroup group, RenderGraphPassStage passStage)
        {
            ImageAspectFlags aspect = ResolveAspect(group, usage.ResourceType);
            ImageLayout layout = ResolveLayout(usage.ResourceType);
            PipelineStageFlags stages = ResolveStage(usage.ResourceType, passStage);
            AccessFlags access = ResolveAccess(usage.ResourceType, usage.Access);
            return new(layout, stages, access, aspect);
        }

        public bool Equals(PlannedImageState other)
            => Layout == other.Layout && StageMask == other.StageMask && AccessMask == other.AccessMask && AspectMask == other.AspectMask;

        public override bool Equals(object? obj)
            => obj is PlannedImageState other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)Layout, (int)StageMask, (int)AccessMask, (int)AspectMask);
    }

    private static ImageLayout ResolveLayout(RenderPassResourceType type)
        => type switch
        {
            RenderPassResourceType.ColorAttachment or RenderPassResourceType.ResolveAttachment => ImageLayout.ColorAttachmentOptimal,
            RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment => ImageLayout.DepthStencilAttachmentOptimal,
            RenderPassResourceType.SampledTexture => ImageLayout.ShaderReadOnlyOptimal,
            RenderPassResourceType.StorageTexture => ImageLayout.General,
            RenderPassResourceType.TransferSource => ImageLayout.TransferSrcOptimal,
            RenderPassResourceType.TransferDestination => ImageLayout.TransferDstOptimal,
            _ => ImageLayout.General
        };

    private static PipelineStageFlags ResolveStage(RenderPassResourceType type, RenderGraphPassStage passStage)
    {
        return type switch
        {
            RenderPassResourceType.ColorAttachment or RenderPassResourceType.ResolveAttachment => PipelineStageFlags.ColorAttachmentOutputBit,
            RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            RenderPassResourceType.TransferSource or RenderPassResourceType.TransferDestination => PipelineStageFlags.TransferBit,
            RenderPassResourceType.SampledTexture => SampleStage(passStage),
            RenderPassResourceType.StorageTexture => StorageStage(passStage),
            _ => DefaultStage(passStage)
        };

        static PipelineStageFlags SampleStage(RenderGraphPassStage stage)
            => stage switch
            {
                RenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                RenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit
            };

        static PipelineStageFlags StorageStage(RenderGraphPassStage stage)
            => stage switch
            {
                RenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                RenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit
            };

        static PipelineStageFlags DefaultStage(RenderGraphPassStage stage)
            => stage switch
            {
                RenderGraphPassStage.Compute => PipelineStageFlags.ComputeShaderBit,
                RenderGraphPassStage.Transfer => PipelineStageFlags.TransferBit,
                _ => PipelineStageFlags.AllGraphicsBit
            };
    }

    private static AccessFlags ResolveAccess(RenderPassResourceType type, RenderGraphAccess accessIntent)
    {
        bool reads = accessIntent is RenderGraphAccess.Read or RenderGraphAccess.ReadWrite;
        bool writes = accessIntent is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite;

        AccessFlags flags = AccessFlags.None;

        switch (type)
        {
            case RenderPassResourceType.ColorAttachment:
            case RenderPassResourceType.ResolveAttachment:
                if (reads)
                    flags |= AccessFlags.ColorAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.ColorAttachmentWriteBit;
                break;
            case RenderPassResourceType.DepthAttachment:
            case RenderPassResourceType.StencilAttachment:
                if (reads)
                    flags |= AccessFlags.DepthStencilAttachmentReadBit;
                if (writes)
                    flags |= AccessFlags.DepthStencilAttachmentWriteBit;
                break;
            case RenderPassResourceType.SampledTexture:
                flags |= AccessFlags.ShaderReadBit;
                break;
            case RenderPassResourceType.StorageTexture:
                if (reads)
                    flags |= AccessFlags.ShaderReadBit;
                if (writes)
                    flags |= AccessFlags.ShaderWriteBit;
                break;
            case RenderPassResourceType.TransferSource:
                flags |= AccessFlags.TransferReadBit;
                break;
            case RenderPassResourceType.TransferDestination:
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

    private static ImageAspectFlags ResolveAspect(VulkanPhysicalImageGroup group, RenderPassResourceType type)
    {
        if (IsDepthFormat(group.Format) || type is RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment)
        {
            bool hasStencil = FormatHasStencil(group.Format) || type == RenderPassResourceType.StencilAttachment;
            return hasStencil ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
        }

        return ImageAspectFlags.ColorBit;
    }

    private static bool IsDepthFormat(Format format)
        => format is Format.D16Unorm
            or Format.D32Sfloat
            or Format.D24UnormS8Uint
            or Format.D32SfloatS8Uint
            or Format.X8D24UnormPack32
            or Format.D16UnormS8Uint;

    private static bool FormatHasStencil(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
}
