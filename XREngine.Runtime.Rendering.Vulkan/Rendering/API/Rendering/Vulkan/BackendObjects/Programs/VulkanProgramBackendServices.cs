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
internal unsafe sealed class VulkanProgramBackendServices(
    VulkanBackendObjectContext context,
    VulkanCommandRuntime commandRuntime,
    VulkanFramePlanner framePlanner,
    VulkanFrameTelemetry telemetry)
{
    private readonly VulkanTrackedCommandEncoder _encoder = new(
        context.Api,
        context.DeviceContext,
        commandRuntime,
        context.Resources,
        telemetry);
    private readonly ThreadLocal<Dictionary<Type, object>> _bindingWorkspaces =
        new(static () => []);

    internal VulkanCommandRuntime CommandRuntime => commandRuntime;
    internal VulkanFrameTelemetry Telemetry => telemetry;

    internal T GetOrCreateBindingWorkspace<T>(Func<T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Dictionary<Type, object> workspaces = _bindingWorkspaces.Value!;
        Type key = typeof(T);
        if (workspaces.TryGetValue(key, out object? existing))
            return (T)existing;

        T created = factory();
        workspaces.Add(key, created);
        return created;
    }

    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        if (buffer is null)
            return;

        string name = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Name ?? string.Empty
            : buffer.AttributeName;
        if (!string.IsNullOrWhiteSpace(name))
            framePlanner.TrackedBuffersByName[name] = buffer;
    }

    internal int ResolveDescriptorViewFamilyIdentity()
    {
        ResourcePlannerRuntimeState state = framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State;
        FrameOpContext? context = state.LastActiveFrameOpContext;
        if (context is not { } active)
            return 0;

        return active.OutputTargetIdentity != 0
            ? active.OutputTargetIdentity
            : active.ViewportIdentity;
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
    {
        if (pipeline.Handle != 0)
            context.Lifetime.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.Pipeline, pipeline.Handle),
                owner,
                externallyOwned: false);
    }

    internal void NotifyPipelineCreated(string kind)
        => context.Pipelines.NotifyPipelineCreated(kind);

    internal void RecordComputePipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VkRenderProgram program,
        ulong programPipelineHash)
    {
        _ = passIndex;
        _ = passMetadata;
        _ = program;
        _ = programPipelineHash;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: false);
    }

    internal void ExecuteWithPipelineCompilationQuiesced(Action mutation, string reason)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        using VulkanPipelineCompilationMutationLease lease =
            context.Pipelines.AcquireCompilationMutationLease(reason);
        mutation();
    }

    internal void BindDescriptorSetsTracked(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        uint firstSet,
        DescriptorSet[] sets)
    {
        if (sets.Length == 0)
            return;

        _encoder.Track(commandBuffer, ObjectType.PipelineLayout, layout.Handle);
        for (int index = 0; index < sets.Length; index++)
            _encoder.Track(commandBuffer, ObjectType.DescriptorSet, sets[index].Handle);
        fixed (DescriptorSet* setPtr = sets)
            context.Api.CmdBindDescriptorSets(
                commandBuffer,
                bindPoint,
                layout,
                firstSet,
                (uint)sets.Length,
                setPtr,
                0,
                null);
    }

    internal bool TryPushDescriptorHeapProgramData(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        DescriptorHeapPushDataPayload? payload,
        out string reason)
    {
        reason = string.Empty;
        if (payload is null)
        {
            reason = $"descriptor heap payload is missing for program '{program.Data.Name ?? "UnnamedProgram"}'.";
            return false;
        }
        if (_encoder.TryPushDescriptorHeapProgramData(
                commandBuffer,
                program,
                payload.Dwords,
                payload.Dwords.Length))
        {
            return true;
        }

        reason = $"descriptor heap push failed for program '{program.Data.Name ?? "UnnamedProgram"}'.";
        return false;
    }

    // Descriptor-set cache ownership is migrated with the command recording
    // authority; these calls remain here as the wrapper-facing seam.
    internal bool TryGetPreparedComputeDescriptorSets(
        uint imageIndex,
        ulong schemaKey,
        ulong bindingKey,
        out DescriptorSet[] descriptorSets)
        => TryGetPreparedComputeDescriptorSetsCore(
            context.Descriptors, imageIndex, schemaKey, bindingKey, out descriptorSets);

    internal string DescribePreparedComputeDescriptorBindings(uint imageIndex, ulong schemaKey)
    {
        VulkanDescriptorManager descriptors = context.Descriptors;
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
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets,
        out bool isNewAllocation)
        => TryGetOrCreateComputeDescriptorSetsCore(
            context, imageIndex, schemaKey, bindingKey, layouts, poolSizes,
            usesUpdateAfterBind, out descriptorSets, out isNewAllocation);

    internal void DispatchCompute(VkRenderProgram program, int x, int y, int z)
    {
        if (!context.IsDeviceOperational)
            return;

        if (!program.Link(program.Data.AllowAsyncBackendCompile))
            return;

        ResourcePlannerRuntimeState plannerState = framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State;
        FrameOpContext frameContext = plannerState.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;

        ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();
        if (!program.ValidateComputeSnapshot(snapshot, out _))
            return;

        try
        {
            if (program.GetOrCreateComputePipeline(passIndex, frameContext.PassMetadata).Handle == 0)
                return;
        }
        catch
        {
            return;
        }

        // The command-owned queue is the sole mutable ingress. The preparation
        // phase performs the same resource-use lowering for the frozen op set.
        VulkanFrameOperationQueue queue = framePlanner.Operations;
        using (queue.SyncRoot.EnterScope())
            queue.Pending.Add(ComputeDispatchOp.Rent(
                passIndex,
                program,
                checked((uint)Math.Max(x, 1)),
                checked((uint)Math.Max(y, 1)),
                checked((uint)Math.Max(z, 1)),
                snapshot,
                frameContext));
    }

    /// <summary>
    /// Publishes a transform-feedback operation through the generation-local frame
    /// planner.  The producer captures only planner state; native transform-feedback
    /// command encoding remains owned by the command runtime when this operation is
    /// consumed by primary recording.
    /// </summary>
    internal void EnqueueTransformFeedback(
        VkTransformFeedback transformFeedback,
        EXRTransformFeedbackOperation operation,
        XRDataBuffer? counterBuffer,
        ulong feedbackBufferOffset,
        ulong? feedbackBufferSize,
        ulong counterBufferOffset,
        uint counterOffset,
        uint vertexStride,
        uint instanceCount,
        uint firstInstance)
    {
        ArgumentNullException.ThrowIfNull(transformFeedback);

        ResourcePlannerRuntimeState plannerState = framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State;
        FrameOpContext frameContext = plannerState.LastActiveFrameOpContext ?? default;
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;

        VulkanFrameOperationQueue queue = framePlanner.Operations;
        using (queue.SyncRoot.EnterScope())
            queue.Pending.Add(new TransformFeedbackOp(
                passIndex,
                frameContext.OutputFrameBuffer,
                transformFeedback,
                operation,
                counterBuffer,
                feedbackBufferOffset,
                feedbackBufferSize,
                counterBufferOffset,
                counterOffset,
                vertexStride,
                instanceCount,
                firstInstance,
                frameContext));
    }

    /// <summary>
    /// Publishes the legacy framebuffer selection to command-owned mutable state.
    /// The framebuffer wrapper has already created its generation-local resources;
    /// recording later captures the effective target into immutable operation input.
    /// </summary>
    internal void SetBoundFrameBufferState(
        EFramebufferTarget target,
        XRFrameBuffer? frameBuffer)
        => commandRuntime.SetBoundFrameBufferState(target, frameBuffer);

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
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets,
        out bool isNewAllocation)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
        isNewAllocation = false;
        if (layouts.Length == 0 || poolSizes.Length == 0)
            return false;

        VulkanDescriptorManager descriptors = context.Descriptors;
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
                    context, cache, schemaKey, layouts, poolSizes,
                    usesUpdateAfterBind, out descriptorSets))
            {
                return false;
            }

            cache.CachedSets.Add(key, descriptorSets);
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
        bool usesUpdateAfterBind,
        out DescriptorSet[] descriptorSets)
    {
        descriptorSets = Array.Empty<DescriptorSet>();
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
                return true;
            }
            if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
                block.AllocatedAllocations = block.MaxAllocations;
            else
                return false;
        }

        uint capacity = blocks.Count == 0 ? 64u : Math.Min(blocks[^1].MaxAllocations * 2u, 512u);
        if (!TryCreateComputeDescriptorPool(context, capacity, layouts, poolSizes, usesUpdateAfterBind, out ComputeDescriptorPoolBlock? created))
            return false;
        ComputeDescriptorPoolBlock createdBlock = created!;
        blocks.Add(createdBlock);
        Result allocate = TryAllocateComputeDescriptorSets(context, createdBlock.Pool, layouts, usesUpdateAfterBind, out descriptorSets);
        if (allocate != Result.Success)
            return false;
        createdBlock.AllocatedAllocations++;
        return true;
    }

    private static bool TryCreateComputeDescriptorPool(
        VulkanBackendObjectContext context,
        uint allocationCapacity,
        DescriptorSetLayout[] layouts,
        DescriptorPoolSize[] poolSizes,
        bool usesUpdateAfterBind,
        out ComputeDescriptorPoolBlock? block)
    {
        block = null;
        DescriptorPoolSize[] scaled = new DescriptorPoolSize[poolSizes.Length];
        for (int index = 0; index < poolSizes.Length; index++)
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
            context.Lifetime.RegisterResource(new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, pool.Handle), "Compute.DescriptorPool", false);
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
                context.DescriptorLifetime.RegisterDescriptorSets(
                    pool, descriptorSets, usesUpdateAfterBind, "Compute.DescriptorSet");
                context.DescriptorLifetime.RecordTableGeneration();
            }
            return result;
        }
    }
}
