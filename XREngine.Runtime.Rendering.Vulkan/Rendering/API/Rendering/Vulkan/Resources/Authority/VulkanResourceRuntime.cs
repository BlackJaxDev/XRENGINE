using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Aggregates the mutable resource services for one logical-device lifetime.
/// </summary>
/// <remarks>
/// This type deliberately has no renderer reference. Native Vulkan calls, command recording,
/// and shutdown ordering remain renderer concerns; this object only establishes the single
/// ownership boundary for the state those operations mutate.
/// </remarks>
internal sealed partial class VulkanResourceRuntime
{
    internal const int DefaultRetirementDrainLimitPerFrame = 8;

    private int _framebufferRetirementFrameSlot;
    private long _descriptorTableGeneration;
    internal VulkanResourceRuntime(int frameSlotCount)
    {
        BackendObjects = new VulkanBackendObjectRegistry();
        Descriptors = new VulkanDescriptorManager();
        Allocations = new VulkanAllocationAuthority(
            new VulkanBufferResourceManager(),
            new VulkanImageAllocationTracker(),
            new VulkanStagingManager());
        Buffers = new VulkanBufferResourceService(Allocations);
        Uploads = new VulkanTextureUploadService();
        Queries = new VulkanQueryAuthority();
        Lifetime = new VulkanLifetimeAuthority(
            new VulkanResourceLifetimeTracker(),
            new VulkanResourceRetirementQueue(frameSlotCount));
        DescriptorLifetime = new VulkanDescriptorLifetimeAuthority(this, Descriptors, Lifetime);
        Images = new VulkanImageResourceService(Allocations, Lifetime);
        Images.ConfigureRetirementRuntime(this);
        FallbackTexture = new VulkanFallbackTextureAuthority(this, new VulkanFallbackTextureState());
        BlackFallbackTexture = new VulkanFallbackTextureAuthority(
            this,
            new VulkanFallbackTextureState(),
            "BlackFallbackTexture",
            red: 0,
            green: 0,
            blue: 0,
            alpha: 255);
        Buffers.BindLifetime(Lifetime);
        Samplers = new VulkanSamplerResourceService(this, Descriptors, Lifetime);
        Framebuffers = new VulkanFrameBufferResourceService(this);
        SparseTextureStreaming = new VulkanSparseTextureStreamingService();
        ResidentDrawTemplates = new VulkanResidentDrawTemplateTable(
            this,
            primaryCapacity: 256u,
            variantsPerDraw: 1u);
        ResidentTemplateFrameSlotLifetimes =
            new VulkanResidentTemplateFrameSlotLifetimes(frameSlotCount);
    }

    internal VulkanBackendObjectRegistry BackendObjects { get; }

    /// <summary>
    /// Resource-owned descriptor publication generation. Frame telemetry observes
    /// this value at its frame-loop boundary rather than being retained by
    /// descriptor lifetime services.
    /// </summary>
    internal ulong DescriptorTableGeneration
        => unchecked((ulong)Volatile.Read(ref _descriptorTableGeneration));

    internal void RecordDescriptorTableGeneration()
        => Interlocked.Increment(ref _descriptorTableGeneration);
    internal VulkanDescriptorManager Descriptors { get; }
    internal VulkanAllocationAuthority Allocations { get; }
    internal VulkanBufferResourceService Buffers { get; }
    internal VulkanImageResourceService Images { get; }
    internal VulkanTextureUploadService Uploads { get; }
    internal VulkanQueryAuthority Queries { get; }
    internal VulkanFallbackTextureAuthority FallbackTexture { get; }
    internal VulkanFallbackTextureAuthority BlackFallbackTexture { get; }
    internal VulkanLifetimeAuthority Lifetime { get; }
    internal VulkanDescriptorLifetimeAuthority DescriptorLifetime { get; }
    internal VulkanSamplerResourceService Samplers { get; }
    internal VulkanFrameBufferResourceService Framebuffers { get; }
    internal VulkanSparseTextureStreamingService SparseTextureStreaming { get; }
    internal VulkanResidentDrawTemplateTable ResidentDrawTemplates { get; }
    internal VulkanResidentTemplateFrameSlotLifetimes ResidentTemplateFrameSlotLifetimes { get; }
    internal VulkanPipelineManager PipelineManager { get; } = new();
    internal VulkanBackendObjectContext? BackendObjectContext;
    internal bool AllowSynchronousResourceUploads { get; private set; }

    /// <summary>Resolves the placeholder authority for an intentionally unassigned sampled texture.</summary>
    internal VulkanFallbackTextureAuthority GetMissingTextureFallback(RenderingParameters renderOptions)
        => renderOptions.MissingTextureFallback == EMissingTextureFallback.Black
            ? BlackFallbackTexture
            : FallbackTexture;
    private VulkanWrapperLookupPort? _wrapperLookup;
    private VulkanBackendObjectFactory? _backendObjectFactory;
    private VulkanWrapperColdComposition? _wrapperColdComposition;
    private VulkanResourceCommandWrapperPort? _synchronousCommands;
    private RenderGraph.VulkanResourcePlannerPublicationReader? _plannerPublications;
    internal VulkanWrapperLookupPort WrapperLookup
        => _wrapperLookup ?? throw new InvalidOperationException("The Vulkan resource runtime has no published wrapper lookup port.");

    /// <summary>
    /// Factory-owned cold wrapper composition for this resource generation.
    /// The resource runtime retains it only as the generation-local creation
    /// boundary; its behavior ports are published by bootstrap composition.
    /// </summary>
    internal VulkanWrapperColdComposition WrapperColdComposition
        => _wrapperColdComposition ?? throw new InvalidOperationException(
            "The Vulkan resource runtime has no published wrapper cold composition.");

    internal VulkanBackendObjectContext GetOrCreateBackendObjectContext(
        Vk api,
        VulkanDeviceContext deviceContext)
    {
        VulkanBackendObjectContext context = BackendObjectContext ?? PublishBackendObjectContext(
            new VulkanBackendObjectContext(api, deviceContext, this));
        VulkanWrapperLookupPort lookup = _wrapperLookup ??= new VulkanWrapperLookupPort(context);
        _wrapperColdComposition ??= new VulkanWrapperColdComposition(lookup);
        return context;
    }

    internal AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
    {
        VulkanBackendObjectContext context = BackendObjectContext ?? throw new InvalidOperationException(
            "The Vulkan backend object context has not been published.");
        VulkanWrapperColdComposition composition = _wrapperColdComposition ?? throw new InvalidOperationException(
            "The Vulkan wrapper cold composition has not been published.");
        return composition.GetOrCreate(_backendObjectFactory ??= new VulkanBackendObjectFactory(), context, renderObject);
    }

    /// <summary>
    /// Resource-owned synchronous command service.  Resource wrappers resolve
    /// it from their generation context at the operation boundary rather than
    /// retaining a command/telemetry/planner port for their entire lifetime.
    /// </summary>
    internal VulkanResourceCommandWrapperPort SynchronousCommands
        => _synchronousCommands ?? throw new InvalidOperationException(
            "The Vulkan resource runtime has no published synchronous command service.");

    /// <summary>Planner publication access owned by the resource generation.</summary>
    internal RenderGraph.VulkanResourcePlannerPublicationReader PlannerPublications
        => _plannerPublications ?? throw new InvalidOperationException(
            "The Vulkan resource runtime has no published planner publications.");

    internal void ConfigureWrapperOperationServices(
        VulkanResourceCommandWrapperPort synchronousCommands,
        RenderGraph.VulkanResourcePlannerPublicationReader publications)
    {
        ArgumentNullException.ThrowIfNull(synchronousCommands);
        ArgumentNullException.ThrowIfNull(publications);
        PublishSingle(ref _synchronousCommands,
            synchronousCommands,
            "synchronous command service");
        PublishSingle(ref _plannerPublications, publications, "planner publications");
    }

    private static void PublishSingle<T>(ref T? destination, T value, string name) where T : class
    {
        T? current = Interlocked.CompareExchange(ref destination, value, null);
        if (current is not null && !ReferenceEquals(current, value))
            throw new InvalidOperationException($"The Vulkan resource runtime already owns a different {name}.");
    }

    /// <summary>Publishes the immutable upload policy for this resource generation.</summary>
    internal void PublishSynchronousUploadPolicy(bool allowSynchronousUploads)
    {
        if (AllowSynchronousResourceUploads != default && AllowSynchronousResourceUploads != allowSynchronousUploads)
            throw new InvalidOperationException("The Vulkan resource runtime upload policy cannot change within a generation.");
        AllowSynchronousResourceUploads = allowSynchronousUploads;
    }

    internal RenderPass SwapchainRenderPass;
    internal RenderPass SwapchainLoadRenderPass;
    internal Dictionary<ulong, uint> RenderPassColorAttachmentCounts { get; } = new();
    internal Dictionary<ulong, Format[]> RenderPassColorAttachmentFormats { get; } = new();
    internal Dictionary<ulong, string> RenderPassSemanticSignatures { get; } = new();
    internal Dictionary<Format, bool> FormatColorBlendSupport { get; } = new();
    internal bool? SupportsGpuAutoExposure;
    internal bool AutoExposureComputeInitialized;
    internal XRRenderProgram? AutoExposureComputeProgram2D;
    internal XRRenderProgram? AutoExposureComputeProgram2DArray;
    internal object TextureUploadContextSync { get; } = new();
    internal Dictionary<VulkanFrameBufferRenderPassKey, Silk.NET.Vulkan.RenderPass> FrameBufferRenderPasses { get; } = new();
    internal VulkanPhysicalImageGroup? RetainedAutoExposureHistoryGroup;

    internal ulong GetPublishedGeneration(ObjectType type, ulong handle)
        => Lifetime.Tracker.GetPublishedGeneration(
            new VulkanResourceLifetimeKey(type, handle));

    internal int FramebufferRetirementFrameSlot
        => Volatile.Read(ref _framebufferRetirementFrameSlot);

    /// <summary>
    /// Publishes the desktop frame slot used for framebuffer retirement.  Output
    /// services use this instead of reaching through a renderer frame-loop mirror.
    /// </summary>
    internal void PublishFramebufferRetirementFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)Lifetime.Retirement.Framebuffers.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        Volatile.Write(ref _framebufferRetirementFrameSlot, frameSlot);
    }

    /// <summary>Registers a framebuffer and the image views it keeps alive.</summary>
    internal void RegisterFramebuffer(
        Framebuffer framebuffer,
        ReadOnlySpan<ImageView> attachments,
        string owner)
    {
        if (framebuffer.Handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        VulkanResourceLifetimeKey[] attachmentKeys = new VulkanResourceLifetimeKey[attachments.Length];
        for (int index = 0; index < attachments.Length; index++)
            attachmentKeys[index] = new VulkanResourceLifetimeKey(ObjectType.ImageView, attachments[index].Handle);

        tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.Framebuffer, framebuffer.Handle),
            owner,
            externallyOwned: false);
        lock (tracker.SyncRoot)
            tracker.FramebufferAttachments[framebuffer.Handle] = attachmentKeys;
    }

    /// <summary>
    /// Captures the framebuffer's completion proof and queues it once for the
    /// current output frame slot.  The global handle set prevents a duplicate
    /// destroy when more than one output artifact releases the same framebuffer.
    /// </summary>
    internal void RetireFramebuffer(Framebuffer framebuffer, string owner)
    {
        if (framebuffer.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.Framebuffer, framebuffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        VulkanRetirementTicket ticket;
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((resource.State & (EVulkanResourceLifetimeState.Destroyed | EVulkanResourceLifetimeState.PendingRetirement)) != 0)
                return;

            ticket = new VulkanRetirementTicket(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                resource.Generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, resource.Generation));
            resource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref tracker.RetirementSerial));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            tracker.PublishedResourceGenerations[key] = 0;
        }

        int frameSlot = Volatile.Read(ref _framebufferRetirementFrameSlot);
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                framebuffer.Handle,
                new RetiredFramebuffer(framebuffer, ticket),
                Lifetime.Retirement.Framebuffers,
                Lifetime.Retirement.FramebufferHandles,
                Lifetime.Retirement.AllFramebufferHandles);
    }

    /// <summary>
    /// Captures a query-pool generation's completion proof and queues its native
    /// destruction on the active output slot. Query wrappers use this resource
    /// authority instead of reaching through the renderer lifecycle facade.
    /// </summary>
    internal void RetireQueryPool(QueryPool queryPool, string owner)
    {
        if (queryPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.QueryPool, queryPool.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        VulkanRetirementTicket ticket;
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource = tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((resource.State & (EVulkanResourceLifetimeState.Destroyed | EVulkanResourceLifetimeState.PendingRetirement)) != 0)
                return;

            ticket = new VulkanRetirementTicket(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                resource.Generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, resource.Generation));
            resource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref tracker.RetirementSerial));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            tracker.PublishedResourceGenerations[key] = 0;
        }

        int frameSlot = Volatile.Read(ref _framebufferRetirementFrameSlot);
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                queryPool.Handle,
                new RetiredQueryPool(queryPool, ticket),
                Lifetime.Retirement.QueryPools,
                Lifetime.Retirement.QueryPoolHandles,
                Lifetime.Retirement.AllQueryPoolHandles);
    }

    /// <summary>Publishes legacy render-pass metadata to resource consumers.</summary>
    internal void RegisterRenderPass(
        RenderPass renderPass,
        ReadOnlySpan<Format> colorAttachmentFormats,
        string semanticSignature)
    {
        if (renderPass.Handle == 0)
            return;

        Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.RenderPass, renderPass.Handle),
            "RenderPass",
            externallyOwned: false);
        RenderPassColorAttachmentCounts[renderPass.Handle] = (uint)colorAttachmentFormats.Length;
        RenderPassColorAttachmentFormats[renderPass.Handle] = colorAttachmentFormats.ToArray();
        RenderPassSemanticSignatures[renderPass.Handle] = semanticSignature;
    }

    /// <summary>
    /// Registers a short-lived command buffer and its pool ownership before a
    /// synchronous sidecar submission records any native resource references.
    /// </summary>
    internal void RegisterSynchronousCommandBuffer(
        CommandBuffer commandBuffer,
        CommandPool commandPool,
        CommandBufferLevel level,
        string owner)
    {
        if (commandBuffer.Handle == 0 || commandPool.Handle == 0)
            throw new ArgumentException("A synchronous command buffer requires a live command buffer and command pool.");

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
            owner,
            externallyOwned: false);
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeKey poolKey = new(ObjectType.CommandPool, commandPool.Handle);
            VulkanResourceLifetimeRecord pool = tracker.GetOrRegisterResourceNoLock(
                poolKey,
                "SynchronousCommandBuffer.AllocationPool");
            if ((pool.State & (EVulkanResourceLifetimeState.PendingRetirement |
                               EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot allocate synchronous command buffer 0x{handle:X} from retiring command pool {poolKey}.");
            }

            VulkanCommandBufferLifetimeRecord lifetime = new()
            {
                Level = level,
                AllocatingCommandPool = poolKey,
                AllocatingCommandPoolGeneration = pool.Generation,
            };
            tracker.CommandBufferLifetimes[handle] = lifetime;
            if (!tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? children))
            {
                children = [];
                tracker.CommandBuffersByPool.Add(poolKey, children);
            }
            children.Add(handle);
        }
    }

    internal void CompleteSynchronousFence(Fence fence)
    {
        if (fence.Handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        ulong handle = unchecked((ulong)fence.Handle);
        lock (tracker.SyncRoot)
        {
            for (int index = tracker.LifetimeSubmissions.Count - 1; index >= 0; index--)
            {
                VulkanLifetimeSubmission submission = tracker.LifetimeSubmissions[index];
                if (submission.FenceHandle != handle)
                    continue;

                tracker.MarkQueueSequenceCompletedNoLock(submission.QueueDomain, submission.QueueSequence);
                tracker.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }

    internal void CompleteSynchronousCommandBuffer(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.CommandBufferLifetimes.Remove(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                foreach ((VulkanResourceLifetimeKey key, ulong generation) in lifetime.Dependencies)
                {
                    if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                        resource.Generation != generation)
                    {
                        continue;
                    }

                    resource.Pins.ReleaseRecordedReference();
                    if (!resource.Pins.HasRecordedReferences)
                        resource.State &= ~EVulkanResourceLifetimeState.Recorded;
                    if (tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? commandBuffers))
                        commandBuffers.Remove(handle);
                }

                if (lifetime.AllocatingCommandPool.IsValid &&
                    tracker.CommandBuffersByPool.TryGetValue(lifetime.AllocatingCommandPool, out HashSet<ulong>? children))
                {
                    children.Remove(handle);
                    if (children.Count == 0)
                        tracker.CommandBuffersByPool.Remove(lifetime.AllocatingCommandPool);
                }
            }
        }

        CompleteDetachedExternalResourceDestruction(
            ObjectType.CommandBuffer,
            handle,
            GetPublishedGeneration(ObjectType.CommandBuffer, handle),
            forced: false);
    }

    /// <summary>
    /// Acquires the immutable arena generation captured by a prepared worker draw.
    /// Worker recordings have no mutable manifest scope, so the sealed draw generation
    /// is the sole admissible authority.
    /// </summary>
    internal bool TryAcquirePreparedFrameDataLease(
        CommandBuffer commandBuffer,
        VkMeshRenderer owner,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
    {
        reason = string.Empty;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong generation = MappedFrameArena?.Generation ?? 0UL;
        if (commandBufferHandle == 0 || generation == 0)
            return true;
        if (owner is null || sealedGeneration == 0 || sealedGeneration != generation)
        {
            reason = $"prepared frame-data generation {sealedGeneration} does not match active generation {generation}";
            return false;
        }

        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
            }

            if (lifetime.QueuedSubmissionCount != 0 ||
                (lifetime.FrameDataLease.Generation != 0 && lifetime.FrameDataLease.Generation != generation) ||
                !lifetime.FrameDataLease.TryAcquireRecording(generation, commandBufferQueued: false))
            {
                reason = $"command buffer 0x{commandBufferHandle:X} cannot acquire prepared frame-data generation {generation}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Transactionally validates and publishes the dependencies accumulated while a
    /// command buffer was recorded. This is the command-runtime counterpart to the
    /// submission receipt: no individual resource is pinned until every expansion
    /// of the batch has passed its generation and retirement checks.
    /// </summary>
    internal bool TryPublishCommandBufferTrackingBatch(
        CommandBuffer commandBuffer,
        VulkanCommandBufferTrackingBatch batch,
        out string reason)
    {
        reason = string.Empty;
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0)
            return true;

        lock (Lifetime.Tracker.SyncRoot)
        lock (batch)
        {
            if (batch.QueuedSubmissionCount != 0)
            {
                reason = $"Command buffer 0x{commandBufferHandle:X} is queued for submission.";
                return false;
            }

            foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
            {
                if (!TryValidateCommandBufferDependencyNoLock(commandBufferHandle, key, out reason))
                    return false;
            }

            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                Lifetime.Tracker.CommandBufferLifetimes[commandBufferHandle] = lifetime;
            }

            foreach (VulkanResourceLifetimeKey key in batch.Dependencies)
                PublishCommandBufferDependencyNoLock(commandBufferHandle, lifetime, key);

            lifetime.RefreshTouchedDependencies();
            batch.Dependencies.Clear();
            return true;
        }
    }

    internal void AbandonCommandBufferRecording(CommandBuffer commandBuffer)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0)
            return;

        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
                return;

            lifetime.FrameDataLease.AbandonRecording();
            ReleasePreparedCommandBufferDependenciesNoLock(commandBufferHandle, lifetime);
        }
    }

    internal void CompleteCommandBufferRecording(CommandBuffer commandBuffer, bool cacheVariant)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0)
            return;

        lock (Lifetime.Tracker.SyncRoot)
        {
            if (Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime))
                lifetime.FrameDataLease.CompleteRecording(cacheVariant);
        }
    }

    internal bool TryValidateCommandBufferDependencyNoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        out string reason)
    {
        VulkanResourceLifetimeRecord resource = Lifetime.Tracker.GetOrRegisterResourceNoLock(key, "CommandRuntime.TrackingBatch");
        if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            reason = $"Command buffer 0x{commandBufferHandle:X} attempted to record retired resource {key} generation {resource.Generation}.";
            return false;
        }

        if (key.Type == ObjectType.ImageView &&
            Lifetime.Tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImage) &&
            backingImage != 0)
        {
            if (!TryValidateCommandBufferDependencyNoLock(commandBufferHandle, new VulkanResourceLifetimeKey(ObjectType.Image, backingImage), out reason))
                return false;
        }
        else if (key.Type == ObjectType.BufferView &&
                 Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBuffer) &&
                 backingBuffer != 0)
        {
            if (!TryValidateCommandBufferDependencyNoLock(commandBufferHandle, new VulkanResourceLifetimeKey(ObjectType.Buffer, backingBuffer), out reason))
                return false;
        }
        else if (key.Type == ObjectType.Framebuffer &&
                 Lifetime.Tracker.FramebufferAttachments.TryGetValue(key.Handle, out VulkanResourceLifetimeKey[]? attachments))
        {
            for (int index = 0; index < attachments.Length; index++)
                if (attachments[index].IsValid && !TryValidateCommandBufferDependencyNoLock(commandBufferHandle, attachments[index], out reason))
                    return false;
        }

        reason = string.Empty;
        return true;
    }

    internal void PublishCommandBufferDependencyNoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime,
        VulkanResourceLifetimeKey key)
    {
        VulkanResourceLifetimeRecord resource = Lifetime.Tracker.GetOrRegisterResourceNoLock(key, "CommandRuntime.TrackingBatch");
        if (!lifetime.Dependencies.TryGetValue(key, out ulong generation) || generation != resource.Generation)
        {
            lifetime.Dependencies[key] = resource.Generation;
            resource.Pins.AddRecordedReference();
            resource.State |= EVulkanResourceLifetimeState.Recorded;
            if (!Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? buffers))
            {
                buffers = [];
                Lifetime.Tracker.ResourceCommandBufferDependencies[key] = buffers;
            }
            buffers.Add(commandBufferHandle);
        }

        if (key.Type == ObjectType.ImageView && Lifetime.Tracker.ImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImage) && backingImage != 0)
            PublishCommandBufferDependencyNoLock(commandBufferHandle, lifetime, new VulkanResourceLifetimeKey(ObjectType.Image, backingImage));
        else if (key.Type == ObjectType.BufferView && Lifetime.Tracker.BufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBuffer) && backingBuffer != 0)
            PublishCommandBufferDependencyNoLock(commandBufferHandle, lifetime, new VulkanResourceLifetimeKey(ObjectType.Buffer, backingBuffer));
        else if (key.Type == ObjectType.Framebuffer && Lifetime.Tracker.FramebufferAttachments.TryGetValue(key.Handle, out VulkanResourceLifetimeKey[]? attachments))
        {
            for (int index = 0; index < attachments.Length; index++)
                if (attachments[index].IsValid)
                    PublishCommandBufferDependencyNoLock(commandBufferHandle, lifetime, attachments[index]);
        }
    }

    private void ReleasePreparedCommandBufferDependenciesNoLock(ulong commandBufferHandle, VulkanCommandBufferLifetimeRecord lifetime)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in lifetime.Dependencies)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) || resource.Generation != generation)
                continue;
            resource.Pins.ReleaseRecordedReference();
            if (!resource.Pins.HasRecordedReferences)
                resource.State &= ~EVulkanResourceLifetimeState.Recorded;
            if (Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? buffers))
                buffers.Remove(commandBufferHandle);
        }

        lifetime.Dependencies.Clear();
        lifetime.TouchedDependencies.Clear();
    }

    internal void CompleteDetachedExternalResourceDestruction(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        bool forced)
    {
        if (handle == 0 || expectedGeneration == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != expectedGeneration)
                return;

            if (forced)
                Interlocked.Increment(ref Lifetime.Tracker.ForcedResourceDestructionCount);
            resource.State = EVulkanResourceLifetimeState.Destroyed;
            Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
        }
    }

    internal void UnregisterRenderPass(RenderPass renderPass)
    {
        if (renderPass.Handle == 0)
            return;

        RenderPassColorAttachmentCounts.Remove(renderPass.Handle);
        RenderPassColorAttachmentFormats.Remove(renderPass.Handle);
        RenderPassSemanticSignatures.Remove(renderPass.Handle);
    }

    /// <summary>
    /// Detaches externally owned WSI image identities so a recreated swapchain
    /// may legally reuse native handles while the prior generation retires.
    /// Command artifacts must already have been retired by the caller.
    /// </summary>
    internal ulong[] DetachExternalImageLifetimesForHandleReuse(Image[] images)
    {
        ulong[] generations = new ulong[images.Length];
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int index = 0; index < images.Length; index++)
            {
                ulong handle = images[index].Handle;
                if (handle == 0)
                    continue;
                VulkanResourceLifetimeKey key = new(ObjectType.Image, handle);
                if (!tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    continue;

                generations[index] = resource.Generation;
                tracker.ResourceLifetimes.Remove(key);
                tracker.PublishedResourceGenerations.TryRemove(key, out _);
                tracker.ResourceCommandBufferDependencies.Remove(key);
            }
        }
        return generations;
    }

    internal void NotifyResourceUseCompleted(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.Pins.ResetCompletion();
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }
    }

    internal bool CanResetCommandBuffer(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
            return CanResetCommandBufferNoLock(handle);
    }

    internal bool CanResetCommandBufferNoLock(ulong handle)
    {
        if (handle == 0)
            return false;

        if (Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                handle,
                out VulkanCommandBufferLifetimeRecord? lifetime))
        {
            if (lifetime.QueuedSubmissionCount != 0)
                return false;

            VulkanResourceLifetimeKey poolKey = lifetime.AllocatingCommandPool;
            if (poolKey.IsValid &&
                (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    poolKey,
                    out VulkanResourceLifetimeRecord? pool) ||
                 pool.Generation != lifetime.AllocatingCommandPoolGeneration ||
                 (pool.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                EVulkanResourceLifetimeState.Destroyed)) != 0))
            {
                return false;
            }
        }

        VulkanResourceLifetimeRecord commandRecord =
            Lifetime.Tracker.GetOrRegisterResourceNoLock(
                new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                "CommandBuffer.Reset");
        if ((commandRecord.State &
             (EVulkanResourceLifetimeState.PendingRetirement |
              EVulkanResourceLifetimeState.Destroyed)) != 0 ||
            commandRecord.Pins.HasRecordedReferences)
        {
            return false;
        }

        return UpdateResourceCompletionStateNoLock(commandRecord);
    }

    internal bool TryValidateCommandBufferRecordingAdmissionNoLock(
        ulong handle,
        out string reason)
    {
        reason = string.Empty;
        if (handle == 0)
        {
            reason = "A live command buffer is required.";
            return false;
        }

        VulkanResourceLifetimeRecord commandRecord =
            Lifetime.Tracker.GetOrRegisterResourceNoLock(
                new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                "CommandBuffer.RecordingAdmission");
        if ((commandRecord.State &
             (EVulkanResourceLifetimeState.PendingRetirement |
              EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            reason = $"Command buffer 0x{handle:X} is {commandRecord.State}.";
            return false;
        }

        if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                handle,
                out VulkanCommandBufferLifetimeRecord? lifetime))
            return true;

        if (lifetime.QueuedSubmissionCount != 0)
        {
            reason = $"Command buffer 0x{handle:X} is queued for submission.";
            return false;
        }

        VulkanResourceLifetimeKey poolKey = lifetime.AllocatingCommandPool;
        if (!poolKey.IsValid)
            return true;

        if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                poolKey,
                out VulkanResourceLifetimeRecord? pool) ||
            pool.Generation != lifetime.AllocatingCommandPoolGeneration ||
            (pool.State & (EVulkanResourceLifetimeState.PendingRetirement |
                           EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            reason = $"Command buffer 0x{handle:X} allocating pool {poolKey} is no longer live.";
            return false;
        }

        return true;
    }

    internal void CompleteCommandBufferReset(ulong handle)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                return;
            }

            ReleaseCommandBufferDependenciesNoLock(handle, lifetime);
            lifetime.FrameDataLease.EvictCachedVariant();
            lifetime.FrameDataLease.Reset();
            lifetime.RecordingGeneration++;
        }
    }

    internal void CompleteCommandBufferDestruction(CommandBuffer commandBuffer)
        => CompleteSimpleResourceDestruction(
            ObjectType.CommandBuffer,
            unchecked((ulong)commandBuffer.Handle));

    internal void CompleteCommandPoolDestruction(CommandPool commandPool)
        => CompleteSimpleResourceDestruction(
            ObjectType.CommandPool,
            commandPool.Handle);

    internal void CompletePipelineDestruction(Pipeline pipeline)
        => CompleteSimpleResourceDestruction(
            ObjectType.Pipeline,
            pipeline.Handle);

    internal void CompleteCommandPoolChildDestructions(CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey poolKey = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        CommandBuffer[] children;
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                    poolKey,
                    out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
                return;

            children = new CommandBuffer[ownedChildren.Count];
            int index = 0;
            foreach (ulong childHandle in ownedChildren)
                children[index++] = new CommandBuffer
                {
                    Handle = unchecked((nint)childHandle),
                };
        }

        for (int index = 0; index < children.Length; index++)
        {
            CompleteCommandBufferDestruction(children[index]);
        }
    }

    internal void QueueCommandPoolRetirement(
        CommandPool commandPool,
        int frameSlot)
    {
        if (commandPool.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        VulkanRetirementTicket ticket;
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(
                    key,
                    "CommandRuntime.OwnedSecondaryPool");
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else
            {
                UpdateResourceCompletionStateNoLock(resource);
                ticket = new VulkanRetirementTicket(
                    resource.Pins.LastGraphicsSequence,
                    resource.Pins.LastTransferSequence,
                    resource.Pins.LastOtherSequence,
                    Stopwatch.GetTimestamp(),
                    resource.Generation,
                    (resource.State & EVulkanResourceLifetimeState.External) != 0,
                    VulkanRetirementPinSet.Single(key, resource.Generation));
                resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                resource.RetirementTicket = ticket;
                Lifetime.Tracker.PublishedResourceGenerations[key] = 0;
            }
        }

        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                commandPool.Handle,
                new RetiredCommandPool(commandPool, ticket),
                Lifetime.Retirement.CommandPools,
                Lifetime.Retirement.CommandPoolHandles,
                Lifetime.Retirement.AllCommandPoolHandles);
    }

    internal VulkanRetirementTicket PrepareCommandBufferRetirement(
        CommandBuffer commandBuffer,
        string owner)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return VulkanRetirementTicket.None;

        VulkanResourceLifetimeKey key = new(
            ObjectType.CommandBuffer,
            handle);
        Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord resource =
                Lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                return resource.RetirementTicket;

            UpdateResourceCompletionStateNoLock(resource);
            VulkanRetirementTicket ticket = new(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                resource.Generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, resource.Generation));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            Lifetime.Tracker.PublishedResourceGenerations[key] = 0;
            return ticket;
        }
    }

    internal void QueueCommandBufferRetirement(
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        in VulkanRetirementTicket ticket,
        int frameSlot)
    {
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                unchecked((ulong)commandBuffer.Handle),
                new RetiredCommandBuffer(
                    commandPool,
                    commandBuffer,
                    ticket),
                Lifetime.Retirement.CommandBuffers,
                Lifetime.Retirement.CommandBufferHandles,
                Lifetime.Retirement.AllCommandBufferHandles);
    }

    internal bool IsCommandBufferPendingRetirement(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        lock (Lifetime.Retirement.SyncRoot)
            return Lifetime.Retirement.AllCommandBufferHandles.Contains(
                unchecked((ulong)commandBuffer.Handle));
    }

    internal bool UpdateResourceCompletionStateNoLock(
        VulkanResourceLifetimeRecord resource)
    {
        bool completed =
            resource.Pins.LastGraphicsSequence <= Lifetime.Tracker.CompletedGraphicsSequence &&
            resource.Pins.LastTransferSequence <= Lifetime.Tracker.CompletedTransferSequence &&
            resource.Pins.LastOtherSequence <= Lifetime.Tracker.CompletedOtherSequence;
        if (!completed)
            return false;

        if ((resource.State & EVulkanResourceLifetimeState.Submitted) != 0)
        {
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }

        return true;
    }

    private void ReleaseCommandBufferDependenciesNoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord lifetime)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in
                 lifetime.Dependencies)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != generation)
                continue;

            resource.Pins.ReleaseRecordedReference();

            if (!resource.Pins.HasRecordedReferences)
                resource.State &= ~EVulkanResourceLifetimeState.Recorded;

            if (Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(
                    key,
                    out HashSet<ulong>? commandBuffers))
                commandBuffers.Remove(commandBufferHandle);
        }

        lifetime.Dependencies.Clear();
        lifetime.TouchedDependencies.Clear();
    }

    internal unsafe void DrainRetiredPipelines(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<RetiredPipeline> list = Lifetime.Retirement.Pipelines[frameSlot];
        List<RetiredPipeline> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0; index < list.Count && ready.Count < maxItems;)
            {
                RetiredPipeline candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.Pipeline.Handle,
                    Lifetime.Retirement.PipelineHandles,
                    Lifetime.Retirement.AllPipelineHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            Pipeline pipeline = ready[index].Pipeline;
            if (pipeline.Handle == 0)
                continue;

            api.DestroyPipeline(device, pipeline, null);
            CompleteSimpleResourceDestruction(
                ObjectType.Pipeline,
                pipeline.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            pipelines: destroyed);
    }

    internal void NotifyTimingQueryPoolsCompleted(
        in VulkanCompletedTimingQueryPools completed)
    {
        NotifyResourceUseCompleted(ObjectType.QueryPool, completed.FrameTiming.Handle);
        NotifyResourceUseCompleted(ObjectType.QueryPool, completed.GpuProfiler.Handle);
    }

    /// <summary>Publishes a program-owned pipeline layout to the lifetime ledger.</summary>
    internal void TrackPipelineLayout(PipelineLayout pipelineLayout, string owner)
    {
        if (pipelineLayout.Handle == 0)
            return;

        Lifetime.LivePipelineLayoutHandles[pipelineLayout.Handle] = owner;
        Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.PipelineLayout, pipelineLayout.Handle),
            owner,
            externallyOwned: false);
    }

    /// <summary>
    /// Begins pipeline-layout destruction. A layout still referenced by recorded
    /// work is queued on the current resource-retirement slot instead of being
    /// destroyed synchronously.
    /// </summary>
    internal bool TryBeginDestroyPipelineLayout(PipelineLayout pipelineLayout, string owner)
    {
        if (pipelineLayout.Handle == 0 || !Lifetime.LivePipelineLayoutHandles.TryRemove(
                pipelineLayout.Handle,
                out string? trackedOwner))
            return false;

        VulkanResourceLifetimeKey key = new(ObjectType.PipelineLayout, pipelineLayout.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        VulkanRetirementTicket ticket;
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord record = tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((record.State & (EVulkanResourceLifetimeState.Destroyed | EVulkanResourceLifetimeState.PendingRetirement)) != 0)
                return false;

            ticket = new VulkanRetirementTicket(
                record.Pins.LastGraphicsSequence,
                record.Pins.LastTransferSequence,
                record.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                record.Generation,
                (record.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, record.Generation));
            record.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref tracker.RetirementSerial));
            record.State |= EVulkanResourceLifetimeState.PendingRetirement;
            record.RetirementTicket = ticket;
            tracker.PublishedResourceGenerations[key] = 0;
        }

        if (tracker.IsRetirementReady(ticket))
        {
            CompleteSimpleResourceDestruction(ObjectType.PipelineLayout, pipelineLayout.Handle);
            return true;
        }

        int frameSlot = FramebufferRetirementFrameSlot;
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                pipelineLayout.Handle,
                new VulkanRetiredPipelineLayout(pipelineLayout, ticket, trackedOwner ?? owner),
                Lifetime.Retirement.PipelineLayouts,
                Lifetime.Retirement.PipelineLayoutHandles,
                Lifetime.Retirement.AllPipelineLayoutHandles);
        return false;
    }

    /// <summary>Queues a pipeline until every recorded dependency is complete.</summary>
    internal void RetirePipeline(Pipeline pipeline, string owner)
    {
        if (pipeline.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.Pipeline, pipeline.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        tracker.FenceResourceRecordingAdmission(key, owner);
        Lifetime.PublishTrackingDependenciesBeforeRetirement(key);
        VulkanRetirementTicket ticket;
        lock (tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord record = tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((record.State & (EVulkanResourceLifetimeState.Destroyed | EVulkanResourceLifetimeState.PendingRetirement)) != 0)
                return;

            ticket = new VulkanRetirementTicket(
                record.Pins.LastGraphicsSequence,
                record.Pins.LastTransferSequence,
                record.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                record.Generation,
                (record.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, record.Generation));
            record.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref tracker.RetirementSerial));
            record.State |= EVulkanResourceLifetimeState.PendingRetirement;
            record.RetirementTicket = ticket;
            tracker.PublishedResourceGenerations[key] = 0;
        }

        int frameSlot = FramebufferRetirementFrameSlot;
        lock (Lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                pipeline.Handle,
                new RetiredPipeline(pipeline, ticket),
                Lifetime.Retirement.Pipelines,
                Lifetime.Retirement.PipelineHandles,
                Lifetime.Retirement.AllPipelineHandles);
    }

    internal unsafe void DrainRetiredPipelineLayouts(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<VulkanRetiredPipelineLayout> list = Lifetime.Retirement.PipelineLayouts[frameSlot];
        List<VulkanRetiredPipelineLayout> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                VulkanRetiredPipelineLayout candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.PipelineLayout.Handle,
                    Lifetime.Retirement.PipelineLayoutHandles,
                    Lifetime.Retirement.AllPipelineLayoutHandles);
                Lifetime.LivePipelineLayoutHandles.TryRemove(
                    candidate.PipelineLayout.Handle,
                    out _);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            PipelineLayout layout = ready[index].PipelineLayout;
            if (layout.Handle == 0)
                continue;

            api.DestroyPipelineLayout(device, layout, null);
            CompleteSimpleResourceDestruction(
                ObjectType.PipelineLayout,
                layout.Handle);
        }
    }

    /// <summary>Destroys pipeline layouts left after the owning device is idle.</summary>
    internal unsafe int DestroyRemainingTrackedPipelineLayouts(Vk api, Device device)
    {
        int destroyed = 0;
        foreach (ulong handle in Lifetime.LivePipelineLayoutHandles.Keys.ToArray())
        {
            if (!Lifetime.LivePipelineLayoutHandles.TryRemove(handle, out _))
                continue;

            api.DestroyPipelineLayout(device, new PipelineLayout { Handle = handle }, null);
            CompleteSimpleResourceDestruction(ObjectType.PipelineLayout, handle);
            destroyed++;
        }
        return destroyed;
    }

    internal unsafe void DrainRetiredDescriptorSetLayouts(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<VulkanRetiredDescriptorSetLayout> list =
            Lifetime.Retirement.DescriptorSetLayouts[frameSlot];
        List<VulkanRetiredDescriptorSetLayout> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                VulkanRetiredDescriptorSetLayout candidate =
                    list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorSetLayout.Handle,
                    Lifetime.Retirement.DescriptorSetLayoutHandles,
                    Lifetime.Retirement.AllDescriptorSetLayoutHandles);
                Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(
                    candidate.DescriptorSetLayout.Handle,
                    out _);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            DescriptorSetLayout layout = ready[index].DescriptorSetLayout;
            if (layout.Handle == 0)
                continue;

            api.DestroyDescriptorSetLayout(device, layout, null);
            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorSetLayout,
                layout.Handle);
        }
    }

    internal unsafe void DrainRetiredQueryPools(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 32)
    {
        List<RetiredQueryPool> list = Lifetime.Retirement.QueryPools[frameSlot];
        List<RetiredQueryPool> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredQueryPool candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.QueryPool.Handle,
                    Lifetime.Retirement.QueryPoolHandles,
                    Lifetime.Retirement.AllQueryPoolHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            QueryPool queryPool = ready[index].QueryPool;
            api.DestroyQueryPool(device, queryPool, null);
            CompleteSimpleResourceDestruction(
                ObjectType.QueryPool,
                queryPool.Handle);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            queryPools: ready.Count);
    }

    internal unsafe void DrainRetiredBufferViews(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredBufferView> list = Lifetime.Retirement.BufferViews[frameSlot];
        List<RetiredBufferView> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredBufferView candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.BufferView.Handle,
                    Lifetime.Retirement.BufferViewHandles,
                    Lifetime.Retirement.AllBufferViewHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            BufferView bufferView = ready[index].BufferView;
            api.DestroyBufferView(device, bufferView, null);
            CompleteSimpleResourceDestruction(
                ObjectType.BufferView,
                bufferView.Handle);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            bufferViews: ready.Count);
    }

    /// <summary>
    /// Drains retired framebuffers for the specified frame slot, destroying them if they are ready for retirement.
    /// </summary>
    /// <param name="api">The Vulkan API instance.</param>
    /// <param name="device">The Vulkan device.</param>
    /// <param name="frameSlot">The frame slot for which to drain retired framebuffers.</param>
    /// <param name="maxItems">The maximum number of framebuffers to drain in this call.</param>
    internal unsafe void DrainRetiredFramebuffers(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredFramebuffer> list = Lifetime.Retirement.Framebuffers[frameSlot];
        List<RetiredFramebuffer> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredFramebuffer candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.Framebuffer.Handle,
                    Lifetime.Retirement.FramebufferHandles,
                    Lifetime.Retirement.AllFramebufferHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            Framebuffer framebuffer = ready[index].Framebuffer;
            if (framebuffer.Handle == 0)
                continue;

            api.DestroyFramebuffer(device, framebuffer, null);
            CompleteSimpleResourceDestruction(
                ObjectType.Framebuffer,
                framebuffer.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            framebuffers: destroyed);
    }

    /// <summary>
    /// Drains retired buffers for the specified frame slot, destroying them if they are ready for retirement.
    /// </summary>
    /// <param name="api">The Vulkan API instance.</param>
    /// <param name="device">The Vulkan device.</param>
    /// <param name="telemetry">The Vulkan frame telemetry instance.</param>
    /// <param name="frameSlot">The frame slot for which to drain retired buffers.</param>
    /// <param name="maxItems">The maximum number of buffers to drain in this call.</param>
    internal unsafe int DrainRetiredBuffers(
        Vk api,
        Device device,
        VulkanFrameTelemetry telemetry,
        int frameSlot,
        int maxItems = 256)
    {
        List<RetiredBuffer> list = Lifetime.Retirement.Buffers[frameSlot];
        List<RetiredBuffer> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredBuffer candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket) ||
                    HasUndestroyedBufferView(candidate.Buffer))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                if (candidate.Buffer.Handle != 0)
                {
                    Lifetime.Retirement.BufferHandles[frameSlot].Remove(
                        candidate.Buffer.Handle);
                    Lifetime.Retirement.AllBufferHandles.Remove(
                        candidate.Buffer.Handle);
                }
                if (candidate.Memory.Handle != 0)
                {
                    Lifetime.Retirement.MemoryHandles[frameSlot].Remove(
                        candidate.Memory.Handle);
                    Lifetime.Retirement.AllMemoryHandles.Remove(
                        candidate.Memory.Handle);
                }
            }
        }

        // Track the number of destroyed buffers, freed memories, and pooled buffers for telemetry.
        int destroyedBuffers = 0;
        int freedMemories = 0;
        int pooledBuffers = 0;
        // Drain the ready buffers, destroying or pooling them as appropriate.
        for (int index = 0; index < ready.Count; index++)
        {
            RetiredBuffer retired = ready[index];
            Silk.NET.Vulkan.Buffer buffer = retired.Buffer;
            DeviceMemory memory = retired.Memory;
            if (buffer.Handle != 0)
            {
                // VMA suballocations may share one VkDeviceMemory block. The
                // retirement queue deduplicates that block handle, so recover
                // this buffer's authoritative allocation identity before asking
                // the staging pool to release it for reuse.
                DeviceMemory poolMemory = memory;
                if (Allocations.Buffers.Allocations.TryGetValue(
                        buffer.Handle,
                        out VulkanMemoryAllocation trackedAllocation))
                {
                    poolMemory = trackedAllocation.Memory;
                }
                else if (Allocations.Buffers.LegacyAllocations.TryGetValue(
                             buffer.Handle,
                             out VulkanMemoryAllocation trackedLegacyAllocation))
                {
                    poolMemory = trackedLegacyAllocation.Memory;
                }

                if (poolMemory.Handle != 0 &&
                    Allocations.Staging.TryPublishRecycled(
                        this,
                        buffer,
                        poolMemory,
                        retired.Ticket.ResourceGeneration,
                        out _))
                {
                    pooledBuffers++;
                    continue;
                }

                if (!TryBeginDestroyResourceGeneration(
                        ObjectType.Buffer,
                        buffer.Handle,
                        retired.Ticket.ResourceGeneration,
                        "RetiredBufferDrain"))
                {
                    throw new InvalidOperationException(
                        $"Cannot destroy retired Vulkan buffer 0x{buffer.Handle:X}: " +
                        $"generation {retired.Ticket.ResourceGeneration} is no longer authoritative.");
                }

                // A buffer that is actually destroyed must not leave a stale
                // staging-pool record. Match by buffer identity so this also
                // cleans queue entries whose shared memory handle was deduped.
                Allocations.Staging.TryForget(buffer, default);

                if (Allocations.Buffers.Allocations.TryRemove(
                        buffer.Handle,
                        out VulkanMemoryAllocation allocation))
                {
                    if (TryTakeLiveBuffer(buffer))
                    {
                        telemetry.UnregisterDeviceAddressRange(buffer);
                        api.DestroyBuffer(device, buffer, null);
                        Allocations.Buffers.MemoryAllocator!.Free(
                            api,
                            device,
                            allocation);
                        CompleteSimpleResourceDestruction(
                            ObjectType.Buffer,
                            buffer.Handle);
                        destroyedBuffers++;
                        freedMemories++;
                    }
                    continue;
                }

                if (Allocations.Buffers.LegacyAllocations.TryRemove(
                        buffer.Handle,
                        out VulkanMemoryAllocation legacyAllocation))
                {
                    if (TryTakeLiveBuffer(buffer))
                    {
                        telemetry.UnregisterDeviceAddressRange(buffer);
                        api.DestroyBuffer(device, buffer, null);
                        if (legacyAllocation.Memory.Handle != 0)
                        {
                            api.FreeMemory(device, legacyAllocation.Memory, null);
                            freedMemories++;
                        }
                        CompleteSimpleResourceDestruction(
                            ObjectType.Buffer,
                            buffer.Handle);
                        destroyedBuffers++;
                    }
                    continue;
                }

                if (TryTakeLiveBuffer(buffer))
                {
                    telemetry.UnregisterDeviceAddressRange(buffer);
                    api.DestroyBuffer(device, buffer, null);
                    CompleteSimpleResourceDestruction(
                        ObjectType.Buffer,
                        buffer.Handle);
                    destroyedBuffers++;
                }
            }

            if (memory.Handle != 0 &&
                Allocations.Buffers.MemoryAllocator is VulkanLegacyAllocator)
            {
                api.FreeMemory(device, memory, null);
                freedMemories++;
            }
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            buffers: destroyedBuffers,
            bufferMemories: freedMemories);
        return pooledBuffers;
    }

    /// <summary>
    /// Drains retired images for the specified frame slot, destroying them if they are ready for retirement.
    /// </summary>
    /// <param name="api">The Vulkan API instance.</param>
    /// <param name="device">The Vulkan device.</param>
    /// <param name="frameSlot">The frame slot for which to drain retired images.</param>
    /// <param name="maxItems">The maximum number of images to drain in this call.</param>
    internal unsafe void DrainRetiredImages(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredImageResourceEntry> list =
            Lifetime.Retirement.Images[frameSlot];
        List<RetiredImageResourceEntry> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredImageResourceEntry candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket) ||
                    HasUndestroyedImageDependency(candidate.Resources))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
            }
        }

        int destroyedImages = 0;
        int freedMemories = 0;
        int destroyedViews = 0;
        int destroyedSamplers = 0;
        long destroyedImageBytes = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            RetiredImageResourceEntry entry = ready[index];
            RetiredImageResources resources = entry.Resources;
            bool canDestroyImage = resources.Image.Handle != 0 &&
                CanDestroyResourceGeneration(
                    ObjectType.Image,
                    resources.Image.Handle,
                    entry.ImageGeneration);
            bool canDestroySampler = resources.Sampler.Handle != 0 &&
                CanDestroyResourceGeneration(
                    ObjectType.Sampler,
                    resources.Sampler.Handle,
                    entry.SamplerGeneration);
            bool hasTrackedImageAllocation = false;
            VulkanMemoryAllocation trackedImageAllocation = default;
            if (canDestroyImage)
            {
                hasTrackedImageAllocation =
                    Allocations.Images.Allocations.TryRemove(
                        resources.Image.Handle,
                        out trackedImageAllocation);
                Allocations.Images.DebugInfo.TryRemove(
                    resources.Image.Handle,
                    out _);
            }

            if (canDestroySampler)
            {
                api.DestroySampler(device, resources.Sampler, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.Sampler,
                    resources.Sampler.Handle);
                Descriptors.UnregisterLiveSampler(resources.Sampler);
                destroyedSamplers++;
            }

            if (TryTakeImageViewGeneration(
                    resources.PrimaryView,
                    entry.PrimaryViewGeneration))
            {
                api.DestroyImageView(device, resources.PrimaryView, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.ImageView,
                    resources.PrimaryView.Handle);
                destroyedViews++;
            }

            if (resources.AttachmentViews is not null)
            {
                for (int viewIndex = 0;
                     viewIndex < resources.AttachmentViews.Length;
                     viewIndex++)
                {
                    ImageView view = resources.AttachmentViews[viewIndex];
                    ulong generation =
                        viewIndex < entry.AttachmentViewGenerations.Length
                            ? entry.AttachmentViewGenerations[viewIndex]
                            : 0;
                    if (!TryTakeImageViewGeneration(view, generation))
                        continue;

                    api.DestroyImageView(device, view, null);
                    CompleteSimpleResourceDestruction(
                        ObjectType.ImageView,
                        view.Handle);
                    destroyedViews++;
                }
            }

            if (canDestroyImage)
            {
                api.DestroyImage(device, resources.Image, null);
                CompleteSimpleResourceDestruction(
                    ObjectType.Image,
                    resources.Image.Handle);
                Lifetime.ImageViews.RetiringImageHandles.TryRemove(
                    resources.Image.Handle,
                    out _);
                destroyedImages++;
                if (resources.AllocatedVRAMBytes > 0)
                    destroyedImageBytes += resources.AllocatedVRAMBytes;
            }

            if (canDestroyImage &&
                hasTrackedImageAllocation &&
                trackedImageAllocation.Memory.Handle != 0)
            {
                Allocations.Buffers.MemoryAllocator!.Free(
                    api,
                    device,
                    trackedImageAllocation);
                freedMemories++;
            }

            CompleteRetiredImageDeduplication(frameSlot, in entry);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            images: destroyedImages,
            imageViews: destroyedViews,
            samplers: destroyedSamplers,
            imageMemories: freedMemories,
            imageBytes: destroyedImageBytes);
    }

    private bool CanDestroyResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration)
    {
        if (handle == 0 || expectedGeneration == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            bool forced = Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            return Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    new VulkanResourceLifetimeKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource) &&
                resource.Generation == expectedGeneration &&
                (resource.State & EVulkanResourceLifetimeState.Destroyed) == 0 &&
                (forced ||
                 (Lifetime.Tracker.IsRetirementReadyNoLock(
                      resource.RetirementTicket) &&
                  resource.Pins.IsRetirementReady(
                      Lifetime.Tracker.CompletedGraphicsSequence,
                      Lifetime.Tracker.CompletedTransferSequence,
                      Lifetime.Tracker.CompletedOtherSequence)));
        }
    }

    private bool TryTakeImageViewGeneration(
        ImageView imageView,
        ulong expectedGeneration)
    {
        if (!CanDestroyResourceGeneration(
                ObjectType.ImageView,
                imageView.Handle,
                expectedGeneration) ||
            !Lifetime.ImageViews.LiveHandles.TryRemove(imageView.Handle, out _))
        {
            return false;
        }

        Lifetime.ImageViews.DescriptorHeapCreateInfos.TryRemove(
            imageView.Handle,
            out _);
        return true;
    }

    private bool HasUndestroyedImageDependency(
        in RetiredImageResources resources)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (resources.Image.Handle != 0)
            {
                foreach ((ulong viewHandle, ulong backingImageHandle) in
                         Lifetime.Tracker.ImageViewBackingImages)
                {
                    if (backingImageHandle != resources.Image.Handle ||
                        ContainsRetiredImageView(resources, viewHandle))
                    {
                        continue;
                    }

                    if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                            new VulkanResourceLifetimeKey(
                                ObjectType.ImageView,
                                viewHandle),
                            out VulkanResourceLifetimeRecord? view) ||
                        (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    {
                        return true;
                    }
                }
            }

            foreach ((ulong framebufferHandle, VulkanResourceLifetimeKey[] attachments)
                     in Lifetime.Tracker.FramebufferAttachments)
            {
                if (Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(
                            ObjectType.Framebuffer,
                            framebufferHandle),
                        out VulkanResourceLifetimeRecord? framebuffer) &&
                    (framebuffer.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                {
                    continue;
                }

                for (int index = 0; index < attachments.Length; index++)
                {
                    VulkanResourceLifetimeKey attachment = attachments[index];
                    if (ContainsRetiredImageView(resources, attachment.Handle) ||
                        (resources.Image.Handle != 0 &&
                         Lifetime.Tracker.ImageViewBackingImages.TryGetValue(
                             attachment.Handle,
                             out ulong backingImageHandle) &&
                         backingImageHandle == resources.Image.Handle))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsRetiredImageView(
        in RetiredImageResources resources,
        ulong viewHandle)
    {
        if (viewHandle == 0)
            return false;
        if (resources.PrimaryView.Handle == viewHandle)
            return true;

        ImageView[]? attachmentViews = resources.AttachmentViews;
        if (attachmentViews is null)
            return false;
        for (int index = 0; index < attachmentViews.Length; index++)
            if (attachmentViews[index].Handle == viewHandle)
                return true;

        return false;
    }

    private void CompleteRetiredImageDeduplication(
        int frameSlot,
        in RetiredImageResourceEntry entry)
    {
        RetiredImageResources resources = entry.Resources;
        lock (Lifetime.Retirement.SyncRoot)
        {
            if (resources.Image.Handle != 0)
            {
                Lifetime.Retirement.ImageHandles[frameSlot].Remove(
                    resources.Image.Handle);
                Lifetime.Retirement.AllImageHandles.Remove(
                    resources.Image.Handle);
            }
            if (resources.Memory.Handle != 0)
            {
                Lifetime.Retirement.ImageMemoryHandles[frameSlot].Remove(
                    resources.Memory.Handle);
                Lifetime.Retirement.AllImageMemoryHandles.Remove(
                    resources.Memory.Handle);
            }
            RemoveRetiredImageViewDeduplication(
                frameSlot,
                resources.PrimaryView,
                entry.PrimaryViewGeneration);
            if (resources.AttachmentViews is not null)
            {
                for (int index = 0;
                     index < resources.AttachmentViews.Length;
                     index++)
                {
                    ulong generation =
                        index < entry.AttachmentViewGenerations.Length
                            ? entry.AttachmentViewGenerations[index]
                            : 0;
                    RemoveRetiredImageViewDeduplication(
                        frameSlot,
                        resources.AttachmentViews[index],
                        generation);
                }
            }
            if (resources.Sampler.Handle != 0)
            {
                Lifetime.Retirement.SamplerHandles[frameSlot].Remove(
                    resources.Sampler.Handle);
                Lifetime.Retirement.AllSamplerHandles.Remove(
                    resources.Sampler.Handle);
            }
        }
    }

    private void RemoveRetiredImageViewDeduplication(
        int frameSlot,
        ImageView view,
        ulong generation)
    {
        if (view.Handle == 0)
            return;

        VulkanPinnedResourceGeneration key = new(
            new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle),
            generation);
        Lifetime.Retirement.ImageViewHandles[frameSlot].Remove(key);
        Lifetime.Retirement.AllImageViewHandles.Remove(key);
    }

    private bool HasUndestroyedBufferView(Silk.NET.Vulkan.Buffer buffer)
    {
        if (buffer.Handle == 0)
            return false;

        lock (Lifetime.Tracker.SyncRoot)
        {
            foreach ((ulong viewHandle, ulong backingBufferHandle) in
                     Lifetime.Tracker.BufferViewBackingBuffers)
            {
                if (backingBufferHandle != buffer.Handle)
                    continue;

                if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(ObjectType.BufferView, viewHandle),
                        out VulkanResourceLifetimeRecord? view) ||
                    (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryTakeLiveBuffer(Silk.NET.Vulkan.Buffer buffer)
        => Allocations.Buffers.LiveHandles.TryRemove(buffer.Handle, out _);

    internal bool TryReactivateResourceAfterRetirement(
        ObjectType type,
        ulong handle,
        ulong retiredGeneration,
        string owner,
        out ulong publishedGeneration)
    {
        publishedGeneration = 0;
        if (retiredGeneration == 0)
            throw new ArgumentOutOfRangeException(
                nameof(retiredGeneration),
                "A retired Vulkan resource generation must be nonzero.");

        lock (Lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeKey key = new(type, handle);
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != retiredGeneration ||
                resource.RetirementTicket.ResourceGeneration != retiredGeneration ||
                (resource.State & EVulkanResourceLifetimeState.PendingRetirement) == 0 ||
                (resource.State & (EVulkanResourceLifetimeState.Destroyed |
                                   EVulkanResourceLifetimeState.External)) != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot recycle {key} generation {retiredGeneration}: " +
                    "the matching pending-retirement lifetime is not active.");
            }
            if (Lifetime.Tracker.ForcedRetirementDrainDepth > 0 ||
                Lifetime.Tracker.DeviceLost)
            {
                return false;
            }
            if (!Lifetime.Tracker.IsRetirementReadyNoLock(
                    resource.RetirementTicket))
            {
                throw new InvalidOperationException(
                    $"Cannot recycle {resource.Key} before its retirement completion point is reached.");
            }

            ulong nextGeneration = VulkanGeneration.IncrementNonZero(
                ref Lifetime.Tracker.ResourceGeneration);
            resource.Owner = owner;
            resource.Generation = nextGeneration;
            resource.State = EVulkanResourceLifetimeState.CpuOwned;
            resource.Pins = default;
            resource.LastSubmissionSerial = 0;
            resource.LastFrameOpContextId = 0;
            resource.LastFrameOpKind = null;
            resource.RetirementSerial = 0;
            resource.RetirementTicket = default;
            Lifetime.Tracker.PublishedResourceGenerations[key] = nextGeneration;
            publishedGeneration = nextGeneration;
            return true;
        }
    }

    internal unsafe void DrainRetiredDescriptorSets(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 64)
    {
        List<RetiredDescriptorSet> list = Lifetime.Retirement.DescriptorSets[frameSlot];
        List<RetiredDescriptorSet> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredDescriptorSet candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorSet.Handle,
                    Lifetime.Retirement.DescriptorSetHandles,
                    Lifetime.Retirement.AllDescriptorSetHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            RetiredDescriptorSet entry = ready[index];
            DescriptorSet descriptorSet = entry.DescriptorSet;
            Result result = api.FreeDescriptorSets(
                device,
                entry.DescriptorPool,
                1,
                &descriptorSet);
            if (result != Result.Success)
            {
                lock (Lifetime.Retirement.SyncRoot)
                {
                    VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                        frameSlot,
                        entry.DescriptorSet.Handle,
                        entry,
                        Lifetime.Retirement.DescriptorSets,
                        Lifetime.Retirement.DescriptorSetHandles,
                        Lifetime.Retirement.AllDescriptorSetHandles);
                }
                continue;
            }

            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorSet,
                entry.DescriptorSet.Handle);
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            descriptorSets: destroyed);
    }

    internal unsafe void DrainRetiredDescriptorPools(
        Vk api,
        Device device,
        int frameSlot,
        int maxItems = 8)
    {
        List<RetiredDescriptorPool> list = Lifetime.Retirement.DescriptorPools[frameSlot];
        List<RetiredDescriptorPool> ready = [];
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredDescriptorPool candidate = list[index];
                if (!Lifetime.Tracker.IsRetirementReady(candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorPool.Handle,
                    Lifetime.Retirement.DescriptorPoolHandles,
                    Lifetime.Retirement.AllDescriptorPoolHandles);
            }
        }

        int destroyed = 0;
        for (int index = 0; index < ready.Count; index++)
        {
            DescriptorPool pool = ready[index].DescriptorPool;
            if (pool.Handle == 0)
                continue;

            api.DestroyDescriptorPool(device, pool, null);
            CompleteSimpleResourceDestruction(
                ObjectType.DescriptorPool,
                pool.Handle);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();
            destroyed++;
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            descriptorPools: destroyed);
    }

    private void CompleteSimpleResourceDestruction(
        ObjectType type,
        ulong handle)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(type, handle);
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            bool forced = Lifetime.Tracker.ForcedRetirementDrainDepth > 0;
            if (!forced &&
                (!Lifetime.Tracker.IsRetirementReadyNoLock(
                     resource.RetirementTicket) ||
                 !resource.Pins.IsRetirementReady(
                     Lifetime.Tracker.CompletedGraphicsSequence,
                     Lifetime.Tracker.CompletedTransferSequence,
                     Lifetime.Tracker.CompletedOtherSequence)))
            {
                throw new InvalidOperationException(
                    $"Attempted to destroy {key} generation {resource.Generation} before its GPU completion point was reached.");
            }

            if (forced)
                Interlocked.Increment(ref Lifetime.Tracker.ForcedResourceDestructionCount);

            resource.State = EVulkanResourceLifetimeState.Destroyed;
            Lifetime.Tracker.ResourceCommandBufferDependencies.Remove(key);
            if (type == ObjectType.DescriptorSet)
                RemoveDescriptorSetLifetimeNoLock(handle);
            if (type == ObjectType.DescriptorPool)
                RemoveDescriptorSetsOwnedByPoolNoLock(handle);
            if (type == ObjectType.CommandBuffer &&
                Lifetime.Tracker.CommandBufferLifetimes.Remove(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? commandBufferLifetime))
            {
                ReleaseCommandBufferDependenciesNoLock(
                    handle,
                    commandBufferLifetime);
                VulkanResourceLifetimeKey poolKey =
                    commandBufferLifetime.AllocatingCommandPool;
                if (poolKey.IsValid &&
                    Lifetime.Tracker.CommandBuffersByPool.TryGetValue(
                        poolKey,
                        out HashSet<ulong>? children))
                {
                    children.Remove(handle);
                    if (children.Count == 0)
                        Lifetime.Tracker.CommandBuffersByPool.Remove(poolKey);
                }
            }
            if (type == ObjectType.CommandPool)
                Lifetime.Tracker.CommandBuffersByPool.Remove(key);
            if (type == ObjectType.ImageView)
                Lifetime.Tracker.ImageViewBackingImages.Remove(handle);
            if (type == ObjectType.BufferView)
                Lifetime.Tracker.BufferViewBackingBuffers.Remove(handle);
            if (type == ObjectType.Framebuffer)
                Lifetime.Tracker.FramebufferAttachments.Remove(handle);
        }
    }

    private void RemoveDescriptorSetsOwnedByPoolNoLock(ulong poolHandle)
        => VulkanDescriptorManager.RemoveDescriptorSetsOwnedByPoolNoLock(
            Lifetime,
            poolHandle,
            forced: false);

    private void RemoveDescriptorSetLifetimeNoLock(ulong setHandle)
        => VulkanDescriptorManager.RemoveDescriptorSetLifetimeNoLock(
            Lifetime,
            setHandle,
            forced: false);

    internal bool TryValidatePresentationSourceForReplay(
        in VulkanPresentationSourceTuple source,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!source.HasLogicalSource || source.LogicalEpoch == 0 ||
            source.Image.Handle == 0 || source.ImageView.Handle == 0 ||
            source.Sampler.Handle == 0 ||
            source.ExpectedLayout == ImageLayout.Undefined ||
            source.Width == 0 || source.Height == 0)
        {
            failureReason =
                $"final presentation source epoch {source.LogicalEpoch} is not replayable";
            return false;
        }

        ulong currentImageGeneration = GetPublishedGeneration(
            ObjectType.Image,
            source.Image.Handle);
        ulong currentImageViewGeneration = GetPublishedGeneration(
            ObjectType.ImageView,
            source.ImageView.Handle);
        ulong currentSamplerGeneration = GetPublishedGeneration(
            ObjectType.Sampler,
            source.Sampler.Handle);
        if (currentImageGeneration == source.ImageAllocationGeneration &&
            currentImageViewGeneration == source.ImageViewGeneration &&
            currentSamplerGeneration == source.SamplerGeneration)
        {
            return true;
        }

        failureReason =
            $"final presentation replay source epoch {source.LogicalEpoch} references a superseded native image generation";
        return false;
    }

    /// <summary>
    /// Mapped frame storage is created only after device and frame-slot setup. Replacing it is
    /// intentionally explicit so an old generation cannot be silently retargeted.
    /// </summary>
    internal VulkanMappedFrameArena? MappedFrameArena { get; private set; }

    internal void PublishMappedFrameArena(VulkanMappedFrameArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (MappedFrameArena is not null)
            throw new InvalidOperationException("A mapped frame arena is already published.");

        MappedFrameArena = arena;
    }

    internal VulkanMappedFrameArena? DetachMappedFrameArena()
    {
        VulkanMappedFrameArena? arena = MappedFrameArena;
        MappedFrameArena = null;
        return arena;
    }
}
