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
    private void RebuildRenderGraphAndBarriers(
        in ResourcePlanningInputs planningInputs,
        ulong resourcePlannerSignature,
        ulong resourceAllocationSignature)
    {
        ActiveCompiledRenderGraph = planningInputs.CompiledGraph;

        BarrierPlanFastPathKey barrierKey = new(
            planningInputs.CompiledGraph,
            resourcePlannerSignature,
            resourceAllocationSignature,
            planningInputs.QueueOwnership);
        if (ActiveHasBarrierPlanFastPathKey && barrierKey.Matches(ActiveBarrierPlanFastPathKey))
            return;

        ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
        plannerState.BarrierPlanner.Rebuild(
            planningInputs.ActivePassMetadata,
            plannerState.ResourcePlanner,
            plannerState.ResourceAllocator,
            plannerState.CompiledRenderGraph.Synchronization,
            planningInputs.QueueOwnership);
        ActiveBarrierPlanFastPathKey = barrierKey;
        ActiveHasBarrierPlanFastPathKey = true;
    }

    private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        RenderResourceRegistry? resourceRegistry,
        int resourceRegistryRevision,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return passMetadata;

        if (activePassIndices is null)
        {
            if (constrainToActivePassSet)
                return Array.Empty<RenderPassMetadata>();

            if (resourceRegistry is null)
                return passMetadata;
        }

        if (activePassIndices is { Count: 0 })
            return Array.Empty<RenderPassMetadata>();

        if (ReferenceEquals(passMetadata, _lastActiveFilterSourcePassMetadata) &&
            ReferenceEquals(resourceRegistry, _lastActiveFilterResourceRegistry) &&
            resourceRegistryRevision == _lastActiveFilterResourceRegistryRevision &&
            activePassSetSignature == _lastActiveFilterPassSetSignature &&
            activeResourceSetSignature == _lastActiveFilterResourceSetSignature &&
            constrainToActivePassSet == _lastActiveFilterConstrainToActivePassSet)
        {
            return _lastActiveFilterResult;
        }

        for (int cacheIndex = 0; cacheIndex < _activePassMetadataFilterCache.Count; cacheIndex++)
        {
            ActivePassMetadataFilterCacheEntry entry = _activePassMetadataFilterCache[cacheIndex];
            if (!entry.Matches(
                passMetadata,
                resourceRegistry,
                resourceRegistryRevision,
                activePassSetSignature,
                activeResourceSetSignature,
                constrainToActivePassSet))
            {
                continue;
            }

            RememberLastActivePassMetadataFilter(
                passMetadata,
                resourceRegistry,
                resourceRegistryRevision,
                activePassSetSignature,
                activeResourceSetSignature,
                constrainToActivePassSet,
                entry.Result);
            return entry.Result;
        }

        int filteredCapacity = activePassIndices is null
            ? passMetadata.Count
            : Math.Min(passMetadata.Count, activePassIndices.Count);
        List<RenderPassMetadata> filtered = new(filteredCapacity);
        bool removedResourceUsages = false;
        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is not null && !activePassIndices.Contains(pass.PassIndex))
                continue;

            RenderPassMetadata activePass = FilterActivePassResourceUsages(
                pass,
                activePassIndices,
                activeFrameBufferNames,
                resourceRegistry,
                ref removedResourceUsages);
            filtered.Add(activePass);
        }

        IReadOnlyCollection<RenderPassMetadata> result;
        if (filtered.Count == passMetadata.Count && !removedResourceUsages)
        {
            result = passMetadata;
        }
        else if (filtered.Count == 0)
        {
            result = [];
        }
        else
        {
            filtered.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            result = [.. filtered];
        }

        var cacheEntry = new ActivePassMetadataFilterCacheEntry(
            passMetadata,
            resourceRegistry,
            resourceRegistryRevision,
            activePassSetSignature,
            activeResourceSetSignature,
            constrainToActivePassSet,
            result);
        if (_activePassMetadataFilterCache.Count < MaxActivePassMetadataFilterCacheEntries)
        {
            _activePassMetadataFilterCache.Add(cacheEntry);
        }
        else
        {
            _activePassMetadataFilterCache[_activePassMetadataFilterCacheReplacementIndex] = cacheEntry;
            _activePassMetadataFilterCacheReplacementIndex =
                (_activePassMetadataFilterCacheReplacementIndex + 1) % MaxActivePassMetadataFilterCacheEntries;
        }

        RememberLastActivePassMetadataFilter(
            passMetadata,
            resourceRegistry,
            resourceRegistryRevision,
            activePassSetSignature,
            activeResourceSetSignature,
            constrainToActivePassSet,
            result);
        return result;
    }

    private void RememberLastActivePassMetadataFilter(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        RenderResourceRegistry? resourceRegistry,
        int resourceRegistryRevision,
        int activePassSetSignature,
        int activeResourceSetSignature,
        bool constrainToActivePassSet,
        IReadOnlyCollection<RenderPassMetadata> result)
    {
        _lastActiveFilterSourcePassMetadata = passMetadata;
        _lastActiveFilterResourceRegistry = resourceRegistry;
        _lastActiveFilterResourceRegistryRevision = resourceRegistryRevision;
        _lastActiveFilterPassSetSignature = activePassSetSignature;
        _lastActiveFilterResourceSetSignature = activeResourceSetSignature;
        _lastActiveFilterConstrainToActivePassSet = constrainToActivePassSet;
        _lastActiveFilterResult = result;
    }

    private static RenderPassMetadata FilterActivePassResourceUsages(
        RenderPassMetadata pass,
        HashSet<int>? activePassIndices,
        HashSet<string>? activeFrameBufferNames,
        RenderResourceRegistry? resourceRegistry,
        ref bool removedResourceUsages)
    {
        bool hasActiveFrameBufferSet = activeFrameBufferNames is { Count: > 0 };
        bool hasResourceRegistry = resourceRegistry is not null;
        if (!hasActiveFrameBufferSet && !hasResourceRegistry)
            return pass;

        List<RenderPassResourceUsage>? activeUsages = null;
        for (int i = 0; i < pass.ResourceUsages.Count; i++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[i];
            if ((hasActiveFrameBufferSet && IsInactiveFrameBufferUsage(usage, activeFrameBufferNames!)) ||
                (hasResourceRegistry && IsMissingDeclaredResourceUsage(usage, resourceRegistry!)))
            {
                removedResourceUsages = true;
                if (activeUsages is null)
                {
                    activeUsages = new List<RenderPassResourceUsage>(pass.ResourceUsages.Count);
                    for (int previous = 0; previous < i; previous++)
                        activeUsages.Add(pass.ResourceUsages[previous]);
                }
                continue;
            }

            activeUsages?.Add(usage);
        }

        if (activeUsages is null)
            return pass;

        RenderPassMetadata filtered = new(pass.PassIndex, pass.Name, pass.Stage, pass.DeclarationOrder);
        filtered.UpdatePipelineReadiness(pass.RequiresPipelineReady);
        filtered.UpdateSecondaryCachePolicy(pass.SecondaryCachePolicy);
        foreach (RenderPassResourceUsage usage in activeUsages)
            filtered.AddUsage(usage);

        foreach (int dependency in pass.ExplicitDependencies)
            if (activePassIndices is null || activePassIndices.Contains(dependency))
                filtered.AddDependency(dependency);

        foreach (string schema in pass.DescriptorSchemas)
            filtered.AddDescriptorSchema(schema);

        return filtered;
    }

    private static bool IsMissingDeclaredResourceUsage(
        RenderPassResourceUsage usage,
        RenderResourceRegistry resourceRegistry)
    {
        string resourceName = usage.ResourceName;
        if (string.IsNullOrWhiteSpace(resourceName) ||
            resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!VulkanResourceBindingKey.TryParse(resourceName, out VulkanResourceBindingKey binding))
            return false;

        if (binding.Kind == EVulkanResourceBindingKind.FrameBuffer)
        {
            return !IsVulkanExternalOutputName(binding.Name) &&
                !resourceRegistry.FrameBufferRecords.ContainsKey(binding.Name);
        }

        if (binding.Kind == EVulkanResourceBindingKind.Texture)
            return !resourceRegistry.TextureRecords.ContainsKey(binding.Name);

        if (binding.Kind == EVulkanResourceBindingKind.Buffer)
            return !resourceRegistry.BufferRecords.ContainsKey(binding.Name);

        return false;
    }

    private static bool IsInactiveFrameBufferUsage(
        RenderPassResourceUsage usage,
        HashSet<string> activeFrameBufferNames)
    {
        if (!VulkanResourceBindingKey.TryParse(usage.ResourceName, out VulkanResourceBindingKey binding)
            || binding.Kind != EVulkanResourceBindingKind.FrameBuffer)
            return false;

        return !IsVulkanExternalOutputName(binding.Name) &&
            !activeFrameBufferNames.Contains(binding.Name);
    }

    private static int ComputeActivePassSetSignature(HashSet<int>? activePassIndices)
    {
        if (activePassIndices is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activePassIndices.Count);
        long sum = 0;
        long squaredSum = 0;
        int xor = 0;
        foreach (int passIndex in activePassIndices)
        {
            sum += passIndex;
            squaredSum += (long)passIndex * passIndex;
            xor ^= HashCode.Combine(passIndex);
        }

        hash.Add(sum);
        hash.Add(squaredSum);
        hash.Add(xor);
        return hash.ToHashCode();
    }

    private static int ComputeActiveFrameBufferSetSignature(HashSet<string>? activeFrameBufferNames)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activeFrameBufferNames.Count);
        foreach (string frameBufferName in activeFrameBufferNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            hash.Add(frameBufferName, StringComparer.OrdinalIgnoreCase);

        return hash.ToHashCode();
    }

    private void LogDeferredResourcePlanReplacementRetirement(
        int imageCount,
        int bufferCount,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (IsDeviceLost)
            return;

        Debug.VulkanEvery(
            "Vulkan.ResourcePlanner.PlanReplacementDeferredRetirement",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Deferring replaced physical resource plan retirement through frame-slot/timeline completion. revision={0} oldPlan=0x{1:X16} newPlan=0x{2:X16} oldAllocation=0x{3:X16} newAllocation=0x{4:X16} images={5} buffers={6}",
            ActiveResourcePlannerRevision + 1,
            ActiveResourcePlannerSignature,
            plannerSignature,
            ActiveResourceAllocationSignature,
            allocationSignature,
            imageCount,
            bufferCount);
    }

    private static void ValidateVulkanResourcePlanMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner,
        HashSet<int>? activePassIndices = null)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is { Count: > 0 } && !activePassIndices.Contains(pass.PassIndex))
                continue;

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                string resourceName = usage.ResourceName;
                if (string.IsNullOrWhiteSpace(resourceName)
                    || IsVulkanExternalOutputResourceBinding(resourceName, planner))
                {
                    continue;
                }

                if (!VulkanResourceBindingKey.TryParse(resourceName, out VulkanResourceBindingKey binding))
                    continue;

                if (binding.Kind == EVulkanResourceBindingKind.FrameBuffer)
                {
                    ValidateVulkanFrameBufferBinding(pass, usage, binding, planner);
                    continue;
                }

                if (binding.Kind == EVulkanResourceBindingKind.Texture)
                {
                    if (!IsVulkanPlannerOptionalResource(binding.Name)
                        && !planner.TryGetTextureDescriptor(binding.Name, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingTexture.{pass.PassIndex}.{binding.Name}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared texture '{1}'.",
                            pass.Name,
                            binding.Name);
                    }
                    continue;
                }

                if (binding.Kind == EVulkanResourceBindingKind.Buffer)
                {
                    if (!IsVulkanPlannerOptionalResource(binding.Name)
                        && !planner.TryGetBufferDescriptor(binding.Name, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingBuffer.{pass.PassIndex}.{binding.Name}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared buffer '{1}'.",
                            pass.Name,
                            binding.Name);
                    }
                }
            }
        }
    }

    private static void ValidateVulkanFrameBufferBinding(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        VulkanResourceBindingKey binding,
        VulkanResourcePlanner planner)
    {
        if (IsVulkanExternalOutputName(binding.Name) || IsVulkanPlannerOptionalResource(binding.Name))
            return;

        if (!planner.TryGetFrameBufferDescriptor(binding.Name, out FrameBufferResourceDescriptor? descriptor)
            || descriptor is null)
        {
            Debug.VulkanWarningEvery(
                $"VulkanResourcePlanner.MissingFBO.{pass.PassIndex}.{binding.Name}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Pass '{0}' references missing declared framebuffer '{1}'.",
                pass.Name,
                binding.Name);
            return;
        }

        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (!MatchesVulkanFrameBufferSlot(attachment.Attachment, binding.Slot))
                continue;

            if (!planner.TryGetTextureDescriptor(attachment.ResourceName, out _))
            {
                if (IsVulkanPlannerOptionalResource(attachment.ResourceName))
                    return;

                Debug.VulkanWarningEvery(
                    $"VulkanResourcePlanner.MissingFBOAttachment.{pass.PassIndex}.{binding.Name}.{attachment.ResourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' references attachment '{2}' that is missing from declared textures.",
                    pass.Name,
                    binding.Name,
                    attachment.ResourceName);
            }
            return;
        }

        Debug.VulkanWarningEvery(
            $"VulkanResourcePlanner.MissingFBOSlot.{pass.PassIndex}.{binding.Name}.{binding.Slot}",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' has no attachment matching slot '{2}' for usage {3}.",
            pass.Name,
            binding.Name,
            binding.Slot,
            usage.ResourceType);
    }

    private static bool IsVulkanExternalOutputResourceBinding(string resourceName, VulkanResourcePlanner planner)
    {
        if (!VulkanResourceBindingKey.TryParse(resourceName, out VulkanResourceBindingKey binding))
            return false;

        if (binding.Kind == EVulkanResourceBindingKind.Output)
            return !planner.TryGetOutputFrameBufferDescriptor(out _);

        if (planner.TryGetOutputFrameBufferDescriptor(out _) &&
            binding.Kind == EVulkanResourceBindingKind.FrameBuffer)
        {
            if (IsVulkanExternalOutputName(binding.Name))
                return false;
        }

        return binding.Kind == EVulkanResourceBindingKind.FrameBuffer
            && IsVulkanExternalOutputName(binding.Name);
    }

    private static bool IsVulkanExternalOutputName(string resourceName)
        => resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase);

    private static bool IsVulkanPlannerOptionalResource(string resourceName)
        => VulkanPlannerOptionalResourceNames.Contains(resourceName);

    private static bool MatchesVulkanFrameBufferSlot(EFrameBufferAttachment attachment, string slot)
    {
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

    private static ulong ComputeResourceAllocationSignature(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
    {
        ResourceAllocationSignatureBreakdown breakdown = ComputeResourceAllocationSignatureBreakdown(
            context,
            planner,
            passMetadata,
            extentContext,
            supportsTransformFeedback);
        HashCode hash = new();
        hash.Add(breakdown.AllocationDescriptors);
        hash.Add(breakdown.DisplayWidth);
        hash.Add(breakdown.DisplayHeight);
        hash.Add(breakdown.InternalWidth);
        hash.Add(breakdown.InternalHeight);
        hash.Add(breakdown.PhysicalUsage);
        hash.Add(breakdown.SupportsTransformFeedback);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourceAllocationSignatureBreakdown ComputeResourceAllocationSignatureBreakdown(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
        => new(
            ComputePhysicalResourceDescriptorSignature(context.ResourceRegistry),
            extentContext.WindowWidth,
            extentContext.WindowHeight,
            extentContext.InternalWidth,
            extentContext.InternalHeight,
            VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, passMetadata),
            supportsTransformFeedback);

    private static ulong ComputeResourcePlannerSignature(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(ComputeResourcePlanCompatibilityFingerprint(context));
        hash.Add(compiledGraph.Plan.CompatibilityIdentity);
        hash.Add(ComputePassMetadataSignature(passMetadata));

        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        hash.Add(queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

        return unchecked((ulong)hash.ToHashCode());
    }

    private static ulong ComputeResourcePlanCompatibilityFingerprint(in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x56554C4B504C414EUL);
        hash.Add((int)context.ContextKind);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputFrameBufferIdentity);
        hash.Add(ResolveResourcePlanOutputTargetIdentity(context));
        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));
        hash.Add(ComputePassMetadataSignature(context.PassMetadata));
        hash.Add(context.ResourceGeneration);
        hash.Add(context.SubmissionQueueFamily);
        return hash.ToHash();
    }

    private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => new(
            context.ContextKind,
            context.ContextId,
            ComputeResourcePlanCompatibilityFingerprint(context),
            ComputeResourceRegistrySignature(context.ResourceRegistry),
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            ComputePassMetadataSignature(passMetadata),
            ComputeCompiledGraphBatchSignature(compiledGraph),
            ComputeCompiledGraphEdgeSignature(compiledGraph),
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.SubmissionQueueFamily,
            queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

    private static int ComputeCompiledGraphBatchSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        return hash.ToHashCode();
    }

    private static int ComputeCompiledGraphEdgeSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        return hash.ToHashCode();
    }

    private static int ComputeResourceRegistrySignature(RenderResourceRegistry? registry)
        => registry?.DescriptorSignature ?? 0;

    private static int ComputePhysicalResourceDescriptorSignature(RenderResourceRegistry? registry)
    {
        if (registry is null)
            return 0;

        HashCode hash = new();

        foreach (KeyValuePair<string, RenderTextureResource> pair in registry.TextureRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextureResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.FormatLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.ArrayLayers);
            hash.Add(descriptor.StereoCompatible);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.RequiresStorageUsage);
            hash.Add((int)descriptor.Kind);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.InternalFormat.HasValue ? (int)descriptor.InternalFormat.Value : -1);
            hash.Add(descriptor.PixelFormat.HasValue ? (int)descriptor.PixelFormat.Value : -1);
            hash.Add(descriptor.PixelType.HasValue ? (int)descriptor.PixelType.Value : -1);
            hash.Add(descriptor.SizedInternalFormat.HasValue ? (int)descriptor.SizedInternalFormat.Value : -1);
            hash.Add(descriptor.Samples);
            hash.Add(descriptor.MipPolicy.BaseMipLevel);
            hash.Add(descriptor.MipPolicy.MipLevelCount);
            hash.Add(descriptor.MipPolicy.AutoGenerateMipmaps);
            hash.Add(descriptor.MipPolicy.RequireImmutableStorage);
            hash.Add(descriptor.SourceTextureName, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.BaseMipLevel);
            hash.Add(descriptor.MipLevelCount);
            hash.Add(descriptor.BaseLayer);
            hash.Add(descriptor.LayerCount);
            hash.Add((int)descriptor.DepthStencilAspect);
            hash.Add(descriptor.ArrayTarget);
            hash.Add(descriptor.Multisample);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in registry.BufferRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            BufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add(descriptor.SizeInBytes);
            hash.Add((int)descriptor.Target);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.ElementStride);
            hash.Add(descriptor.ElementCount);
            hash.Add((int)descriptor.AccessPattern);
        }

        return hash.ToHashCode();
    }

    // Frame-op contexts are copied into every draw packet, but the pipeline pass metadata
    // behind them is immutable until a pass revision changes. Without this cache the planner
    // walks every pass, usage, dependency, and schema once per draw while constructing planner
    // keys; a clean command-chain replay can consequently spend more CPU hashing than drawing.
    private const int MaxPassMetadataSignatureCacheEntries = 128;
    private static int ComputePassMetadataSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        int revisionStamp = ComputePassMetadataRevisionStamp(passMetadata);
        if (!VulkanFramePlanner.PassMetadataSignatureCache.TryGetValue(
                passMetadata,
                out RenderPassMetadataSignatureCacheEntry? cacheEntry))
        {
            // Count acquires every ConcurrentDictionary partition lock. Keep that
            // maintenance work off the steady hit path, where this method runs once
            // for every captured draw context.
            if (VulkanFramePlanner.PassMetadataSignatureCache.Count >= MaxPassMetadataSignatureCacheEntries)
                VulkanFramePlanner.PassMetadataSignatureCache.Clear();

            cacheEntry = VulkanFramePlanner.PassMetadataSignatureCache.GetOrAdd(
                passMetadata,
                static _ => new RenderPassMetadataSignatureCacheEntry());
        }

        if (cacheEntry.RevisionStamp == revisionStamp)
            return cacheEntry.Signature;

        lock (cacheEntry)
        {
            if (cacheEntry.RevisionStamp == revisionStamp)
                return cacheEntry.Signature;

            int signature = ComputePassMetadataSignatureUncached(passMetadata);
            cacheEntry.Signature = signature;
            cacheEntry.RevisionStamp = revisionStamp;
            return signature;
        }
    }

    private static int ComputePassMetadataSignatureUncached(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        HashCode hash = new();
        hash.Add(passMetadata.Count);

        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int passIndex = 0; passIndex < passList.Count; passIndex++)
                AddPassMetadataToHash(ref hash, passList[passIndex]);
        }
        else
        {
            foreach (RenderPassMetadata pass in passMetadata)
                AddPassMetadataToHash(ref hash, pass);
        }

        return hash.ToHashCode();
    }

    private static void AddPassMetadataToHash(ref HashCode hash, RenderPassMetadata pass)
    {
        hash.Add(pass.PassIndex);
        hash.Add(pass.DeclarationOrder);
        hash.Add((int)pass.Stage);
        hash.Add(pass.Name, StringComparer.Ordinal);
        hash.Add(pass.Revision);

        for (int usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[usageIndex];
            hash.Add(usage.ResourceName, StringComparer.Ordinal);
            hash.Add((int)usage.ResourceType);
            hash.Add((int)usage.Access);
            hash.Add((int)usage.LoadOp);
            hash.Add((int)usage.StoreOp);
            AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
        }

        for (int dependencyIndex = 0; dependencyIndex < pass.ExplicitDependencies.Count; dependencyIndex++)
            hash.Add(pass.ExplicitDependencies[dependencyIndex]);

        for (int schemaIndex = 0; schemaIndex < pass.DescriptorSchemas.Count; schemaIndex++)
            hash.Add(pass.DescriptorSchemas[schemaIndex], StringComparer.Ordinal);
    }

    private static int ComputePassMetadataRevisionStamp(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        if (passMetadata is RenderPassMetadataSnapshot snapshot)
            return snapshot.RevisionStamp;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int passIndex = 0; passIndex < passList.Count; passIndex++)
            {
                RenderPassMetadata pass = passList[passIndex];
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add(pass.Revision);
            }
        }
        else
        {
            foreach (RenderPassMetadata pass in passMetadata)
            {
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add(pass.Revision);
            }
        }

        return hash.ToHashCode();
    }

    private int ComputeResourcePlanningSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        HashCode hash = new();
        hash.Add(fallbackExtent.Width);
        hash.Add(fallbackExtent.Height);

        foreach (VulkanAllocationRequest request in ResourcePlanner.CurrentPlan.AllTextures())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        foreach (VulkanBufferAllocationRequest request in ResourcePlanner.CurrentPlan.AllBuffers())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        if (passMetadata is not null)
        {
            hash.Add(passMetadata.Count);
            foreach (RenderPassMetadata pass in passMetadata.OrderBy(static p => p.PassIndex))
            {
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add((int)pass.Stage);
                hash.Add(pass.Name, StringComparer.Ordinal);

                foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
                {
                    hash.Add(usage.ResourceName, StringComparer.Ordinal);
                    hash.Add((int)usage.ResourceType);
                    hash.Add((int)usage.Access);
                    hash.Add((int)usage.LoadOp);
                    hash.Add((int)usage.StoreOp);
                    AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
                }
            }
        }

        return hash.ToHashCode();
    }

    private static void AddSubresourceRangeToHash(ref HashCode hash, RenderGraphSubresourceRange range)
    {
        hash.Add(range.BaseMipLevel);
        hash.Add(range.MipLevelCount);
        hash.Add(range.BaseArrayLayer);
        hash.Add(range.ArrayLayerCount);
    }
    internal int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        passMetadata ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;

        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.InvalidPass.{opName}.SwapchainPseudoPass",
                TimeSpan.FromSeconds(1),
                "[Vulkan] '{0}' attempted to use the reserved swapchain pseudo-pass as a render-graph pass. Treating it as unresolved.",
                opName);
            passIndex = int.MinValue;
        }

        // Short-circuit: well-known EDefaultRenderPass values are always valid.
        // Metadata may lag behind runtime enqueues (conditional pipeline paths,
        // hot-reload) — accept standard passes without warning.
        if (passIndex != int.MinValue &&
            Enum.IsDefined<EDefaultRenderPass>((EDefaultRenderPass)passIndex))
            return passIndex;

        bool hasMetadata = passMetadata is { Count: > 0 };
        bool passDefinedInMetadata = hasMetadata &&
            PassMetadataContainsPassIndex(passMetadata!, passIndex);

        if (passIndex != int.MinValue && (!hasMetadata || passDefinedInMetadata))
            return passIndex;

        int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
        {
            bool currentPassDefined = currentPassIndex != int.MinValue &&
                (!hasMetadata || PassMetadataContainsPassIndex(passMetadata!, currentPassIndex));

            if (currentPassDefined)
                return currentPassIndex;

            if (hasMetadata && !opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            {
                const int preRenderPass = (int)EDefaultRenderPass.PreRender;
                if (PassMetadataContainsPassIndex(passMetadata!, preRenderPass))
                    return preRenderPass;
            }
        }

        int fallback = ResolveFallbackPassIndex(opName, passMetadata);

        string reason = passIndex == int.MinValue
            ? "invalid sentinel value"
            : $"pass {passIndex} is missing from metadata";

        int? firstKnownBarrierPass = BarrierPlanner.GetFirstKnownPassIndex();

        Debug.VulkanWarningEvery(
            $"Vulkan.InvalidPass.{opName}.{passIndex}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] '{0}' emitted with invalid render-graph pass index ({1}). Falling back to pass {2}. " +
            "MetadataCount={3} BarrierPlannerFirstPass={4} CurrentPipeline={5}",
            opName,
            reason,
            fallback,
            passMetadata?.Count ?? -1,
            firstKnownBarrierPass?.ToString() ?? "none",
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetType().Name ?? "null");

        return fallback;
    }

    private static bool PassMetadataContainsPassIndex(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        int passIndex)
    {
        if (passMetadata is IReadOnlyList<RenderPassMetadata> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].PassIndex == passIndex)
                    return true;

            return false;
        }

        foreach (RenderPassMetadata metadata in passMetadata)
            if (metadata.PassIndex == passIndex)
                return true;

        return false;
    }

    private static int ResolveFallbackPassIndex(string opName, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return int.MinValue;

        ERenderGraphPassStage? preferredStage = ResolvePreferredFallbackStage(opName, passMetadata);
        if (preferredStage.HasValue)
        {
            RenderPassMetadata? preferredPass = passMetadata
                .Where(m => m.Stage == preferredStage.Value)
                .OrderBy(m => m.PassIndex)
                .FirstOrDefault();

            if (preferredPass is not null)
                return preferredPass.PassIndex;
        }

        return passMetadata.OrderBy(m => m.PassIndex).First().PassIndex;
    }

    private static ERenderGraphPassStage? ResolvePreferredFallbackStage(string opName, IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return ERenderGraphPassStage.Compute;

        if (opName.Contains("Blit", StringComparison.OrdinalIgnoreCase))
        {
            bool hasTransferPass = passMetadata.Any(m => m.Stage == ERenderGraphPassStage.Transfer);
            return hasTransferPass ? ERenderGraphPassStage.Transfer : ERenderGraphPassStage.Graphics;
        }

        return ERenderGraphPassStage.Graphics;
    }

}
