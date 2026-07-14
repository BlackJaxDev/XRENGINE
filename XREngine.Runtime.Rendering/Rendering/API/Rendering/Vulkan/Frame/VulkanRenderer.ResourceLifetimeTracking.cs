using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    [Flags]
    internal enum EVulkanResourceLifetimeState : byte
    {
        None = 0,
        CpuOwned = 1 << 0,
        Recorded = 1 << 1,
        Submitted = 1 << 2,
        Completed = 1 << 3,
        External = 1 << 4,
        PendingRetirement = 1 << 5,
        Destroyed = 1 << 6,
    }

    internal readonly record struct VulkanResourceLifetimeKey(ObjectType Type, ulong Handle)
    {
        public bool IsValid => Handle != 0;
        public override string ToString() => $"{Type}:0x{Handle:X}";
    }

    internal readonly record struct VulkanRetirementTicket(
        ulong GraphicsSequence,
        ulong TransferSequence,
        ulong OtherSequence,
        long EnqueuedTimestamp,
        ulong ResourceGeneration,
        bool ExternalOwnershipPending)
    {
        public static VulkanRetirementTicket None => default;

        public VulkanRetirementTicket Merge(in VulkanRetirementTicket other)
            => new(
                Math.Max(GraphicsSequence, other.GraphicsSequence),
                Math.Max(TransferSequence, other.TransferSequence),
                Math.Max(OtherSequence, other.OtherSequence),
                EnqueuedTimestamp == 0
                    ? other.EnqueuedTimestamp
                    : other.EnqueuedTimestamp == 0
                        ? EnqueuedTimestamp
                        : Math.Min(EnqueuedTimestamp, other.EnqueuedTimestamp),
                Math.Max(ResourceGeneration, other.ResourceGeneration),
                ExternalOwnershipPending || other.ExternalOwnershipPending);
    }

    private enum EVulkanLifetimeQueueDomain : byte
    {
        Graphics,
        Transfer,
        Other,
    }

    private sealed class VulkanResourceLifetimeRecord
    {
        public required VulkanResourceLifetimeKey Key;
        public required ulong Generation;
        public required string Owner;
        public EVulkanResourceLifetimeState State;
        public ulong LastGraphicsSequence;
        public ulong LastTransferSequence;
        public ulong LastOtherSequence;
        public ulong LastSubmissionSerial;
        public ulong LastFrameOpContextId;
        public string? LastFrameOpKind;
        public ulong RetirementSerial;
        public VulkanRetirementTicket RetirementTicket;
        public int RecordedReferenceCount;
    }

    private sealed class VulkanCommandBufferLifetimeRecord
    {
        public readonly Dictionary<VulkanResourceLifetimeKey, ulong> Dependencies = new(64);
        public readonly List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> TouchedDependencies = new(64);
        public ulong RecordingGeneration;

        public void RefreshTouchedDependencies()
        {
            TouchedDependencies.Clear();
            TouchedDependencies.EnsureCapacity(Dependencies.Count);
            foreach (KeyValuePair<VulkanResourceLifetimeKey, ulong> dependency in Dependencies)
                TouchedDependencies.Add(dependency);
        }
    }

    private readonly record struct VulkanDescriptorReferencePair(
        VulkanResourceLifetimeKey First,
        VulkanResourceLifetimeKey Second);

    private sealed class VulkanDescriptorSetLifetimeRecord
    {
        public readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorReferencePair> References = new();
        public readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorImageReference> ImageReferences = new();
        public readonly HashSet<uint> ReflectedImageBindings = [];
        public readonly HashSet<VulkanResourceLifetimeKey> IndexedReferences = [];
        public DescriptorPool Pool;
        public bool UsesUpdateAfterBind;
        public bool HasReflection;
        public ulong Generation;
    }

    private readonly record struct VulkanDescriptorImageReference(
        ImageView View,
        ImageLayout Layout,
        DescriptorType Type);

    private readonly record struct VulkanPublishedDescriptorImageReference(
        uint Binding,
        uint Element,
        VulkanDescriptorImageReference Reference);

    private sealed record VulkanPublishedDescriptorSetSnapshot(
        ulong Generation,
        VulkanResourceLifetimeKey[] References,
        VulkanPublishedDescriptorImageReference[] ImageReferences,
        uint[] ReflectedImageBindings,
        bool HasReflection);

    private readonly record struct VulkanLifetimeSubmission(
        ulong QueueHandle,
        EVulkanLifetimeQueueDomain QueueDomain,
        ulong QueueSequence,
        ulong TimelineSemaphoreHandle,
        ulong TimelineValue,
        ulong FenceHandle);

    internal readonly record struct VulkanResourceLifetimeSnapshot(
        int LiveResourceCount,
        int RecordedResourceCount,
        int SubmittedResourceCount,
        int CompletedResourceCount,
        int ExternalResourceCount,
        int PendingRetirementCount,
        int DestroyedResourceCount,
        int TrackedCommandBufferCount,
        int TrackedDescriptorSetCount,
        int InFlightSubmissionCount,
        ulong LastGraphicsSequence,
        ulong CompletedGraphicsSequence,
        ulong LastTransferSequence,
        ulong CompletedTransferSequence,
        ulong LastOtherSequence,
        ulong CompletedOtherSequence,
        long OldestPendingRetirementAgeMilliseconds,
        ulong OldestPendingRetirementGenerationAge,
        long ForcedDestructionCount,
        bool DeviceLost);

    private readonly object _vulkanResourceLifetimeLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<VulkanResourceLifetimeKey, ulong> _vulkanPublishedResourceGenerations = new();
    private readonly Dictionary<VulkanResourceLifetimeKey, VulkanResourceLifetimeRecord> _vulkanResourceLifetimes = new();
    private readonly Dictionary<ulong, VulkanCommandBufferLifetimeRecord> _vulkanCommandBufferLifetimes = new();
    private readonly Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> _vulkanResourceCommandBufferDependencies = new();
    private readonly Dictionary<ulong, VulkanDescriptorSetLifetimeRecord> _vulkanDescriptorSetLifetimes = new();
    private readonly Dictionary<ulong, HashSet<ulong>> _vulkanDescriptorSetsByPool = new();
    private readonly Dictionary<VulkanResourceLifetimeKey, HashSet<ulong>> _vulkanDescriptorSetsByReferencedResource = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, VulkanPublishedDescriptorSetSnapshot> _vulkanPublishedDescriptorSets = new();
    private readonly Dictionary<ulong, ulong> _vulkanImageViewBackingImages = new();
    private readonly Dictionary<ulong, ulong> _vulkanBufferViewBackingBuffers = new();
    private readonly Dictionary<ulong, VulkanResourceLifetimeKey[]> _vulkanFramebufferAttachments = new();
    private readonly List<VulkanLifetimeSubmission> _vulkanLifetimeSubmissions = new(16);
    private long _vulkanResourceGeneration;
    private long _vulkanRetirementSerial;
    private ulong _vulkanLastGraphicsSequence;
    private ulong _vulkanCompletedGraphicsSequence;
    private ulong _vulkanLastTransferSequence;
    private ulong _vulkanCompletedTransferSequence;
    private ulong _vulkanLastOtherSequence;
    private ulong _vulkanCompletedOtherSequence;
    private long _vulkanForcedResourceDestructionCount;
    private bool _vulkanLifetimeDeviceLost;
    private int _vulkanForcedRetirementDrainDepth;

    private static VulkanResourceLifetimeKey ResourceKey(ObjectType type, ulong handle)
        => new(type, handle);

    private ulong GetCurrentVulkanResourceGeneration(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return 0;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        return _vulkanPublishedResourceGenerations.TryGetValue(key, out ulong generation)
            ? generation
            : 0;
    }

    private void RegisterVulkanResource(
        ObjectType type,
        ulong handle,
        string owner,
        bool externallyOwned = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (_vulkanResourceLifetimeLock)
        {
            if (_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? existing))
            {
                if ((existing.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                {
                    throw new InvalidOperationException(
                        $"Vulkan handle {key} was recycled by {owner} while generation {existing.Generation} is still pending retirement.");
                }

                if ((existing.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    existing.Owner = owner;
                    _vulkanPublishedResourceGenerations[key] = existing.Generation;
                    if (externallyOwned)
                        existing.State |= EVulkanResourceLifetimeState.External;
                    return;
                }
            }

            ulong generation = unchecked((ulong)Interlocked.Increment(ref _vulkanResourceGeneration));
            _vulkanResourceLifetimes[key] = new VulkanResourceLifetimeRecord
            {
                Key = key,
                Generation = generation,
                Owner = string.IsNullOrWhiteSpace(owner) ? "<unknown>" : owner,
                State = EVulkanResourceLifetimeState.CpuOwned |
                    (externallyOwned ? EVulkanResourceLifetimeState.External : EVulkanResourceLifetimeState.None),
            };
            _vulkanPublishedResourceGenerations[key] = generation;
        }
    }

    internal void RegisterVulkanPipeline(Pipeline pipeline, string owner)
        => RegisterVulkanResource(ObjectType.Pipeline, pipeline.Handle, owner);

    private Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        Image* image,
        string owner)
    {
        Result result = Api!.CreateImage(device, ref createInfo, null, image);
        if (result == Result.Success && image is not null)
            RegisterVulkanResource(ObjectType.Image, image->Handle, owner);
        return result;
    }

    private Result CreateVulkanImageTracked(
        ref ImageCreateInfo createInfo,
        out Image image,
        string owner)
    {
        image = default;
        fixed (Image* imagePtr = &image)
            return CreateVulkanImageTracked(ref createInfo, imagePtr, owner);
    }

    private void DestroyVulkanImageImmediateTracked(Image image, string owner)
    {
        if (image.Handle == 0)
            return;

        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.Image,
            image.Handle,
            owner);
        if (!IsVulkanRetirementReady(ticket))
        {
            throw new InvalidOperationException(
                $"Cannot immediately destroy image 0x{image.Handle:X} in {owner} before its GPU completion point.");
        }

        Api!.DestroyImage(device, image, null);
        CompleteVulkanResourceDestruction(ObjectType.Image, image.Handle);
    }

    internal void RegisterVulkanFramebuffer(
        Framebuffer framebuffer,
        ReadOnlySpan<ImageView> attachments,
        string owner)
    {
        if (framebuffer.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Framebuffer, framebuffer.Handle, owner);
        VulkanResourceLifetimeKey[] attachmentKeys = new VulkanResourceLifetimeKey[attachments.Length];
        for (int i = 0; i < attachments.Length; i++)
            attachmentKeys[i] = ResourceKey(ObjectType.ImageView, attachments[i].Handle);

        lock (_vulkanResourceLifetimeLock)
            _vulkanFramebufferAttachments[framebuffer.Handle] = attachmentKeys;
    }

    private void RegisterVulkanImageViewResource(
        ImageView imageView,
        Image backingImage,
        string owner,
        bool backingImageExternallyOwned)
    {
        RegisterVulkanResource(ObjectType.ImageView, imageView.Handle, owner);
        if (backingImage.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Image, backingImage.Handle, $"{owner}.BackingImage", backingImageExternallyOwned);
        lock (_vulkanResourceLifetimeLock)
            _vulkanImageViewBackingImages[imageView.Handle] = backingImage.Handle;
    }

    private void RegisterVulkanBufferViewResource(
        BufferView bufferView,
        Silk.NET.Vulkan.Buffer backingBuffer,
        string owner)
    {
        RegisterVulkanResource(ObjectType.BufferView, bufferView.Handle, owner);
        if (backingBuffer.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.Buffer, backingBuffer.Handle, $"{owner}.BackingBuffer");
        lock (_vulkanResourceLifetimeLock)
            _vulkanBufferViewBackingBuffers[bufferView.Handle] = backingBuffer.Handle;
    }

    private VulkanResourceLifetimeRecord GetOrRegisterVulkanResource_NoLock(
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? record))
            return record;

        ulong generation = unchecked((ulong)Interlocked.Increment(ref _vulkanResourceGeneration));
        record = new VulkanResourceLifetimeRecord
        {
            Key = key,
            Generation = generation,
            Owner = owner,
            State = EVulkanResourceLifetimeState.CpuOwned,
        };
        _vulkanResourceLifetimes[key] = record;
        _vulkanPublishedResourceGenerations[key] = generation;
        return record;
    }

    private void ResetVulkanCommandBufferLifetime(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        RegisterVulkanResource(ObjectType.CommandBuffer, handle, "CommandBuffer");
        lock (_vulkanResourceLifetimeLock)
        {
            VulkanResourceLifetimeRecord commandRecord = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(ObjectType.CommandBuffer, handle),
                "CommandBuffer");
            if ((commandRecord.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                throw new InvalidOperationException(
                    $"Command buffer 0x{handle:X} cannot be reset while pending retirement.");
            }
            commandRecord.State |= EVulkanResourceLifetimeState.CpuOwned;

            if (!_vulkanCommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                _vulkanCommandBufferLifetimes[handle] = lifetime;
            }

            ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
            lifetime.RecordingGeneration++;
        }
    }

    private void RemoveVulkanCommandBufferLifetime(CommandBuffer commandBuffer, bool destroyed = false)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (_vulkanResourceLifetimeLock)
        {
            if (_vulkanCommandBufferLifetimes.Remove(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
            if (destroyed && _vulkanResourceLifetimes.TryGetValue(
                    ResourceKey(ObjectType.CommandBuffer, handle),
                    out VulkanResourceLifetimeRecord? record))
            {
                record.State = EVulkanResourceLifetimeState.Destroyed;
            }
        }
    }

    private void TrackVulkanCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (commandBuffer.Handle == 0 || handle == 0)
            return;

        if (TryRecordCommandBufferDependency(commandBuffer, type, handle))
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (_vulkanResourceLifetimeLock)
            TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, key, owner);
    }

    private void TrackVulkanCommandBufferResource_NoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (!TryTrackVulkanCommandBufferResource_NoLock(commandBufferHandle, key, owner, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    private bool TryTrackVulkanCommandBufferResource_NoLock(
        ulong commandBufferHandle,
        VulkanResourceLifetimeKey key,
        string owner,
        out string failureReason)
    {
        VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, owner);
        if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            failureReason =
                $"Command buffer 0x{commandBufferHandle:X} attempted to record retired Vulkan resource {key} generation {resource.Generation} owned by {resource.Owner}.";
            return false;
        }

        if (!_vulkanCommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
        {
            commandLifetime = new VulkanCommandBufferLifetimeRecord();
            _vulkanCommandBufferLifetimes[commandBufferHandle] = commandLifetime;
        }

        AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, resource);

        if (key.Type == ObjectType.ImageView &&
            _vulkanImageViewBackingImages.TryGetValue(key.Handle, out ulong backingImageHandle) &&
            backingImageHandle != 0)
        {
            VulkanResourceLifetimeKey imageKey = ResourceKey(ObjectType.Image, backingImageHandle);
            VulkanResourceLifetimeRecord image = GetOrRegisterVulkanResource_NoLock(imageKey, $"{owner}.BackingImage");
            if ((image.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason =
                    $"Command buffer 0x{commandBufferHandle:X} attempted to record image view {key} backed by retired image {imageKey}.";
                return false;
            }

            AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, image);
        }

        if (key.Type == ObjectType.BufferView &&
            _vulkanBufferViewBackingBuffers.TryGetValue(key.Handle, out ulong backingBufferHandle) &&
            backingBufferHandle != 0)
        {
            VulkanResourceLifetimeKey bufferKey = ResourceKey(ObjectType.Buffer, backingBufferHandle);
            VulkanResourceLifetimeRecord buffer = GetOrRegisterVulkanResource_NoLock(bufferKey, $"{owner}.BackingBuffer");
            if ((buffer.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason =
                    $"Command buffer 0x{commandBufferHandle:X} attempted to record buffer view {key} backed by retired buffer {bufferKey}.";
                return false;
            }

            AddVulkanCommandBufferDependency_NoLock(commandBufferHandle, commandLifetime, buffer);
        }

        if (key.Type == ObjectType.Framebuffer &&
            _vulkanFramebufferAttachments.TryGetValue(key.Handle, out VulkanResourceLifetimeKey[]? attachmentKeys))
        {
            for (int i = 0; i < attachmentKeys.Length; i++)
            {
                VulkanResourceLifetimeKey attachmentKey = attachmentKeys[i];
                if (attachmentKey.IsValid &&
                    !TryTrackVulkanCommandBufferResource_NoLock(
                        commandBufferHandle,
                        attachmentKey,
                        "Framebuffer.Attachment",
                        out failureReason))
                {
                    return false;
                }
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private void AddVulkanCommandBufferDependency_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord commandLifetime,
        VulkanResourceLifetimeRecord resource)
    {
        if (commandLifetime.Dependencies.TryGetValue(resource.Key, out ulong generation) &&
            generation == resource.Generation)
        {
            return;
        }

        commandLifetime.Dependencies[resource.Key] = resource.Generation;
        resource.RecordedReferenceCount++;
        resource.State |= EVulkanResourceLifetimeState.Recorded;
        if (!_vulkanResourceCommandBufferDependencies.TryGetValue(resource.Key, out HashSet<ulong>? commandBuffers))
        {
            commandBuffers = [];
            _vulkanResourceCommandBufferDependencies[resource.Key] = commandBuffers;
        }
        commandBuffers.Add(commandBufferHandle);
    }

    private void ReleaseVulkanCommandBufferDependencies_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord commandLifetime)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in commandLifetime.Dependencies)
        {
            if (!_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != generation)
            {
                continue;
            }

            resource.RecordedReferenceCount = Math.Max(0, resource.RecordedReferenceCount - 1);
            if (resource.RecordedReferenceCount == 0)
                resource.State &= ~EVulkanResourceLifetimeState.Recorded;

            if (_vulkanResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? commandBuffers))
            {
                commandBuffers.Remove(commandBufferHandle);
                if (commandBuffers.Count == 0)
                    _vulkanResourceCommandBufferDependencies.Remove(key);
            }
        }

        commandLifetime.Dependencies.Clear();
        commandLifetime.TouchedDependencies.Clear();
    }

    private void MergeVulkanSecondaryCommandBufferDependencies(
        CommandBuffer primary,
        ReadOnlySpan<CommandBuffer> secondaries)
    {
        if (primary.Handle == 0 || secondaries.Length == 0)
            return;

        ulong primaryHandle = unchecked((ulong)primary.Handle);
        FlushCommandBufferTrackingBatch(primary);
        for (int i = 0; i < secondaries.Length; i++)
            FlushCommandBufferTrackingBatch(secondaries[i]);
        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanCommandBufferLifetimes.TryGetValue(primaryHandle, out VulkanCommandBufferLifetimeRecord? primaryLifetime))
            {
                primaryLifetime = new VulkanCommandBufferLifetimeRecord();
                _vulkanCommandBufferLifetimes[primaryHandle] = primaryLifetime;
            }

            for (int i = 0; i < secondaries.Length; i++)
            {
                ulong secondaryHandle = unchecked((ulong)secondaries[i].Handle);
                if (secondaryHandle == 0)
                    continue;

                TrackVulkanCommandBufferResource_NoLock(
                    primaryHandle,
                    ResourceKey(ObjectType.CommandBuffer, secondaryHandle),
                    "CommandBuffer.SecondaryExecution");

                if (!_vulkanCommandBufferLifetimes.TryGetValue(secondaryHandle, out VulkanCommandBufferLifetimeRecord? secondaryLifetime))
                    continue;

                foreach ((VulkanResourceLifetimeKey key, ulong generation) in secondaryLifetime.Dependencies)
                {
                    if (_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) &&
                        resource.Generation == generation)
                    {
                        AddVulkanCommandBufferDependency_NoLock(primaryHandle, primaryLifetime, resource);
                    }
                }
            }

            primaryLifetime.RefreshTouchedDependencies();
        }
    }

    private void CmdExecuteCommandsTracked(
        CommandBuffer primary,
        uint commandBufferCount,
        CommandBuffer* secondaryCommandBuffers)
    {
        if (commandBufferCount == 0 || secondaryCommandBuffers is null)
            return;

        ReadOnlySpan<CommandBuffer> secondaries = new(secondaryCommandBuffers, checked((int)commandBufferCount));
        MergeVulkanSecondaryCommandBufferDependencies(primary, secondaries);
        MergeRecordedImageLayoutStates(primary, secondaries);
        Api!.CmdExecuteCommands(primary, commandBufferCount, secondaryCommandBuffers);
    }

    private void CmdBeginRenderPassTracked(
        CommandBuffer commandBuffer,
        RenderPassBeginInfo* beginInfo,
        SubpassContents contents)
    {
        if (beginInfo is not null)
        {
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.RenderPass,
                beginInfo->RenderPass.Handle,
                "RenderPass.Begin");
            TrackVulkanCommandBufferResource(
                commandBuffer,
                ObjectType.Framebuffer,
                beginInfo->Framebuffer.Handle,
                "Framebuffer.BeginRenderPass");
        }

        Api!.CmdBeginRenderPass(commandBuffer, beginInfo, contents);
    }

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        CommandBuffer* commandBuffers,
        string owner = "CommandBuffer.Allocation")
    {
        Result result = Api!.AllocateCommandBuffers(device, ref allocateInfo, commandBuffers);
        if (result != Result.Success || commandBuffers is null)
            return result;

        for (int i = 0; i < allocateInfo.CommandBufferCount; i++)
        {
            CommandBuffer commandBuffer = commandBuffers[i];
            RegisterVulkanResource(
                ObjectType.CommandBuffer,
                unchecked((ulong)commandBuffer.Handle),
                owner);
        }

        return result;
    }

    private Result AllocateVulkanCommandBuffersTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        out CommandBuffer commandBuffer,
        string owner = "CommandBuffer.Allocation")
    {
        commandBuffer = default;
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
            return AllocateVulkanCommandBuffersTracked(ref allocateInfo, commandBufferPtr, owner);
    }

    private void FreeVulkanCommandBuffersTracked(
        CommandPool commandPool,
        uint commandBufferCount,
        CommandBuffer* commandBuffers,
        string owner)
    {
        if (commandPool.Handle == 0 || commandBufferCount == 0 || commandBuffers is null)
            return;

        for (int i = 0; i < commandBufferCount; i++)
        {
            CommandBuffer commandBuffer = commandBuffers[i];
            if (commandBuffer.Handle == 0)
                continue;

            ulong handle = unchecked((ulong)commandBuffer.Handle);
            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.CommandBuffer,
                handle,
                owner);
            if (!IsVulkanRetirementReady(ticket))
            {
                RetireCommandBuffer(commandPool, commandBuffer);
                commandBuffers[i] = default;
                continue;
            }

            Api!.FreeCommandBuffers(device, commandPool, 1, &commandBuffer);
            RemoveCommandBufferBindState(commandBuffers[i]);
            CompleteVulkanResourceDestruction(ObjectType.CommandBuffer, handle);
            commandBuffers[i] = default;
        }
    }

    private void FreeVulkanCommandBufferTracked(
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
            FreeVulkanCommandBuffersTracked(commandPool, 1, commandBufferPtr, owner);
    }

    private void CmdCopyBufferTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBuffer.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, destination.Handle, "CopyBuffer.Destination");
        Api!.CmdCopyBuffer(commandBuffer, source, destination, regionCount, regions);
    }

    private void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        BufferImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBufferToImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyBufferToImage.Destination");
        Api!.CmdCopyBufferToImage(commandBuffer, source, destination, destinationLayout, regionCount, regions);
    }

    private void CmdCopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref BufferImageCopy region)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, source.Handle, "CopyBufferToImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyBufferToImage.Destination");
        Api!.CmdCopyBufferToImage(commandBuffer, source, destination, destinationLayout, regionCount, ref region);
    }

    private void CmdCopyImageToBufferTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "CopyImageToBuffer.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Buffer, destination.Handle, "CopyImageToBuffer.Destination");
        Api!.CmdCopyImageToBuffer(commandBuffer, source, sourceLayout, destination, regionCount, regions);
    }

    private void CmdCopyImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageCopy* regions)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "CopyImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "CopyImage.Destination");
        Api!.CmdCopyImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }

    private void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageBlit* regions,
        Filter filter)
    {
        if (t_excludeDesktopSwapchainBarriers &&
            (IsDesktopSwapchainImage(source) || IsDesktopSwapchainImage(destination)))
        {
            return;
        }

        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "BlitImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "BlitImage.Destination");
        Api!.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions,
            filter);
    }

    private void CmdBlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref ImageBlit region,
        Filter filter)
    {
        if (t_excludeDesktopSwapchainBarriers &&
            (IsDesktopSwapchainImage(source) || IsDesktopSwapchainImage(destination)))
        {
            return;
        }

        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, source.Handle, "BlitImage.Source");
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, destination.Handle, "BlitImage.Destination");
        Api!.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            ref region,
            filter);
    }

    private void CmdClearColorImageTracked(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout imageLayout,
        ref ClearColorValue clearValue,
        uint rangeCount,
        ref ImageSubresourceRange ranges)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, image.Handle, "ClearColorImage");
        Api!.CmdClearColorImage(
            commandBuffer,
            image,
            imageLayout,
            ref clearValue,
            rangeCount,
            ref ranges);
    }

    private void CmdClearDepthStencilImageTracked(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout imageLayout,
        ref ClearDepthStencilValue clearValue,
        uint rangeCount,
        ref ImageSubresourceRange ranges)
    {
        TrackVulkanCommandBufferResource(commandBuffer, ObjectType.Image, image.Handle, "ClearDepthStencilImage");
        Api!.CmdClearDepthStencilImage(
            commandBuffer,
            image,
            imageLayout,
            ref clearValue,
            rangeCount,
            ref ranges);
    }

    private void RegisterVulkanDescriptorSet(
        DescriptorPool pool,
        DescriptorSet descriptorSet,
        bool usesUpdateAfterBind,
        string owner,
        uint setIndex = 0,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        if (descriptorSet.Handle == 0)
            return;

        RegisterVulkanResource(ObjectType.DescriptorPool, pool.Handle, $"{owner}.Pool");
        RegisterVulkanResource(ObjectType.DescriptorSet, descriptorSet.Handle, owner);
        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanDescriptorSetLifetimes.TryGetValue(descriptorSet.Handle, out VulkanDescriptorSetLifetimeRecord? state))
            {
                state = new VulkanDescriptorSetLifetimeRecord();
                _vulkanDescriptorSetLifetimes[descriptorSet.Handle] = state;
            }

            UpdateVulkanDescriptorSetPoolIndex_NoLock(descriptorSet.Handle, state.Pool.Handle, pool.Handle);
            state.Pool = pool;
            state.UsesUpdateAfterBind = usesUpdateAfterBind;
            state.HasReflection = reflectedBindings is not null;
            state.ReflectedImageBindings.Clear();
            if (reflectedBindings is not null)
            {
                for (int i = 0; i < reflectedBindings.Count; i++)
                {
                    DescriptorBindingInfo binding = reflectedBindings[i];
                    if (binding.Set == setIndex && IsLifetimeTrackedImageDescriptorType(binding.DescriptorType))
                        state.ReflectedImageBindings.Add(binding.Binding);
                }
            }

            state.Generation++;
            PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSet.Handle, state);
        }
    }

    private void PublishVulkanDescriptorSetSnapshot_NoLock(
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state)
    {
        HashSet<VulkanResourceLifetimeKey> uniqueReferences = new();
        foreach (VulkanDescriptorReferencePair pair in state.References.Values)
        {
            if (pair.First.IsValid)
                uniqueReferences.Add(pair.First);
            if (pair.Second.IsValid)
                uniqueReferences.Add(pair.Second);
        }

        UpdateVulkanDescriptorSetReferenceIndex_NoLock(descriptorSetHandle, state, uniqueReferences);

        VulkanPublishedDescriptorImageReference[] imageReferences = state.ImageReferences.Count == 0
            ? []
            : new VulkanPublishedDescriptorImageReference[state.ImageReferences.Count];
        int imageIndex = 0;
        foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in state.ImageReferences)
            imageReferences[imageIndex++] = new VulkanPublishedDescriptorImageReference(binding, element, reference);

        _vulkanPublishedDescriptorSets[descriptorSetHandle] = new VulkanPublishedDescriptorSetSnapshot(
            state.Generation,
            uniqueReferences.Count == 0 ? [] : uniqueReferences.ToArray(),
            imageReferences,
            state.ReflectedImageBindings.Count == 0 ? [] : state.ReflectedImageBindings.ToArray(),
            state.HasReflection);
    }

    private void UpdateVulkanDescriptorSetPoolIndex_NoLock(
        ulong descriptorSetHandle,
        ulong previousPoolHandle,
        ulong poolHandle)
    {
        if (previousPoolHandle == poolHandle)
            return;

        if (previousPoolHandle != 0 &&
            _vulkanDescriptorSetsByPool.TryGetValue(previousPoolHandle, out HashSet<ulong>? previousSets))
        {
            previousSets.Remove(descriptorSetHandle);
            if (previousSets.Count == 0)
                _vulkanDescriptorSetsByPool.Remove(previousPoolHandle);
        }

        if (poolHandle == 0)
            return;

        if (!_vulkanDescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets))
        {
            ownedSets = [];
            _vulkanDescriptorSetsByPool[poolHandle] = ownedSets;
        }

        ownedSets.Add(descriptorSetHandle);
    }

    private void UpdateVulkanDescriptorSetReferenceIndex_NoLock(
        ulong descriptorSetHandle,
        VulkanDescriptorSetLifetimeRecord state,
        HashSet<VulkanResourceLifetimeKey> currentReferences)
    {
        foreach (VulkanResourceLifetimeKey previousReference in state.IndexedReferences)
        {
            if (currentReferences.Contains(previousReference) ||
                !_vulkanDescriptorSetsByReferencedResource.TryGetValue(previousReference, out HashSet<ulong>? sets))
            {
                continue;
            }

            sets.Remove(descriptorSetHandle);
            if (sets.Count == 0)
                _vulkanDescriptorSetsByReferencedResource.Remove(previousReference);
        }

        foreach (VulkanResourceLifetimeKey currentReference in currentReferences)
        {
            if (state.IndexedReferences.Contains(currentReference))
                continue;

            if (!_vulkanDescriptorSetsByReferencedResource.TryGetValue(currentReference, out HashSet<ulong>? sets))
            {
                sets = [];
                _vulkanDescriptorSetsByReferencedResource[currentReference] = sets;
            }

            sets.Add(descriptorSetHandle);
        }

        state.IndexedReferences.Clear();
        state.IndexedReferences.UnionWith(currentReferences);
    }

    private void RegisterVulkanDescriptorSets(
        DescriptorPool pool,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        bool usesUpdateAfterBind,
        string owner,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        for (int i = 0; i < descriptorSets.Length; i++)
            RegisterVulkanDescriptorSet(
                pool,
                descriptorSets[i],
                usesUpdateAfterBind,
                owner,
                unchecked((uint)i),
                reflectedBindings);
    }

    private void ValidateAndRecordVulkanDescriptorWrites(uint writeCount, WriteDescriptorSet* writes)
    {
        if (writeCount == 0 || writes is null)
            return;

        HashSet<ulong> changedSets = new();
        lock (_vulkanResourceLifetimeLock)
        {
            for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
            {
                WriteDescriptorSet write = writes[writeIndex];
                if (write.DstSet.Handle == 0)
                    continue;

                VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, write.DstSet.Handle);
                VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(setKey, "DescriptorSet.Update");
                if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                    throw new InvalidOperationException($"Cannot update retired Vulkan descriptor set {setKey}.");

                if (!_vulkanDescriptorSetLifetimes.TryGetValue(write.DstSet.Handle, out VulkanDescriptorSetLifetimeRecord? setState))
                {
                    setState = new VulkanDescriptorSetLifetimeRecord();
                    _vulkanDescriptorSetLifetimes[write.DstSet.Handle] = setState;
                }

                bool setUseCompleted = UpdateVulkanResourceCompletionState_NoLock(setResource);
                bool bindingSupportsUpdateAfterBind =
                    setState.UsesUpdateAfterBind && CanUseUpdateAfterBind(write.DescriptorType);
                if (!setUseCompleted && !bindingSupportsUpdateAfterBind)
                {
                    throw new InvalidOperationException(
                        $"Cannot update in-flight Vulkan descriptor set {setKey}; binding={write.DstBinding} type={write.DescriptorType} was not registered for update-after-bind.");
                }

                for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
                {
                    (uint Binding, uint Element) bindingKey =
                        (write.DstBinding, write.DstArrayElement + descriptorIndex);
                    VulkanDescriptorReferencePair references = ResolveDescriptorReferences(write, descriptorIndex);
                    ValidateAndPropagateVulkanDescriptorReference_NoLock(
                        setKey,
                        setResource,
                        references.First,
                        setUseCompleted);
                    ValidateAndPropagateVulkanDescriptorReference_NoLock(
                        setKey,
                        setResource,
                        references.Second,
                        setUseCompleted);
                    if (!setState.References.TryGetValue(bindingKey, out VulkanDescriptorReferencePair previousReferences) ||
                        previousReferences != references)
                    {
                        setState.References[bindingKey] = references;
                        changedSets.Add(write.DstSet.Handle);
                    }
                    if (write.PImageInfo is not null && IsLifetimeTrackedImageDescriptorType(write.DescriptorType))
                    {
                        DescriptorImageInfo imageInfo = write.PImageInfo[descriptorIndex];
                        VulkanDescriptorImageReference imageReference = new(
                            imageInfo.ImageView,
                            imageInfo.ImageLayout,
                            write.DescriptorType);
                        if (!setState.ImageReferences.TryGetValue(bindingKey, out VulkanDescriptorImageReference previousImage) ||
                            previousImage != imageReference)
                        {
                            setState.ImageReferences[bindingKey] = imageReference;
                            changedSets.Add(write.DstSet.Handle);
                        }
                    }
                    else
                    {
                        if (setState.ImageReferences.Remove(bindingKey))
                            changedSets.Add(write.DstSet.Handle);
                    }
                }
            }

            foreach (ulong descriptorSetHandle in changedSets)
            {
                VulkanDescriptorSetLifetimeRecord state = _vulkanDescriptorSetLifetimes[descriptorSetHandle];
                state.Generation++;
                PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSetHandle, state);
            }
        }
    }

    private void ValidateAndPropagateVulkanDescriptorReference_NoLock(
        VulkanResourceLifetimeKey setKey,
        VulkanResourceLifetimeRecord setResource,
        VulkanResourceLifetimeKey referenceKey,
        bool setUseCompleted)
    {
        if (!referenceKey.IsValid)
            return;

        VulkanResourceLifetimeRecord reference = GetOrRegisterVulkanResource_NoLock(
            referenceKey,
            "DescriptorSet.Reference");
        if ((reference.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot update descriptor set {setKey} with retired Vulkan resource {referenceKey} generation {reference.Generation}.");
        }

        if (!setUseCompleted)
            PropagateVulkanDescriptorSetSubmission_NoLock(setResource, reference);

    }

    private static void PropagateVulkanDescriptorSetSubmission_NoLock(
        VulkanResourceLifetimeRecord descriptorSet,
        VulkanResourceLifetimeRecord reference)
    {
        reference.LastGraphicsSequence = Math.Max(reference.LastGraphicsSequence, descriptorSet.LastGraphicsSequence);
        reference.LastTransferSequence = Math.Max(reference.LastTransferSequence, descriptorSet.LastTransferSequence);
        reference.LastOtherSequence = Math.Max(reference.LastOtherSequence, descriptorSet.LastOtherSequence);
        reference.LastSubmissionSerial = Math.Max(reference.LastSubmissionSerial, descriptorSet.LastSubmissionSerial);
        reference.LastFrameOpContextId = descriptorSet.LastFrameOpContextId;
        reference.LastFrameOpKind = descriptorSet.LastFrameOpKind;
        reference.State &= ~EVulkanResourceLifetimeState.Completed;
        reference.State |= EVulkanResourceLifetimeState.Submitted;
    }

    private static VulkanDescriptorReferencePair ResolveDescriptorReferences(
        in WriteDescriptorSet write,
        uint descriptorIndex)
    {
        switch (write.DescriptorType)
        {
            case DescriptorType.Sampler:
            case DescriptorType.CombinedImageSampler:
            case DescriptorType.SampledImage:
            case DescriptorType.StorageImage:
            case DescriptorType.InputAttachment:
                if (write.PImageInfo is not null)
                {
                    DescriptorImageInfo info = write.PImageInfo[descriptorIndex];
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.ImageView, info.ImageView.Handle),
                        ResourceKey(ObjectType.Sampler, info.Sampler.Handle));
                }
                break;

            case DescriptorType.UniformBuffer:
            case DescriptorType.StorageBuffer:
            case DescriptorType.UniformBufferDynamic:
            case DescriptorType.StorageBufferDynamic:
                if (write.PBufferInfo is not null)
                {
                    DescriptorBufferInfo info = write.PBufferInfo[descriptorIndex];
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.Buffer, info.Buffer.Handle),
                        default);
                }
                break;

            case DescriptorType.UniformTexelBuffer:
            case DescriptorType.StorageTexelBuffer:
                if (write.PTexelBufferView is not null)
                {
                    return new VulkanDescriptorReferencePair(
                        ResourceKey(ObjectType.BufferView, write.PTexelBufferView[descriptorIndex].Handle),
                        default);
                }
                break;
        }

        return default;
    }

    private static bool IsLifetimeTrackedImageDescriptorType(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    private void TrackVulkanDescriptorSetBinding(CommandBuffer commandBuffer, DescriptorSet descriptorSet)
    {
        if (commandBuffer.Handle == 0 || descriptorSet.Handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (_commandBufferTrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch) &&
            _vulkanPublishedDescriptorSets.TryGetValue(descriptorSet.Handle, out VulkanPublishedDescriptorSetSnapshot? snapshot))
        {
            batch.RecordDependency(ResourceKey(ObjectType.DescriptorSet, descriptorSet.Handle));
            bool expand = batch.MarkDescriptorExpanded(descriptorSet.Handle, snapshot.Generation);
            if (expand)
            {
                for (int i = 0; i < snapshot.References.Length; i++)
                    batch.RecordDependency(snapshot.References[i]);
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorExpansion(expand ? 0 : 1, expand ? 1 : 0);
            if (batch.MarkDescriptorValidated(descriptorSet.Handle, snapshot.Generation))
                ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, snapshot);
            return;
        }

        lock (_vulkanResourceLifetimeLock)
        {
            TrackVulkanCommandBufferResource_NoLock(
                commandBufferHandle,
                ResourceKey(ObjectType.DescriptorSet, descriptorSet.Handle),
                "DescriptorSet.Bind");

            if (!_vulkanDescriptorSetLifetimes.TryGetValue(descriptorSet.Handle, out VulkanDescriptorSetLifetimeRecord? setState) ||
                setState.References.Count == 0)
            {
                return;
            }

            foreach (VulkanDescriptorReferencePair pair in setState.References.Values)
            {
                if (pair.First.IsValid)
                    TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, pair.First, "DescriptorSet.Reference");
                if (pair.Second.IsValid)
                    TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, pair.Second, "DescriptorSet.Reference");
            }

            ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, setState);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayouts(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanPublishedDescriptorSetSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.ImageReferences.Length; i++)
        {
            VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];
            if (snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0)
                continue;

            ValidateVulkanDescriptorImageLayout(
                commandBuffer,
                descriptorSet,
                published.Binding,
                published.Element,
                published.Reference);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayouts(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        VulkanDescriptorSetLifetimeRecord setState)
    {
        foreach (((uint binding, uint element), VulkanDescriptorImageReference reference) in setState.ImageReferences)
        {
            if (setState.HasReflection && !setState.ReflectedImageBindings.Contains(binding))
                continue;

            ValidateVulkanDescriptorImageLayout(commandBuffer, descriptorSet, binding, element, reference);
        }
    }

    [Conditional("DEBUG")]
    private void ValidateVulkanDescriptorImageLayout(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint binding,
        uint element,
        VulkanDescriptorImageReference reference)
    {
        if (reference.View.Handle == 0 ||
            !TryGetDescriptorHeapImageViewCreateInfo(reference.View, out ImageViewCreateInfo viewInfo))
        {
            return;
        }

        ImageSubresourceRange range = viewInfo.SubresourceRange;
        if (!TryGetRecordedImageLayout(commandBuffer, viewInfo.Image, range, out ImageLayout trackedLayout))
            return;

        bool attachmentOrTransfer = trackedLayout is
            ImageLayout.ColorAttachmentOptimal or
            ImageLayout.DepthAttachmentOptimal or
            ImageLayout.StencilAttachmentOptimal or
            ImageLayout.DepthStencilAttachmentOptimal or
            ImageLayout.AttachmentOptimal or
            ImageLayout.TransferSrcOptimal or
            ImageLayout.TransferDstOptimal;
        bool compatible = reference.Type == DescriptorType.StorageImage
            ? trackedLayout == ImageLayout.General && reference.Layout == ImageLayout.General
            : !attachmentOrTransfer && trackedLayout == reference.Layout;
        if (compatible)
            return;

        _liveImageViewHandles.TryGetValue(reference.View.Handle, out string? imageViewOwner);
        string message =
            $"Vulkan descriptor image layout mismatch at command recording: set=0x{descriptorSet.Handle:X} " +
            $"binding={binding}[{element}] view=0x{reference.View.Handle:X} image=0x{viewInfo.Image.Handle:X} " +
            $"owner={imageViewOwner ?? "<unknown>"} descriptor={reference.Layout} tracked={trackedLayout} type={reference.Type}.";
        Debug.VulkanWarning("[Vulkan.Layout] {0}", message);
        if (RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled)
            throw new InvalidOperationException(message);
        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debug.Fail(message);
    }

    private bool ValidateVulkanSubmissionResourceLifetimes(
        ref SubmitInfo submitInfo,
        in VulkanSubmissionDiagnosticContext diagnosticContext,
        out string failureReason)
    {
        for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
        {
            CommandBuffer commandBuffer = submitInfo.PCommandBuffers[commandIndex];
            if (TryFlushCommandBufferTrackingBatch(commandBuffer, out failureReason))
                continue;

            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            if (commandBufferHandle != 0)
            {
                _ = InvalidateCachedCommandBuffersByHandle(
                    [commandBufferHandle],
                    $"submission tracking rejected: {failureReason}");
            }
            return false;
        }

        lock (_vulkanResourceLifetimeLock)
        {
            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                VulkanResourceLifetimeKey commandBufferKey = ResourceKey(ObjectType.CommandBuffer, commandBufferHandle);
                if (_vulkanResourceLifetimes.TryGetValue(commandBufferKey, out VulkanResourceLifetimeRecord? commandResource) &&
                    (commandResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    failureReason =
                        $"submission references retired commandBuffer=0x{commandBufferHandle:X} generation={commandResource.Generation}";
                    return false;
                }

                if (!_vulkanCommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
                {
                    continue;
                }

                RefreshSubmittedDescriptorDependencies_NoLock(commandBufferHandle, commandLifetime);

                foreach ((VulkanResourceLifetimeKey key, ulong recordedGeneration) in commandLifetime.TouchedDependencies)
                {
                    if (!_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    {
                        failureReason = $"commandBuffer=0x{commandBufferHandle:X} references untracked {key}";
                        return false;
                    }

                    if (resource.Generation != recordedGeneration)
                    {
                        failureReason =
                            $"resource={key} owner={resource.Owner} oldGeneration={recordedGeneration} newGeneration={resource.Generation} " +
                            $"output={diagnosticContext.OutputTargetName ?? "<unknown>"} commandBuffer=0x{commandBufferHandle:X} " +
                            $"retirementTicket={DescribeVulkanRetirementTicket(resource.RetirementTicket)} state={resource.State}";
                        return false;
                    }

                    if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                    {
                        failureReason =
                            $"resource={key} owner={resource.Owner} oldGeneration={recordedGeneration} newGeneration={resource.Generation} " +
                            $"output={diagnosticContext.OutputTargetName ?? "<unknown>"} commandBuffer=0x{commandBufferHandle:X} " +
                            $"retirementTicket={DescribeVulkanRetirementTicket(resource.RetirementTicket)} state={resource.State}";
                        return false;
                    }
                }
            }
        }

        failureReason = string.Empty;
        return true;
    }

    private static string DescribeVulkanRetirementTicket(in VulkanRetirementTicket ticket)
        => $"gfx:{ticket.GraphicsSequence}/transfer:{ticket.TransferSequence}/other:{ticket.OtherSequence}/generation:{ticket.ResourceGeneration}/external:{ticket.ExternalOwnershipPending}";

    private void RefreshSubmittedDescriptorDependencies_NoLock(
        ulong commandBufferHandle,
        VulkanCommandBufferLifetimeRecord commandLifetime)
    {
        List<KeyValuePair<VulkanResourceLifetimeKey, ulong>> touched = commandLifetime.TouchedDependencies;
        int descriptorScanCount = touched.Count;
        for (int i = 0; i < descriptorScanCount; i++)
        {
            VulkanResourceLifetimeKey key = touched[i].Key;
            if (key.Type != ObjectType.DescriptorSet ||
                !_vulkanPublishedDescriptorSets.TryGetValue(key.Handle, out VulkanPublishedDescriptorSetSnapshot? snapshot))
            {
                continue;
            }

            for (int referenceIndex = 0; referenceIndex < snapshot.References.Length; referenceIndex++)
            {
                TrackVulkanCommandBufferResource_NoLock(
                    commandBufferHandle,
                    snapshot.References[referenceIndex],
                    "DescriptorSet.SubmitSnapshot");
            }
        }

        commandLifetime.RefreshTouchedDependencies();
    }

    private VulkanLifetimeSubmission RecordSuccessfulVulkanSubmissionLifetime(
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        in VulkanSubmissionDiagnosticContext diagnosticContext)
    {
        for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            FlushCommandBufferTrackingBatch(submitInfo.PCommandBuffers[commandIndex]);

        ulong queueHandle = unchecked((ulong)queue.Handle);
        EVulkanLifetimeQueueDomain domain = ResolveVulkanLifetimeQueueDomain(queue);
        ResolveSubmissionTimelineSignal(ref submitInfo, out ulong timelineSemaphoreHandle, out ulong timelineValue);

        VulkanLifetimeSubmission submission;
        lock (_vulkanResourceLifetimeLock)
        {
            ulong queueSequence = domain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => ++_vulkanLastGraphicsSequence,
                EVulkanLifetimeQueueDomain.Transfer => ++_vulkanLastTransferSequence,
                _ => ++_vulkanLastOtherSequence,
            };

            submission = new VulkanLifetimeSubmission(
                queueHandle,
                domain,
                queueSequence,
                timelineSemaphoreHandle,
                timelineValue,
                unchecked((ulong)fence.Handle));
            _vulkanLifetimeSubmissions.Add(submission);

            for (int commandIndex = 0; commandIndex < submitInfo.CommandBufferCount; commandIndex++)
            {
                ulong commandBufferHandle = unchecked((ulong)submitInfo.PCommandBuffers[commandIndex].Handle);
                if (commandBufferHandle == 0)
                    continue;

                MarkVulkanResourceSubmitted_NoLock(
                    GetOrRegisterVulkanResource_NoLock(
                        ResourceKey(ObjectType.CommandBuffer, commandBufferHandle),
                        "CommandBuffer.Submit"),
                    domain,
                    queueSequence,
                    diagnosticContext.SubmissionSerial,
                    diagnosticContext.FrameOpContextId,
                    diagnosticContext.FrameOpKind);

                if (!_vulkanCommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? commandLifetime))
                    continue;

                foreach ((VulkanResourceLifetimeKey key, _) in commandLifetime.TouchedDependencies)
                {
                    if (_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                    {
                        MarkVulkanResourceSubmitted_NoLock(
                            resource,
                            domain,
                            queueSequence,
                            diagnosticContext.SubmissionSerial,
                            diagnosticContext.FrameOpContextId,
                            diagnosticContext.FrameOpKind);
                    }
                }
            }
        }

        LogVulkanResourceLifetimeDiagnostics(diagnosticContext.SubmissionKind ?? "submit");
        return submission;
    }

    private EVulkanLifetimeQueueDomain ResolveVulkanLifetimeQueueDomain(Queue queue)
    {
        if (queue.Handle == graphicsQueue.Handle || queue.Handle == secondaryGraphicsQueue.Handle)
            return EVulkanLifetimeQueueDomain.Graphics;
        if (queue.Handle == transferQueue.Handle)
            return transferQueue.Handle == graphicsQueue.Handle
                ? EVulkanLifetimeQueueDomain.Graphics
                : EVulkanLifetimeQueueDomain.Transfer;
        return EVulkanLifetimeQueueDomain.Other;
    }

    private static void MarkVulkanResourceSubmitted_NoLock(
        VulkanResourceLifetimeRecord resource,
        EVulkanLifetimeQueueDomain domain,
        ulong queueSequence,
        ulong submissionSerial,
        ulong frameOpContextId,
        string? frameOpKind)
    {
        resource.State &= ~EVulkanResourceLifetimeState.Completed;
        resource.State |= EVulkanResourceLifetimeState.Submitted;
        resource.LastSubmissionSerial = submissionSerial;
        resource.LastFrameOpContextId = frameOpContextId;
        resource.LastFrameOpKind = frameOpKind;
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                resource.LastGraphicsSequence = Math.Max(resource.LastGraphicsSequence, queueSequence);
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                resource.LastTransferSequence = Math.Max(resource.LastTransferSequence, queueSequence);
                break;
            default:
                resource.LastOtherSequence = Math.Max(resource.LastOtherSequence, queueSequence);
                break;
        }
    }

    private void ResolveSubmissionTimelineSignal(
        ref SubmitInfo submitInfo,
        out ulong semaphoreHandle,
        out ulong timelineValue)
    {
        semaphoreHandle = 0;
        timelineValue = 0;
        TimelineSemaphoreSubmitInfo* timelineInfo = FindTimelineSemaphoreSubmitInfo(submitInfo.PNext);
        if (timelineInfo is null ||
            timelineInfo->SignalSemaphoreValueCount == 0 ||
            timelineInfo->PSignalSemaphoreValues is null ||
            submitInfo.PSignalSemaphores is null)
        {
            return;
        }

        uint count = Math.Min(timelineInfo->SignalSemaphoreValueCount, submitInfo.SignalSemaphoreCount);
        for (uint i = 0; i < count; i++)
        {
            ulong value = timelineInfo->PSignalSemaphoreValues[i];
            Semaphore semaphore = submitInfo.PSignalSemaphores[i];
            if (value == 0 || semaphore.Handle == 0)
                continue;

            if (semaphore.Handle == _graphicsTimelineSemaphore.Handle ||
                semaphore.Handle == _transferTimelineSemaphore.Handle ||
                semaphore.Handle == _presentTimelineSemaphore.Handle)
            {
                semaphoreHandle = semaphore.Handle;
                timelineValue = value;
                return;
            }
        }
    }

    private void NotifyVulkanFenceCompleted(Fence fence)
    {
        if (fence.Handle == 0)
            return;

        ulong handle = unchecked((ulong)fence.Handle);
        lock (_vulkanResourceLifetimeLock)
        {
            for (int i = _vulkanLifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = _vulkanLifetimeSubmissions[i];
                if (submission.FenceHandle != handle)
                    continue;

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                _vulkanLifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanTimelineCompleted(Semaphore semaphore, ulong value)
    {
        if (semaphore.Handle == 0 || value == 0)
            return;

        ulong handle = semaphore.Handle;
        lock (_vulkanResourceLifetimeLock)
        {
            for (int i = _vulkanLifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = _vulkanLifetimeSubmissions[i];
                if (submission.TimelineSemaphoreHandle != handle ||
                    submission.TimelineValue == 0 ||
                    submission.TimelineValue > value)
                {
                    continue;
                }

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                _vulkanLifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanQueueIdle(Queue queue)
    {
        ulong queueHandle = unchecked((ulong)queue.Handle);
        lock (_vulkanResourceLifetimeLock)
        {
            for (int i = _vulkanLifetimeSubmissions.Count - 1; i >= 0; i--)
            {
                VulkanLifetimeSubmission submission = _vulkanLifetimeSubmissions[i];
                if (submission.QueueHandle != queueHandle)
                    continue;

                MarkVulkanQueueSequenceCompleted_NoLock(submission.QueueDomain, submission.QueueSequence);
                _vulkanLifetimeSubmissions.RemoveAt(i);
            }
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanDeviceIdle()
    {
        lock (_vulkanResourceLifetimeLock)
        {
            _vulkanCompletedGraphicsSequence = _vulkanLastGraphicsSequence;
            _vulkanCompletedTransferSequence = _vulkanLastTransferSequence;
            _vulkanCompletedOtherSequence = _vulkanLastOtherSequence;
            _vulkanLifetimeSubmissions.Clear();
        }
        AdvanceCompletedImageLayouts();
    }

    private void NotifyVulkanResourceLifetimeDeviceLost()
    {
        lock (_vulkanResourceLifetimeLock)
            _vulkanLifetimeDeviceLost = true;
    }

    private void MarkVulkanQueueSequenceCompleted_NoLock(
        EVulkanLifetimeQueueDomain domain,
        ulong queueSequence)
    {
        switch (domain)
        {
            case EVulkanLifetimeQueueDomain.Graphics:
                _vulkanCompletedGraphicsSequence = Math.Max(_vulkanCompletedGraphicsSequence, queueSequence);
                break;
            case EVulkanLifetimeQueueDomain.Transfer:
                _vulkanCompletedTransferSequence = Math.Max(_vulkanCompletedTransferSequence, queueSequence);
                break;
            default:
                _vulkanCompletedOtherSequence = Math.Max(_vulkanCompletedOtherSequence, queueSequence);
                break;
        }
    }

    private VulkanRetirementTicket CaptureVulkanRetirementTicket(
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (handle == 0)
            return VulkanRetirementTicket.None;

        if (type == ObjectType.Image)
            RetireInternedImageViewsForBackingImage(handle);

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        VulkanRetirementTicket ticket;
        ulong generation;
        string resourceOwner;
        ulong[] dependentCommandBuffers = [];
        int invalidatedDescriptorSetCount = 0;
        lock (_vulkanResourceLifetimeLock)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, owner);
            generation = resource.Generation;
            resourceOwner = resource.Owner;
            if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)
            {
                ticket = resource.RetirementTicket;
            }
            else
            {
                UpdateVulkanResourceCompletionState_NoLock(resource);
                ticket = new VulkanRetirementTicket(
                    resource.LastGraphicsSequence,
                    resource.LastTransferSequence,
                    resource.LastOtherSequence,
                    Stopwatch.GetTimestamp(),
                    resource.Generation,
                    (resource.State & EVulkanResourceLifetimeState.External) != 0);
                resource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref _vulkanRetirementSerial));
                resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                resource.RetirementTicket = ticket;
                invalidatedDescriptorSetCount = InvalidateVulkanDescriptorSetsReferencingResource_NoLock(key);
                if (_vulkanResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? dependents) &&
                    dependents.Count > 0)
                {
                    dependentCommandBuffers = new ulong[dependents.Count];
                    int dependentIndex = 0;
                    foreach (ulong commandBufferHandle in dependents)
                    {
                        if (_vulkanCommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                            lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) &&
                            recordedGeneration == generation)
                        {
                            dependentCommandBuffers[dependentIndex++] = commandBufferHandle;
                        }
                    }

                    if (dependentIndex != dependentCommandBuffers.Length)
                        Array.Resize(ref dependentCommandBuffers, dependentIndex);
                }
            }
        }

        if (dependentCommandBuffers.Length > 0)
            InvalidateCachedCommandBuffersForRetiringResource(key, generation, resourceOwner, dependentCommandBuffers);
        if (invalidatedDescriptorSetCount > 0)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourceLifetime.TargetedDescriptorInvalidation.{key.Type}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Targeted descriptor invalidation resource={0} generation={1} descriptorSets={2}.",
                key,
                generation,
                invalidatedDescriptorSetCount);
        }
        return ticket;
    }

    private int InvalidateVulkanDescriptorSetsReferencingResource_NoLock(VulkanResourceLifetimeKey key)
    {
        if (!_vulkanDescriptorSetsByReferencedResource.TryGetValue(key, out HashSet<ulong>? descriptorSets) ||
            descriptorSets.Count == 0)
        {
            return 0;
        }

        int invalidated = 0;
        foreach (ulong descriptorSetHandle in descriptorSets)
        {
            if (!_vulkanDescriptorSetLifetimes.TryGetValue(
                    descriptorSetHandle,
                    out VulkanDescriptorSetLifetimeRecord? state))
            {
                continue;
            }

            state.Generation++;
            PublishVulkanDescriptorSetSnapshot_NoLock(descriptorSetHandle, state);
            invalidated++;
        }

        return invalidated;
    }

    private void InvalidateCachedCommandBuffersForRetiringResource(
        VulkanResourceLifetimeKey key,
        ulong generation,
        string resourceOwner,
        ReadOnlySpan<ulong> dependentCommandBuffers)
    {
        VulkanExactInvalidationResult result = InvalidateCachedCommandBuffersByHandle(
            dependentCommandBuffers,
            $"retiring {key} generation {generation}");
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
            result.ExactVariantsDirtied,
            result.ExactCommandChainsDirtied,
            result.UnrelatedVariantsPreserved,
            result.GlobalFallbackInvalidations);
        Debug.VulkanEvery(
            $"Vulkan.ResourceLifetime.RetirementInvalidation.{key.Type}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] Exact retirement invalidation resource={0} generation={1} owner={2} dependentCommandBuffers={3} variantsDirtied={4} chainsDirtied={5} unrelatedVariantsPreserved={6} globalFallbacks={7}.",
            key,
            generation,
            resourceOwner,
            dependentCommandBuffers.Length,
            result.ExactVariantsDirtied,
            result.ExactCommandChainsDirtied,
            result.UnrelatedVariantsPreserved,
            result.GlobalFallbackInvalidations);
    }

    private VulkanRetirementTicket CaptureVulkanRetirementWatermark()
    {
        lock (_vulkanResourceLifetimeLock)
        {
            return new VulkanRetirementTicket(
                _vulkanLastGraphicsSequence,
                _vulkanLastTransferSequence,
                _vulkanLastOtherSequence,
                Stopwatch.GetTimestamp(),
                0,
                false);
        }
    }

    private VulkanRetirementTicket CaptureVulkanDescriptorPoolRetirementTicket(
        DescriptorPool pool,
        string owner)
    {
        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.DescriptorPool,
            pool.Handle,
            owner);
        if (pool.Handle == 0)
            return ticket;

        ulong poolGeneration = ticket.ResourceGeneration;
        HashSet<ulong>? dependentCommandBuffers = null;
        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanDescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? ownedSets) ||
                ownedSets.Count == 0)
            {
                return ticket;
            }

            long enqueuedTimestamp = Stopwatch.GetTimestamp();
            foreach (ulong setHandle in ownedSets)
            {
                VulkanResourceLifetimeKey setKey = ResourceKey(ObjectType.DescriptorSet, setHandle);
                VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(
                    setKey,
                    $"{owner}.DescriptorSet");
                VulkanRetirementTicket setTicket;
                if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    setTicket = setResource.RetirementTicket;
                }
                else
                {
                    UpdateVulkanResourceCompletionState_NoLock(setResource);
                    setTicket = new VulkanRetirementTicket(
                        setResource.LastGraphicsSequence,
                        setResource.LastTransferSequence,
                        setResource.LastOtherSequence,
                        enqueuedTimestamp,
                        setResource.Generation,
                        false);
                    setResource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref _vulkanRetirementSerial));
                    setResource.State |= EVulkanResourceLifetimeState.PendingRetirement;
                    setResource.RetirementTicket = setTicket;

                    if (_vulkanResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
                    {
                        foreach (ulong commandBufferHandle in dependents)
                        {
                            if (_vulkanCommandBufferLifetimes.TryGetValue(commandBufferHandle, out VulkanCommandBufferLifetimeRecord? lifetime) &&
                                lifetime.Dependencies.TryGetValue(setKey, out ulong recordedGeneration) &&
                                recordedGeneration == setResource.Generation)
                            {
                                (dependentCommandBuffers ??= []).Add(commandBufferHandle);
                            }
                        }
                    }
                }

                ticket = ticket.Merge(setTicket);
            }
        }

        // One aggregate exact invalidation avoids both global cache teardown and an
        // invalidation/logging pass for every descriptor set in a large pool.
        if (dependentCommandBuffers is { Count: > 0 })
        {
            ulong[] handles = [.. dependentCommandBuffers];
            InvalidateCachedCommandBuffersForRetiringResource(
                ResourceKey(ObjectType.DescriptorPool, pool.Handle),
                poolGeneration,
                owner,
                handles);
        }

        return ticket;
    }

    private bool IsVulkanRetirementReady(in VulkanRetirementTicket ticket)
    {
        lock (_vulkanResourceLifetimeLock)
        {
            if (_vulkanForcedRetirementDrainDepth > 0)
                return true;
            if (_vulkanLifetimeDeviceLost)
                return false;

            return !ticket.ExternalOwnershipPending &&
                ticket.GraphicsSequence <= _vulkanCompletedGraphicsSequence &&
                ticket.TransferSequence <= _vulkanCompletedTransferSequence &&
                ticket.OtherSequence <= _vulkanCompletedOtherSequence;
        }
    }

    private bool TryBeginDestroyVulkanResourceGeneration(
        ObjectType type,
        ulong handle,
        ulong expectedGeneration,
        string owner)
    {
        if (handle == 0 || expectedGeneration == 0)
            return false;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource) ||
                resource.Generation != expectedGeneration ||
                (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0 ||
                !IsVulkanRetirementReady_NoLock(resource.RetirementTicket))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourceLifetime.SkipStaleDestroy.{type}.{handle}.{expectedGeneration}.{owner}",
                    TimeSpan.FromSeconds(5),
                    "[Vulkan.ResourceLifetime] Skipping stale or premature destroy: resource={0} expectedGeneration={1} currentGeneration={2} state={3} owner={4}.",
                    key,
                    expectedGeneration,
                    resource?.Generation ?? 0UL,
                    resource?.State ?? EVulkanResourceLifetimeState.None,
                    owner);
                return false;
            }

            return true;
        }
    }

    private bool HasUndestroyedVulkanBufferViewReference(Silk.NET.Vulkan.Buffer buffer)
    {
        if (buffer.Handle == 0)
            return false;

        lock (_vulkanResourceLifetimeLock)
        {
            foreach ((ulong viewHandle, ulong backingBufferHandle) in _vulkanBufferViewBackingBuffers)
            {
                if (backingBufferHandle != buffer.Handle)
                    continue;

                if (!_vulkanResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.BufferView, viewHandle),
                        out VulkanResourceLifetimeRecord? view) ||
                    (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasUndestroyedVulkanImageDependency(in RetiredImageResources resources)
    {
        if (resources.Image.Handle == 0 &&
            resources.PrimaryView.Handle == 0 &&
            (resources.AttachmentViews is null || resources.AttachmentViews.Length == 0))
        {
            return false;
        }

        lock (_vulkanResourceLifetimeLock)
        {
            if (resources.Image.Handle != 0)
            {
                foreach ((ulong viewHandle, ulong backingImageHandle) in _vulkanImageViewBackingImages)
                {
                    if (backingImageHandle != resources.Image.Handle ||
                        ContainsRetiredImageView(resources, viewHandle))
                    {
                        continue;
                    }

                    if (!_vulkanResourceLifetimes.TryGetValue(
                            ResourceKey(ObjectType.ImageView, viewHandle),
                            out VulkanResourceLifetimeRecord? view) ||
                        (view.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    {
                        return true;
                    }
                }
            }

            foreach ((ulong framebufferHandle, VulkanResourceLifetimeKey[] attachments) in _vulkanFramebufferAttachments)
            {
                if (_vulkanResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.Framebuffer, framebufferHandle),
                        out VulkanResourceLifetimeRecord? framebuffer) &&
                    (framebuffer.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                {
                    continue;
                }

                for (int i = 0; i < attachments.Length; i++)
                {
                    VulkanResourceLifetimeKey attachment = attachments[i];
                    if (ContainsRetiredImageView(resources, attachment.Handle) ||
                        (resources.Image.Handle != 0 &&
                         _vulkanImageViewBackingImages.TryGetValue(attachment.Handle, out ulong backingImageHandle) &&
                         backingImageHandle == resources.Image.Handle))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsRetiredImageView(in RetiredImageResources resources, ulong viewHandle)
    {
        if (viewHandle == 0)
            return false;
        if (resources.PrimaryView.Handle == viewHandle)
            return true;

        ImageView[]? attachmentViews = resources.AttachmentViews;
        if (attachmentViews is null)
            return false;
        for (int i = 0; i < attachmentViews.Length; i++)
        {
            if (attachmentViews[i].Handle == viewHandle)
                return true;
        }

        return false;
    }

    private bool UpdateVulkanResourceCompletionState_NoLock(VulkanResourceLifetimeRecord resource)
    {
        bool completed = resource.LastGraphicsSequence <= _vulkanCompletedGraphicsSequence &&
            resource.LastTransferSequence <= _vulkanCompletedTransferSequence &&
            resource.LastOtherSequence <= _vulkanCompletedOtherSequence;
        if (!completed)
            return false;

        if ((resource.State & EVulkanResourceLifetimeState.Submitted) != 0)
        {
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }

        return true;
    }

    private void EnsureVulkanResourceMutationAllowed(
        ObjectType type,
        ulong handle,
        string operation,
        bool allowWhileInFlight = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (_vulkanResourceLifetimeLock)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(key, operation);
            if ((resource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
                throw new InvalidOperationException($"Cannot perform {operation} on retired Vulkan resource {key}.");

            if (!allowWhileInFlight && !UpdateVulkanResourceCompletionState_NoLock(resource))
            {
                throw new InvalidOperationException(
                    $"Cannot perform {operation} on in-flight Vulkan resource {key}; graphics={resource.LastGraphicsSequence}/{_vulkanCompletedGraphicsSequence} transfer={resource.LastTransferSequence}/{_vulkanCompletedTransferSequence} other={resource.LastOtherSequence}/{_vulkanCompletedOtherSequence}.");
            }
        }
    }

    private void NotifyVulkanResourceUseCompleted(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanResourceLifetimes.TryGetValue(
                    ResourceKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.LastGraphicsSequence = 0;
            resource.LastTransferSequence = 0;
            resource.LastOtherSequence = 0;
            resource.State &= ~EVulkanResourceLifetimeState.Submitted;
            resource.State |= EVulkanResourceLifetimeState.Completed;
        }
    }

    private bool CanMutateVulkanDescriptorPool(DescriptorPool pool, out string reason)
    {
        if (pool.Handle == 0)
        {
            reason = "descriptor pool handle is null";
            return false;
        }

        lock (_vulkanResourceLifetimeLock)
        {
            VulkanResourceLifetimeRecord poolResource = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(ObjectType.DescriptorPool, pool.Handle),
                "DescriptorPool.Mutation");
            if ((poolResource.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                reason = $"descriptor pool 0x{pool.Handle:X} is retired";
                return false;
            }

            if (!_vulkanDescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? ownedSets))
                ownedSets = [];

            foreach (ulong setHandle in ownedSets)
            {
                VulkanResourceLifetimeRecord setResource = GetOrRegisterVulkanResource_NoLock(
                    ResourceKey(ObjectType.DescriptorSet, setHandle),
                    "DescriptorPool.Mutation.Set");
                if (!UpdateVulkanResourceCompletionState_NoLock(setResource))
                {
                    reason =
                        $"descriptor set 0x{setHandle:X} is in flight at graphics={setResource.LastGraphicsSequence}/{_vulkanCompletedGraphicsSequence} transfer={setResource.LastTransferSequence}/{_vulkanCompletedTransferSequence} other={setResource.LastOtherSequence}/{_vulkanCompletedOtherSequence}";
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private Result ResetVulkanDescriptorPoolTracked(DescriptorPool pool)
    {
        if (!CanMutateVulkanDescriptorPool(pool, out string reason))
        {
            Debug.VulkanEvery(
                $"Vulkan.DescriptorPool.ResetDeferred.{GetHashCode()}.{pool.Handle}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Descriptor-pool reset deferred: pool=0x{0:X} reason={1}.",
                pool.Handle,
                reason);
            return Result.NotReady;
        }

        Result result = Api!.ResetDescriptorPool(device, pool, 0);
        if (result != Result.Success)
            return result;

        lock (_vulkanResourceLifetimeLock)
            RemoveDescriptorSetsOwnedByPool_NoLock(pool.Handle, forced: false);
        return result;
    }

    private void CompleteVulkanResourceDestruction(
        ObjectType type,
        ulong handle,
        bool forced = false)
    {
        if (handle == 0)
            return;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        lock (_vulkanResourceLifetimeLock)
        {
            forced |= _vulkanForcedRetirementDrainDepth > 0;
            if (!_vulkanResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? resource))
                return;

            if (!forced && !IsVulkanRetirementReady_NoLock(resource.RetirementTicket))
            {
                throw new InvalidOperationException(
                    $"Attempted to destroy {key} generation {resource.Generation} before its GPU completion point was reached.");
            }

            if (forced)
                Interlocked.Increment(ref _vulkanForcedResourceDestructionCount);

            resource.State = EVulkanResourceLifetimeState.Destroyed;
            _vulkanResourceCommandBufferDependencies.Remove(key);
            if (type == ObjectType.ImageView)
                _vulkanImageViewBackingImages.Remove(handle);
            if (type == ObjectType.BufferView)
                _vulkanBufferViewBackingBuffers.Remove(handle);
            if (type == ObjectType.DescriptorSet)
            {
                RemoveDescriptorSetLifetime_NoLock(handle, forced);
                _vulkanPublishedDescriptorSets.TryRemove(handle, out _);
            }
            if (type == ObjectType.CommandBuffer)
            {
                if (_vulkanCommandBufferLifetimes.Remove(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
                    ReleaseVulkanCommandBufferDependencies_NoLock(handle, lifetime);
            }
            if (type == ObjectType.Framebuffer)
                _vulkanFramebufferAttachments.Remove(handle);
            if (type == ObjectType.DescriptorPool)
                RemoveDescriptorSetsOwnedByPool_NoLock(handle, forced);
        }
    }

    private bool IsVulkanRetirementReady_NoLock(in VulkanRetirementTicket ticket)
        => _vulkanForcedRetirementDrainDepth > 0 ||
           (!_vulkanLifetimeDeviceLost &&
           !ticket.ExternalOwnershipPending &&
           ticket.GraphicsSequence <= _vulkanCompletedGraphicsSequence &&
           ticket.TransferSequence <= _vulkanCompletedTransferSequence &&
           ticket.OtherSequence <= _vulkanCompletedOtherSequence);

    private void BeginForcedVulkanRetirementDrain()
    {
        lock (_vulkanResourceLifetimeLock)
            _vulkanForcedRetirementDrainDepth++;
    }

    private void EndForcedVulkanRetirementDrain()
    {
        lock (_vulkanResourceLifetimeLock)
            _vulkanForcedRetirementDrainDepth = Math.Max(0, _vulkanForcedRetirementDrainDepth - 1);
    }

    private void RemoveDescriptorSetsOwnedByPool_NoLock(ulong poolHandle, bool forced)
    {
        if (!_vulkanDescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? ownedSets) ||
            ownedSets.Count == 0)
        {
            _vulkanDescriptorSetsByPool.Remove(poolHandle);
            return;
        }

        ulong[] removedSets = [.. ownedSets];
        for (int i = 0; i < removedSets.Length; i++)
            RemoveDescriptorSetLifetime_NoLock(removedSets[i], forced);

        _vulkanDescriptorSetsByPool.Remove(poolHandle);
    }

    private void RemoveDescriptorSetLifetime_NoLock(ulong setHandle, bool forced)
    {
        if (_vulkanDescriptorSetLifetimes.Remove(setHandle, out VulkanDescriptorSetLifetimeRecord? state))
        {
            UpdateVulkanDescriptorSetPoolIndex_NoLock(setHandle, state.Pool.Handle, 0);
            foreach (VulkanResourceLifetimeKey reference in state.IndexedReferences)
            {
                if (!_vulkanDescriptorSetsByReferencedResource.TryGetValue(reference, out HashSet<ulong>? sets))
                    continue;

                sets.Remove(setHandle);
                if (sets.Count == 0)
                    _vulkanDescriptorSetsByReferencedResource.Remove(reference);
            }
        }

        _vulkanPublishedDescriptorSets.TryRemove(setHandle, out _);
        if (!_vulkanResourceLifetimes.TryGetValue(
                ResourceKey(ObjectType.DescriptorSet, setHandle),
                out VulkanResourceLifetimeRecord? setResource))
        {
            return;
        }

        setResource.State = EVulkanResourceLifetimeState.Destroyed;
        if (forced)
            Interlocked.Increment(ref _vulkanForcedResourceDestructionCount);
    }

    private void ReleaseExternalVulkanResourceOwnership(ObjectType type, ulong handle)
    {
        if (handle == 0)
            return;

        lock (_vulkanResourceLifetimeLock)
        {
            if (!_vulkanResourceLifetimes.TryGetValue(
                    ResourceKey(type, handle),
                    out VulkanResourceLifetimeRecord? resource))
            {
                return;
            }

            resource.State &= ~EVulkanResourceLifetimeState.External;
            resource.RetirementTicket = resource.RetirementTicket with { ExternalOwnershipPending = false };
        }
    }

    private void ReactivateVulkanResourceAfterRetirement(
        ObjectType type,
        ulong handle,
        string owner)
    {
        if (handle == 0)
            return;

        lock (_vulkanResourceLifetimeLock)
        {
            VulkanResourceLifetimeRecord resource = GetOrRegisterVulkanResource_NoLock(
                ResourceKey(type, handle),
                owner);
            if (!IsVulkanRetirementReady_NoLock(resource.RetirementTicket))
            {
                throw new InvalidOperationException(
                    $"Cannot recycle {resource.Key} before its retirement completion point is reached.");
            }

            resource.Owner = owner;
            resource.State = EVulkanResourceLifetimeState.CpuOwned;
            resource.LastGraphicsSequence = 0;
            resource.LastTransferSequence = 0;
            resource.LastOtherSequence = 0;
            resource.RetirementSerial = 0;
            resource.RetirementTicket = default;
        }
    }

    internal VulkanResourceLifetimeSnapshot GetVulkanResourceLifetimeSnapshot()
    {
        lock (_vulkanResourceLifetimeLock)
        {
            int live = 0;
            int recorded = 0;
            int submitted = 0;
            int completed = 0;
            int external = 0;
            int pending = 0;
            int destroyed = 0;
            long oldestTimestamp = 0;
            ulong oldestRetirementSerial = 0;

            foreach (VulkanResourceLifetimeRecord resource in _vulkanResourceLifetimes.Values)
            {
                UpdateVulkanResourceCompletionState_NoLock(resource);
                EVulkanResourceLifetimeState state = resource.State;
                if ((state & EVulkanResourceLifetimeState.Destroyed) != 0)
                    destroyed++;
                else
                    live++;
                if ((state & EVulkanResourceLifetimeState.Recorded) != 0)
                    recorded++;
                if ((state & EVulkanResourceLifetimeState.Submitted) != 0)
                    submitted++;
                if ((state & EVulkanResourceLifetimeState.Completed) != 0)
                    completed++;
                if ((state & EVulkanResourceLifetimeState.External) != 0)
                    external++;
                if ((state & EVulkanResourceLifetimeState.PendingRetirement) != 0)
                {
                    pending++;
                    long timestamp = resource.RetirementTicket.EnqueuedTimestamp;
                    if (timestamp != 0 && (oldestTimestamp == 0 || timestamp < oldestTimestamp))
                        oldestTimestamp = timestamp;
                    if (resource.RetirementSerial != 0 &&
                        (oldestRetirementSerial == 0 || resource.RetirementSerial < oldestRetirementSerial))
                    {
                        oldestRetirementSerial = resource.RetirementSerial;
                    }
                }
            }

            long oldestAgeMilliseconds = oldestTimestamp == 0
                ? 0
                : (long)Math.Max(0, Stopwatch.GetElapsedTime(oldestTimestamp).TotalMilliseconds);
            ulong latestRetirementSerial = unchecked((ulong)Math.Max(0, Volatile.Read(ref _vulkanRetirementSerial)));
            ulong oldestGenerationAge = oldestRetirementSerial == 0
                ? 0
                : latestRetirementSerial - oldestRetirementSerial + 1;
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResourceLifetimeGauges(
                live,
                _vulkanDescriptorSetLifetimes.Count);
            return new VulkanResourceLifetimeSnapshot(
                live,
                recorded,
                submitted,
                completed,
                external,
                pending,
                destroyed,
                _vulkanCommandBufferLifetimes.Count,
                _vulkanDescriptorSetLifetimes.Count,
                _vulkanLifetimeSubmissions.Count,
                _vulkanLastGraphicsSequence,
                _vulkanCompletedGraphicsSequence,
                _vulkanLastTransferSequence,
                _vulkanCompletedTransferSequence,
                _vulkanLastOtherSequence,
                _vulkanCompletedOtherSequence,
                oldestAgeMilliseconds,
                oldestGenerationAge,
                Volatile.Read(ref _vulkanForcedResourceDestructionCount),
                _vulkanLifetimeDeviceLost);
        }
    }

    private void LogVulkanResourceLifetimeDiagnostics(string reason)
    {
        VulkanResourceLifetimeSnapshot snapshot = GetVulkanResourceLifetimeSnapshot();
        if (snapshot.PendingRetirementCount == 0 && snapshot.InFlightSubmissionCount == 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.ResourceLifetime.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] reason={0} live={1} descriptorSets={2} commandBuffers={3} recorded={4} submitted={5} completed={6} external={7} retirementQueueDepth={8} inFlightSubmissions={9} oldestRetirementMs={10} oldestRetirementGenerationAge={11} graphics={12}/{13} transfer={14}/{15} other={16}/{17} forced={18} deviceLost={19}.",
            reason,
            snapshot.LiveResourceCount,
            snapshot.TrackedDescriptorSetCount,
            snapshot.TrackedCommandBufferCount,
            snapshot.RecordedResourceCount,
            snapshot.SubmittedResourceCount,
            snapshot.CompletedResourceCount,
            snapshot.ExternalResourceCount,
            snapshot.PendingRetirementCount,
            snapshot.InFlightSubmissionCount,
            snapshot.OldestPendingRetirementAgeMilliseconds,
            snapshot.OldestPendingRetirementGenerationAge,
            snapshot.CompletedGraphicsSequence,
            snapshot.LastGraphicsSequence,
            snapshot.CompletedTransferSequence,
            snapshot.LastTransferSequence,
            snapshot.CompletedOtherSequence,
            snapshot.LastOtherSequence,
            snapshot.ForcedDestructionCount,
            snapshot.DeviceLost);
    }
}
