using System.Collections.Concurrent;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Concrete generation-owned services used by program wrappers. This boundary
/// deliberately carries command, resource, and frame-planner authorities rather
/// than a renderer-facade reference.
/// </summary>
internal unsafe sealed class VulkanProgramCreationPort(VulkanBackendObjectContext context)
{
    private readonly ThreadLocal<VkRenderProgram.BindingCaptureWorkspace> _bindingWorkspace =
        new(static () => new VkRenderProgram.BindingCaptureWorkspace(), trackAllValues: false);

    internal VkRenderProgram.BindingCaptureWorkspace BindingWorkspace
        => _bindingWorkspace.Value ?? throw new InvalidOperationException(
            "The Vulkan program binding workspace has been disposed.");

    /// <summary>
    /// Creates a shader dependency through the generation's cold wrapper
    /// boundary. Program linking can discover shaders before the renderer-level
    /// cache has observed them, so an identity-only retained lookup is
    /// intentionally insufficient at this construction boundary.
    /// </summary>
    internal VkShader? GetOrCreateShader(XRShader shader)
        => context.Resources.CreateAPIRenderObject(shader) as VkShader;

    /// <summary>Publishes a newly generated mesh program through the explicit cold factory.</summary>
    internal VkRenderProgram? GetOrCreateProgram(XRRenderProgram program)
    {
        if (context.Resources.CreateAPIRenderObject(program) is not VkRenderProgram wrapper)
            return null;
        if (!wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper;
    }

    /// <summary>Creates an assigned texture only for callers admitted to synchronous resource preparation.</summary>
    internal AbstractRenderAPIObject GetOrCreateTexture(XRTexture texture)
    {
        AbstractRenderAPIObject wrapper = context.Resources.CreateAPIRenderObject(texture);
        if (!wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper;
    }

    internal uint GetRenderPassColorAttachmentCount(RenderPass renderPass)
        => renderPass.Handle != 0 && context.Resources.RenderPassColorAttachmentCounts.TryGetValue(renderPass.Handle, out uint count)
            ? count
            : 1u;

    internal Format GetRenderPassColorAttachmentFormat(RenderPass renderPass, uint attachmentIndex)
        => renderPass.Handle != 0 &&
           context.Resources.RenderPassColorAttachmentFormats.TryGetValue(renderPass.Handle, out Format[]? formats) &&
           attachmentIndex < formats.Length
            ? formats[attachmentIndex]
            : Format.Undefined;

    internal string GetRenderPassSemanticSignature(RenderPass renderPass)
    {
        if (renderPass.Handle != 0 &&
            context.Resources.RenderPassSemanticSignatures.TryGetValue(
                renderPass.Handle,
                out string? signature) &&
            !string.IsNullOrWhiteSpace(signature))
        {
            return signature;
        }

        return $"RenderPass:Unregistered:ColorCount={GetRenderPassColorAttachmentCount(renderPass)}";
    }

    internal bool SupportsColorAttachmentBlend(Format format)
    {
        if (format == Format.Undefined || VkFormatConversions.IsDepthStencilFormat(format))
            return false;
        if (context.Resources.FormatColorBlendSupport.TryGetValue(format, out bool supported))
            return supported;

        context.Api.GetPhysicalDeviceFormatProperties(context.PhysicalDevice, format, out FormatProperties properties);
        supported = (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBlendBit) != 0;
        context.Resources.FormatColorBlendSupport[format] = supported;
        return supported;
    }

    internal void RegisterPipeline(Pipeline pipeline, string owner)
        => context.Resources.RegisterPipeline(pipeline, owner);

    internal void NotifyPipelineCreated(string kind)
        => context.Resources.PipelineManager.NotifyPipelineCreated(kind);

    /// <summary>
    /// Drains compilation and holds the mutation gate. Acquire this before any
    /// program interface lock so shader invalidation and linking use one order.
    /// </summary>
    internal VulkanPipelineCompilationMutationLease AcquirePipelineCompilationMutationLease(string reason)
        => context.Resources.PipelineManager.AcquireCompilationMutationLease(reason);

    internal void ExecuteWithPipelineCompilationQuiesced(Action mutation, string reason)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        using VulkanPipelineCompilationMutationLease lease =
            AcquirePipelineCompilationMutationLease(reason);
        mutation();
    }

    // Descriptor-set cache ownership is migrated with the command recording
    // authority; these calls remain here as the wrapper-facing seam.
    internal bool TryGetPreparedComputeDescriptorSets(
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        out DescriptorSet[] descriptorSets)
        => TryGetPreparedComputeDescriptorSetsCore(
            context.Resources.Descriptors, imageIndex, schemaKey, bindingKey, out descriptorSets);

    internal string DescribePreparedComputeDescriptorBindings(uint imageIndex, ulong schemaKey)
    {
        VulkanDescriptorManager descriptors = context.Resources.Descriptors;
        lock (descriptors.Compute.Gate)
        {
            ComputeDescriptorImageCache[]? caches = descriptors.Compute.Caches;
            if (caches is null || imageIndex >= caches.Length)
                return "cache-unavailable";

            StringBuilder builder = new();
            foreach (ComputeDescriptorCacheKey key in caches[imageIndex].CachedSets.Keys)
            {
                if (key.SchemaKey != schemaKey)
                    continue;
                if (builder.Length > 0)
                    builder.Append(',');
                builder.Append("0x");
                builder.Append(key.BindingKey.ToString("X16"));
            }

            return builder.Length == 0 ? "none-for-schema" : builder.ToString();
        }
    }

    internal bool TryGetOrCreateComputeDescriptorSets(
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] poolSizes,
        int poolSizeCount,
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets,
        out bool isNewAllocation)
        => TryGetOrCreateComputeDescriptorSetsCore(
            context, imageIndex, schemaKey, bindingKey, layouts, poolSizes, poolSizeCount,
            usesUpdateAfterBind, out descriptorSets, out isNewAllocation);

    /// <summary>
    /// Publishes a transform-feedback operation through the generation-local frame
    /// planner.  The producer captures only planner state; native transform-feedback
    /// command encoding remains owned by the command runtime when this operation is
    /// consumed by primary recording.
    /// </summary>
    /// <summary>
    /// Publishes the legacy framebuffer selection to command-owned mutable state.
    /// The framebuffer wrapper has already created its generation-local resources;
    /// recording later captures the effective target into immutable operation input.
    /// </summary>
    internal void TrackPipelineLayout(PipelineLayout layout, string owner)
        => context.Resources.TrackPipelineLayout(layout, owner);

    internal bool TryBeginDestroyPipelineLayout(PipelineLayout layout, string owner)
        => context.Resources.TryBeginDestroyPipelineLayout(layout, owner);

    internal void RetirePipeline(Pipeline pipeline)
        => context.Resources.RetirePipeline(pipeline, "VkRenderProgram");

    internal void DestroyPipelineImmediate(Pipeline pipeline)
    {
        if (pipeline.Handle == 0 || context.Device.Handle == 0)
            return;

        context.Api.DestroyPipeline(context.Device, pipeline, null);
        context.Resources.CompletePipelineDestruction(pipeline);
    }

    private static bool TryGetPreparedComputeDescriptorSetsCore(
        VulkanDescriptorManager descriptors,
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        if (bindingKey == 0UL)
            return false;

        lock (descriptors.Compute.Gate)
        {
            ComputeDescriptorImageCache[]? caches = descriptors.Compute.Caches;
            return caches is not null && imageIndex < caches.Length &&
                caches[imageIndex] is { } cache &&
                cache.CachedSets.TryGetValue(
                    new ComputeDescriptorCacheKey(schemaKey, bindingKey),
                    out descriptorSets!);
        }
    }

    private static bool TryGetOrCreateComputeDescriptorSetsCore(
        VulkanBackendObjectContext context,
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] poolSizes,
        int poolSizeCount,
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets,
        out bool isNewAllocation)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        isNewAllocation = false;
        if (layouts.Length == 0 || poolSizeCount == 0)
            return false;

        VulkanDescriptorManager descriptors = context.Resources.Descriptors;
        lock (descriptors.Compute.Gate)
        {
            ComputeDescriptorImageCache[]? caches = descriptors.Compute.Caches;
            int requiredCount = checked((int)imageIndex + 1);
            if (caches is null)
            {
                caches = new ComputeDescriptorImageCache[requiredCount];
                descriptors.Compute.Caches = caches;
            }
            else if (caches.Length < requiredCount)
            {
                Array.Resize(ref caches, requiredCount);
                descriptors.Compute.Caches = caches;
            }

            for (int index = 0; index < requiredCount; index++)
                caches[index] ??= new ComputeDescriptorImageCache();

            ComputeDescriptorImageCache cache = caches[imageIndex];

            ComputeDescriptorCacheKey key = new(schemaKey, bindingKey);
            if (cache.CachedSets.TryGetValue(key, out DescriptorSet[]? cached))
            {
                descriptorSets = cached;
                return true;
            }

            if (!TryAllocateComputeDescriptorSetBatch(
                    context, cache, schemaKey, layouts, poolSizes, poolSizeCount,
                    usesUpdateAfterBind, out descriptorSets, out DescriptorPool allocationPool))
            {
                return false;
            }

            cache.CachedSets.Add(key, descriptorSets);
            cache.CachedSetPools.Add(key, allocationPool);
            isNewAllocation = true;
            return true;
        }
    }

    private static bool TryAllocateComputeDescriptorSetBatch(
        VulkanBackendObjectContext context,
        ComputeDescriptorImageCache cache,
        ulong schemaKey,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] poolSizes,
        int poolSizeCount,
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets,
        out DescriptorPool allocationPool)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        allocationPool = default;
        if (!cache.PoolsBySchema.TryGetValue(schemaKey, out List<ComputeDescriptorPoolBlock>? blocks))
            cache.PoolsBySchema.Add(schemaKey, blocks = []);

        for (int index = 0; index < blocks.Count; index++)
        {
            ComputeDescriptorPoolBlock block = blocks[index];
            if (block.UsesUpdateAfterBind != usesUpdateAfterBind || block.AllocatedAllocations >= block.MaxAllocations)
                continue;
            Result result = TryAllocateComputeDescriptorSets(context, block.Pool, layouts, usesUpdateAfterBind, out descriptorSets);
            if (result == Result.Success)
            {
                block.AllocatedAllocations++;
                allocationPool = block.Pool;
                return true;
            }
            if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
                block.AllocatedAllocations = block.MaxAllocations;
            else
                return false;
        }

        uint capacity = blocks.Count == 0 ? 64u : Math.Min(blocks[^1].MaxAllocations * 2u, 512u);
        if (!TryCreateComputeDescriptorPool(context, capacity, layouts, poolSizes, poolSizeCount, usesUpdateAfterBind, out ComputeDescriptorPoolBlock? created))
            return false;
        ComputeDescriptorPoolBlock createdBlock = created!;
        blocks.Add(createdBlock);
        Result allocate = TryAllocateComputeDescriptorSets(context, createdBlock.Pool, layouts, usesUpdateAfterBind, out descriptorSets);
        if (allocate != Result.Success)
            return false;
        createdBlock.AllocatedAllocations++;
        allocationPool = createdBlock.Pool;
        return true;
    }

    private static bool TryCreateComputeDescriptorPool(
        VulkanBackendObjectContext context,
        uint allocationCapacity,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] poolSizes,
        int poolSizeCount,
        bool usesUpdateAfterBind,
        out ComputeDescriptorPoolBlock? block)
    {
        block = null;
        DescriptorPoolSize[] scaled = new DescriptorPoolSize[poolSizeCount];
        for (int index = 0; index < poolSizeCount; index++)
            scaled[index] = new DescriptorPoolSize
            {
                Type = poolSizes[index].Type,
                DescriptorCount = checked(Math.Max(poolSizes[index].DescriptorCount, 1u) * allocationCapacity),
            };

        fixed (DescriptorPoolSize* sizes = scaled)
        {
            DescriptorPoolCreateInfo info = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = usesUpdateAfterBind ? DescriptorPoolCreateFlags.UpdateAfterBindBit : 0,
                PoolSizeCount = (uint)scaled.Length,
                PPoolSizes = sizes,
                MaxSets = checked(allocationCapacity * (uint)layouts.Length),
            };
            if (context.Api.CreateDescriptorPool(context.Device, ref info, null, out DescriptorPool pool) != Result.Success)
                return false;
            block = new ComputeDescriptorPoolBlock
            {
                Pool = pool,
                MaxAllocations = allocationCapacity,
                UsesUpdateAfterBind = usesUpdateAfterBind,
            };
            context.Resources.Lifetime.Tracker.RegisterResource(new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, pool.Handle), "Compute.DescriptorPool", false);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
            return true;
        }
    }

    private static Result TryAllocateComputeDescriptorSets(
        VulkanBackendObjectContext context,
        DescriptorPool pool,
        DescriptorSetLayout[] layouts,
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = new DescriptorSet[layouts.Length];
        fixed (DescriptorSetLayout* layoutPtr = layouts)
        fixed (DescriptorSet* descriptorPtr = descriptorSets)
        {
            DescriptorSetAllocateInfo info = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = (uint)layouts.Length,
                PSetLayouts = layoutPtr,
            };
            Result result = context.Api.AllocateDescriptorSets(context.Device, ref info, descriptorPtr);
            if (result == Result.Success)
            {
                context.Resources.DescriptorLifetime.RegisterDescriptorSets(
                    pool, descriptorSets, usesUpdateAfterBind, "Compute.DescriptorSet");
                context.Resources.DescriptorLifetime.RecordTableGeneration();
            }
            return result;
        }
    }
}
