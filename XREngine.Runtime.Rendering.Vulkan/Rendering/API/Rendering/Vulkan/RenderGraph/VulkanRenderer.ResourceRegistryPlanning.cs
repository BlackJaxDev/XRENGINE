using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private RenderResourceRegistry? BuildMergedFrameOpRegistry(
        FrameOp[] ops,
        in FrameOpContext primaryContext,
        ulong frameOpsSignature = 0)
    {
        RenderResourceRegistry? primaryRegistry = primaryContext.ResourceRegistry;
        VulkanFrameOpPlannerStateKey ownerKey = BuildFrameOpPlannerStateKey(primaryContext);
        if (frameOpsSignature != 0)
        {
            for (int cacheIndex = 0; cacheIndex < _mergedFrameOpRegistryCache.Count; cacheIndex++)
            {
                MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[cacheIndex];
                if (entry.FrameOpsSignature != frameOpsSignature || !entry.OwnerKey.Equals(ownerKey))
                    continue;

                entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                return entry.MergedRegistry;
            }
        }

        List<RenderResourceRegistry> registries = CollectUniqueFrameOpRegistries(ops);
        int frameBufferDescriptorSignature = ComputeFrameOpFrameBufferDescriptorSignature(ops);
        bool hasFrameBufferDescriptors = frameBufferDescriptorSignature != 0;

        // Shadow command collections are conditional, but their logical resources are structural
        // for the owning pipeline generation. Once a source registry participates in this owner's
        // plan, retain its descriptors until the compatibility key changes. Owner scoping prevents
        // a desktop shadow/source registry from mutating an eye, mirror, or capture plan.
        List<FrameOpRegistryCacheSource> cacheSources = BuildFrameOpRegistryCacheSources(registries);
        if (TryGetCachedMergedFrameOpRegistry(ownerKey, primaryRegistry, cacheSources, frameBufferDescriptorSignature, ops, out RenderResourceRegistry? cachedRegistry))
            return cachedRegistry;

        if (registries.Count == 0 && !hasFrameBufferDescriptors)
        {
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                primaryRegistry);
            return primaryRegistry;
        }

        if (registries.Count == 1 && !hasFrameBufferDescriptors)
        {
            RenderResourceRegistry resolvedRegistry = registries[0];
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                resolvedRegistry);
            return resolvedRegistry;
        }

        if (!hasFrameBufferDescriptors && primaryRegistry is not null && RegistriesCoveredByPrimary(registries, primaryRegistry))
        {
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                primaryRegistry);
            return primaryRegistry;
        }

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        for (int i = 0; i < registries.Count; i++)
        {
            RenderResourceRegistry registry = registries[i];
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        AddFrameOpFrameBufferDescriptors(merged, ops);

        RememberMergedFrameOpRegistry(
            ownerKey,
            primaryRegistry,
            cacheSources,
            frameBufferDescriptorSignature,
            frameOpsSignature,
            merged);
        return merged;
    }

    private void RememberResolvedFrameOpRegistry(
        in VulkanFrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> cacheSources,
        int frameBufferDescriptorSignature,
        ulong frameOpsSignature,
        RenderResourceRegistry? resolvedRegistry)
    {
        if (frameOpsSignature == 0 || resolvedRegistry is null)
            return;

        RememberMergedFrameOpRegistry(
            ownerKey,
            primaryRegistry,
            cacheSources,
            frameBufferDescriptorSignature,
            frameOpsSignature,
            resolvedRegistry);
    }

    private List<RenderResourceRegistry> CollectUniqueFrameOpRegistries(FrameOp[] ops)
    {
        List<RenderResourceRegistry> registries = _frameOpRegistryScratch;
        registries.Clear();
        registries.EnsureCapacity(Math.Min(ops.Length, MaxFrameOpResourcePlannerSwitchingStates));
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp op = ops[opIndex];
            if (op.Context.ResourceRegistry is not { } registry)
                continue;

            bool exists = false;
            for (int i = 0; i < registries.Count; i++)
            {
                if (ReferenceEquals(registries[i], registry))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                registries.Add(registry);
        }

        // List<T>.Sort(Comparison<T>) materializes a comparer wrapper on every call.
        // Registry lists are normally tiny, so insertion sort is both allocation-free
        // and cheaper than constructing sorting infrastructure in this per-frame path.
        for (int i = 1; i < registries.Count; i++)
        {
            RenderResourceRegistry value = registries[i];
            int valueIdentity = RuntimeHelpers.GetHashCode(value);
            int insertIndex = i;
            while (insertIndex > 0 &&
                   RuntimeHelpers.GetHashCode(registries[insertIndex - 1]) > valueIdentity)
            {
                registries[insertIndex] = registries[insertIndex - 1];
                insertIndex--;
            }

            registries[insertIndex] = value;
        }
        return registries;
    }

    private List<FrameOpRegistryCacheSource> BuildFrameOpRegistryCacheSources(
        List<RenderResourceRegistry> registries)
    {
        List<FrameOpRegistryCacheSource> sources = _frameOpRegistryCacheSourceScratch;
        sources.Clear();
        sources.EnsureCapacity(registries.Count);
        for (int i = 0; i < registries.Count; i++)
        {
            RenderResourceRegistry registry = registries[i];
            sources.Add(new FrameOpRegistryCacheSource(
                registry,
                ComputeResourceRegistrySignature(registry)));
        }

        return sources;
    }

    private bool TryGetCachedMergedFrameOpRegistry(
        in VulkanFrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> sources,
        int frameBufferDescriptorSignature,
        FrameOp[] ops,
        out RenderResourceRegistry? mergedRegistry)
    {
        for (int i = 0; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[i];
            if (!entry.OwnerKey.Equals(ownerKey))
                continue;

            bool descriptorsChanged = false;
            FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;
            List<FrameOpRegistryCacheSource>? updatedSources = null;
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                FrameOpRegistryCacheSource current = sources[sourceIndex];
                int accumulatedIndex = IndexOfFrameOpRegistryCacheSource(accumulatedSources, current);
                if (accumulatedIndex >= 0 &&
                    accumulatedSources[accumulatedIndex].DescriptorSignature == current.DescriptorSignature)
                {
                    continue;
                }

                updatedSources ??= [.. accumulatedSources];
                if (accumulatedIndex >= 0)
                    updatedSources[accumulatedIndex] = current;
                else
                    updatedSources.Add(current);
                descriptorsChanged = true;
            }

            if (updatedSources is not null)
            {
                accumulatedSources = [.. updatedSources];
                entry.Sources = accumulatedSources;
            }

            int primaryDescriptorSignature = primaryRegistry?.DescriptorSignature ?? 0;
            if (entry.PrimaryDescriptorSignature != primaryDescriptorSignature)
            {
                entry.PrimaryDescriptorSignature = primaryDescriptorSignature;
                descriptorsChanged = true;
            }

            if (descriptorsChanged)
            {
                for (int sourceIndex = 0; sourceIndex < accumulatedSources.Length; sourceIndex++)
                {
                    RenderResourceRegistry source = accumulatedSources[sourceIndex].Registry;
                    AddRegistryDescriptors(entry.MergedRegistry, source, overwrite: true);
                }

                // The current primary wins any same-name descriptor conflict while descriptors
                // from conditional sources remain resident until the owner key changes.
                if (primaryRegistry is not null)
                    AddRegistryDescriptors(entry.MergedRegistry, primaryRegistry, overwrite: true);
            }

            if (entry.FrameBufferDescriptorSignature != frameBufferDescriptorSignature)
            {
                AddFrameOpFrameBufferDescriptors(entry.MergedRegistry, ops, overwrite: true);
                entry.FrameBufferDescriptorSignature = frameBufferDescriptorSignature;
            }

            entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            mergedRegistry = entry.MergedRegistry;
            return true;
        }

        mergedRegistry = null;
        return false;
    }

    private static int IndexOfFrameOpRegistryCacheSource(
        FrameOpRegistryCacheSource[] sources,
        in FrameOpRegistryCacheSource current)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (ReferenceEquals(sources[i].Registry, current.Registry))
                return i;
        }

        // Frame commands may produce short-lived registry wrappers for the same
        // immutable descriptor set. Treat those as one structural cache source;
        // retaining every wrapper would grow the source array and allocate on
        // every otherwise-stable frame.
        for (int i = 0; i < sources.Length; i++)
        {
            FrameOpRegistryCacheSource existing = sources[i];
            if (existing.DescriptorSignature == current.DescriptorSignature &&
                RegistryDescriptorsEquivalent(existing.Registry, current.Registry))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool RegistryDescriptorsEquivalent(
        RenderResourceRegistry left,
        RenderResourceRegistry right)
        => left.TextureRecords.Count == right.TextureRecords.Count &&
            left.FrameBufferRecords.Count == right.FrameBufferRecords.Count &&
            left.BufferRecords.Count == right.BufferRecords.Count &&
            TextureDescriptorsCoveredByPrimary(left, right) &&
            FrameBufferDescriptorsCoveredByPrimary(left, right) &&
            BufferDescriptorsCoveredByPrimary(left, right);

    private void RememberMergedFrameOpRegistry(
        in VulkanFrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> sources,
        int frameBufferDescriptorSignature,
        ulong frameOpsSignature,
        RenderResourceRegistry mergedRegistry)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        _mergedFrameOpRegistryCache.Add(new MergedFrameOpRegistryCacheEntry(
            ownerKey,
            primaryRegistry,
            sources.ToArray(),
            frameBufferDescriptorSignature,
            frameOpsSignature,
            mergedRegistry,
            frameId));

        if (_mergedFrameOpRegistryCache.Count <= MaxMergedFrameOpRegistryCacheEntries)
            return;

        int oldestIndex = 0;
        ulong oldestFrameId = _mergedFrameOpRegistryCache[0].LastUsedFrameId;
        for (int i = 1; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            ulong candidateFrameId = _mergedFrameOpRegistryCache[i].LastUsedFrameId;
            if (candidateFrameId < oldestFrameId)
            {
                oldestIndex = i;
                oldestFrameId = candidateFrameId;
            }
        }

        _mergedFrameOpRegistryCache.RemoveAt(oldestIndex);
    }

    private static bool RegistriesCoveredByPrimary(
        IEnumerable<RenderResourceRegistry> registries,
        RenderResourceRegistry primaryRegistry)
    {
        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            if (!TextureDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !FrameBufferDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !BufferDescriptorsCoveredByPrimary(registry, primaryRegistry))
                return false;
        }

        return true;
    }

    private static bool TextureDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
            if (!primary.TextureRecords.TryGetValue(pair.Key, out RenderTextureResource? primaryRecord) ||
                !EqualityComparer<TextureResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;

        return true;
    }

    private static bool FrameBufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
            if (!primary.FrameBufferRecords.TryGetValue(pair.Key, out RenderFrameBufferResource? primaryRecord) ||
                !FrameBufferDescriptorsEquivalent(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;
        
        return true;
    }

    private static bool BufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
            if (!primary.BufferRecords.TryGetValue(pair.Key, out RenderBufferResource? primaryRecord) ||
                !EqualityComparer<BufferResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;
        
        return true;
    }

    private static bool FrameBufferDescriptorsEquivalent(
        FrameBufferResourceDescriptor left,
        FrameBufferResourceDescriptor right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
            left.Lifetime != right.Lifetime ||
            left.SizePolicy != right.SizePolicy ||
            left.Attachments.Count != right.Attachments.Count)
            return false;

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
                return false;
        }

        return true;
    }

    internal static void AddRegistryDescriptors(
        RenderResourceRegistry destination,
        RenderResourceRegistry source,
        bool overwrite)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
            if (overwrite || !destination.TextureRecords.ContainsKey(pair.Key))
                destination.RegisterTextureDescriptor(pair.Value.Descriptor);

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
            if (overwrite || !destination.FrameBufferRecords.ContainsKey(pair.Key))
                destination.RegisterFrameBufferDescriptor(pair.Value.Descriptor);

        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
            if (overwrite || !destination.BufferRecords.ContainsKey(pair.Key))
                destination.RegisterBufferDescriptor(pair.Value.Descriptor);
    }

    private void AddFrameOpFrameBufferDescriptors(
        RenderResourceRegistry destination,
        FrameOp[] ops,
        bool overwrite = false)
    {
        List<XRFrameBuffer> frameBuffers = CollectUniqueFrameOpFrameBuffers(ops);
        for (int frameBufferIndex = 0; frameBufferIndex < frameBuffers.Count; frameBufferIndex++)
        {
            XRFrameBuffer frameBuffer = frameBuffers[frameBufferIndex];
            if (string.IsNullOrWhiteSpace(frameBuffer.Name))
                continue;

            if (frameBuffer.Targets is not null)
            {
                foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
                {
                    if (target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                        continue;

                    if (overwrite || !destination.TextureRecords.ContainsKey(texture.Name))
                    {
                        TextureResourceDescriptor textureDescriptor = RenderResourceDescriptorFactory.FromTexture(texture, RenderResourceLifetime.External);
                        destination.RegisterTextureDescriptor(EnrichTextureDescriptorForFrameBufferAttachment(textureDescriptor, texture, attachment, mipLevel, layerIndex));
                    }

                    if (texture is XRTextureViewBase view)
                    {
                        XRTexture viewedTexture = view.GetViewedTexture();
                        if (!string.IsNullOrWhiteSpace(viewedTexture.Name) &&
                            (overwrite || !destination.TextureRecords.ContainsKey(viewedTexture.Name)))
                        {
                            int sourceMipLevel = mipLevel >= 0 ? SaturatingAddToInt32(view.MinLevel, (uint)mipLevel) : mipLevel;
                            int sourceLayerIndex = layerIndex >= 0 ? SaturatingAddToInt32(view.MinLayer, (uint)layerIndex) : layerIndex;
                            TextureResourceDescriptor viewedDescriptor = RenderResourceDescriptorFactory.FromTexture(viewedTexture, RenderResourceLifetime.External);
                            destination.RegisterTextureDescriptor(EnrichTextureDescriptorForFrameBufferAttachment(viewedDescriptor, viewedTexture, attachment, sourceMipLevel, sourceLayerIndex));
                        }
                    }
                }
            }

            if (overwrite || !destination.FrameBufferRecords.ContainsKey(frameBuffer.Name))
                destination.RegisterFrameBufferDescriptor(RenderResourceDescriptorFactory.FromFrameBuffer(frameBuffer, RenderResourceLifetime.External));
        }
    }

    private int ComputeFrameOpFrameBufferDescriptorSignature(FrameOp[] ops)
    {
        HashCode hash = new();
        List<XRFrameBuffer> frameBuffers = CollectUniqueFrameOpFrameBuffers(ops);
        int namedFrameBufferCount = 0;
        for (int frameBufferIndex = 0; frameBufferIndex < frameBuffers.Count; frameBufferIndex++)
        {
            XRFrameBuffer frameBuffer = frameBuffers[frameBufferIndex];
            if (string.IsNullOrWhiteSpace(frameBuffer.Name))
                continue;

            hash.Add(frameBuffer.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add(RenderResourceSizePolicy.Absolute(
                Math.Max(frameBuffer.Width, 1u),
                Math.Max(frameBuffer.Height, 1u)));
            if (frameBuffer.Targets is not null)
            {
                foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
                {
                    string resourceName = target switch
                    {
                        XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                        _ => target?.GetType().Name ?? string.Empty
                    };
                    hash.Add(resourceName, StringComparer.OrdinalIgnoreCase);
                    hash.Add(attachment);
                    hash.Add(mipLevel);
                    hash.Add(layerIndex);
                }
            }
            namedFrameBufferCount++;
        }

        return namedFrameBufferCount == 0 ? 0 : hash.ToHashCode();
    }

    private List<XRFrameBuffer> CollectUniqueFrameOpFrameBuffers(FrameOp[] ops)
    {
        List<XRFrameBuffer> frameBuffers = _frameOpFrameBufferScratch;
        frameBuffers.Clear();
        frameBuffers.EnsureCapacity(Math.Min(ops.Length * 4, 256));
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp op = ops[opIndex];
            AddUniqueFrameBuffer(frameBuffers, op.Context.OutputFrameBuffer);
            AddUniqueFrameBuffer(frameBuffers, op.Target);
            if (op is not BlitOp blit)
                continue;

            AddUniqueFrameBuffer(frameBuffers, blit.InFbo);
            AddUniqueFrameBuffer(frameBuffers, blit.OutFbo);
        }

        return frameBuffers;
    }

    private static void AddUniqueFrameBuffer(List<XRFrameBuffer> frameBuffers, XRFrameBuffer? candidate)
    {
        if (candidate is null)
            return;

        for (int i = 0; i < frameBuffers.Count; i++)
            if (ReferenceEquals(frameBuffers[i], candidate))
                return;

        frameBuffers.Add(candidate);
    }

    internal static bool RequiresResourcePlannerRebuild(in FrameOpContext previous, in FrameOpContext next)
    {
        if (!ReferenceEquals(previous.PipelineInstance, next.PipelineInstance))
            return true;

        if (!ReferenceEquals(previous.ResourceRegistry, next.ResourceRegistry))
            return true;

        if (!ReferenceEquals(previous.PassMetadata, next.PassMetadata))
            return true;

        return !string.Equals(
            previous.OutputFrameBufferName,
            next.OutputFrameBufferName,
            StringComparison.OrdinalIgnoreCase);
    }


}
