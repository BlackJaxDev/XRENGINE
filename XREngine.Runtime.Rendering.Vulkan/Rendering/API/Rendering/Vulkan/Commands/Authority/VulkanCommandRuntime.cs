using System.Runtime.CompilerServices;
using System.Threading;
using System.Diagnostics;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Renderer-independent owner for command recording admission and persistent
/// schedule artifacts. Frame-operation ordering is delegated to the canonical
/// <see cref="VulkanFrameOperationScheduler"/>. Native command execution remains
/// supplied by the facade at the call boundary.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private VulkanDeviceContext? _configuredDeviceContext;
    private VulkanResourceRuntime? _configuredResourceRuntime;
    private VulkanFrameTelemetry? _configuredFrameTelemetry;
    private VulkanQueryCommandService? _queryCommandService;
    private VulkanRetirementDependencyPublicationPort? _retirementDependencyPublications;
    private CommandChainSchedule?[]? _scheduleCache;
    private readonly VulkanCommandThreadWorkspace _threadWorkspace;
    private readonly VulkanFrameOperationScheduler _primaryOperationScheduler = new();

    internal VulkanProducerCompleteIndirectStream? PendingProducerCompleteIndirectStream { get; set; }
    internal bool ThreadLocalScratchDisposed { get; set; }

    public VulkanCommandRecorder Recorder { get; } = new();
    public VulkanCommandWorkerSynchronization Workers { get; } = new();
    public VulkanCommandPoolAuthority Pools { get; } = new();
    public VulkanCommandChainState CommandChains { get; } = new();
    public VulkanCommandBufferState CommandBuffers { get; } = new();
    public VulkanStateTracker StateTracker { get; } = new();
    public VulkanCommandSynchronizationState Synchronization { get; } = new();
    public VulkanDynamicUiBatchTextOverlayRecorder DynamicUiOverlayRecorder { get; } = new();
    public VulkanOpenXrCommandRecordingService OpenXrRecording { get; } = new();
    public VulkanOpenXrEyeWorkerCommandService OpenXrEyeWorkers { get; } = new();

    internal VulkanCommandRuntime()
        => _threadWorkspace = new VulkanCommandThreadWorkspace(this);

    internal void InitializeSynchronizationBackend(bool supportsSynchronization2)
    {
        EVulkanSynchronizationBackend requested = RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend;
        Synchronization._activeSynchronizationBackend = requested == EVulkanSynchronizationBackend.Sync2 && supportsSynchronization2
            ? EVulkanSynchronizationBackend.Sync2
            : EVulkanSynchronizationBackend.Legacy;
        if (requested == EVulkanSynchronizationBackend.Sync2 && !supportsSynchronization2)
            Debug.VulkanWarning("[Vulkan] SyncBackend requested Sync2, but synchronization2 is unavailable. Falling back to legacy submit/barrier path.");
        Debug.Vulkan("[Vulkan] Synchronization backend initialized: {0}", Synchronization._activeSynchronizationBackend);
    }

    internal void ReleaseCurrentThreadSynchronizationScratch()
        => Synchronization._synchronizationThreadWorkspace.ReleaseCurrentThread();

    internal string DescribeVulkanQueueOperationTail(int maxEntries = 8)
    {
        using (VulkanFrameLockScope.Enter(
                   CommandBuffers.OneTimeSubmitGate,
                   EVulkanFrameWaitReason.QueueLeaseLock))
        {
            long latest = Volatile.Read(ref Synchronization._vulkanQueueOperationSerial);
            if (latest <= 0)
                return string.Empty;
            int available = (int)Math.Min(latest, 64);
            int emitted = 0;
            StringBuilder builder = new("QueueOperationTail");
            for (long serial = latest; serial > 0 && emitted < maxEntries && latest - serial < available; serial--)
            {
                VulkanQueueOperationRecord operation = Synchronization._vulkanQueueOperationHistory[
                    unchecked((int)((serial - 1) % 64))];
                if (operation.Serial != unchecked((ulong)serial))
                    continue;
                builder.Append(" [#").Append(operation.Serial).Append(' ').Append(operation.Operation)
                    .Append(" queue=0x").Append(operation.QueueHandle.ToString("X"))
                    .Append(" result=").Append(operation.Result).Append(" state=").Append(operation.DeviceState)
                    .Append(" submit=").Append(operation.SubmissionSerial).Append(" thread=").Append(operation.ThreadId)
                    .Append(" caller=").Append(operation.Caller ?? "<unknown>").Append(']');
                emitted++;
            }
            return emitted == 0 ? string.Empty : builder.ToString();
        }
    }

    /// <summary>Gets this runtime's explicitly typed command-thread workspace.</summary>
    internal VulkanCommandThreadWorkspace ThreadWorkspace => _threadWorkspace;

    /// <summary>
    /// Publishes the stable authorities used by native command encoding. Output
    /// and planner authorities are deliberately absent: their frame-local facts
    /// arrive only through frozen prepared inputs.
    /// </summary>
    internal void ConfigurePrimaryRecording(
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(resourceRuntime);
        ArgumentNullException.ThrowIfNull(telemetry);

        if (_configuredDeviceContext is not null &&
            (!ReferenceEquals(_configuredDeviceContext, deviceContext) ||
             !ReferenceEquals(_configuredResourceRuntime, resourceRuntime) ||
             !ReferenceEquals(_configuredFrameTelemetry, telemetry)))
        {
            throw new InvalidOperationException(
                "The Vulkan command runtime cannot be rebound to different authorities.");
        }

        _configuredDeviceContext = deviceContext;
        CommandBuffers.DeviceQueueAdmissionGate = deviceContext.QueueAdmissionGate;
        _configuredResourceRuntime = resourceRuntime;
        _configuredFrameTelemetry = telemetry;
        resourceRuntime.Lifetime.ConfigureRetirementDependencyPublications(
            _retirementDependencyPublications ??=
                new VulkanRetirementDependencyPublicationPort(this));
        resourceRuntime.Images.ConfigureCommandRuntime(this);
        _queryCommandService ??= new VulkanQueryCommandService(this);
        resourceRuntime.Queries.BindCommands(_queryCommandService);
        OpenXrRecording.Configure(this, resourceRuntime, deviceContext);
        OpenXrEyeWorkers.Configure(this, OpenXrRecording, deviceContext);
    }

    internal VulkanDeviceContext DeviceContext
        => _configuredDeviceContext ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    internal VulkanResourceRuntime ResourceRuntime
        => _configuredResourceRuntime ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    internal VulkanFrameTelemetry FrameTelemetry
        => _configuredFrameTelemetry ?? throw new InvalidOperationException(
            "The Vulkan command runtime has not been configured for primary recording.");

    internal bool IsDeviceOperational => DeviceContext.IsOperational;

    /// <summary>
    /// Resolves the planner generation for the current command scope. The
    /// planner publication reader receives only the returned immutable value;
    /// it never retains this runtime's thread workspace.
    /// </summary>
    internal ResourcePlannerRuntimeGeneration ResolveResourcePlannerRuntimeGeneration(
        ResourcePlannerRuntimeGeneration publishedGeneration)
    {
        ArgumentNullException.ThrowIfNull(publishedGeneration);
        if (!ThreadWorkspace.TryGetCurrent(out VulkanCommandThreadContext context))
            return publishedGeneration;

        if (!ReferenceEquals(context.ResourcePlannerRuntimeStateOwner, this))
            return publishedGeneration;

        return context.ResourcePlannerRuntimeGeneration ?? throw new InvalidOperationException(
            "The Vulkan command planner scope has no immutable runtime generation.");
    }

    private VulkanTrackedCommandEncoder PrimaryCommandEncoder => new(this);

    internal VulkanProgramRecordingRequest CreateProgramRecordingRequest(CommandBuffer commandBuffer)
        => new(this, commandBuffer);

    internal bool TryPushProgramDescriptorHeapData(CommandBuffer commandBuffer, VkRenderProgram program, DescriptorHeapPushDataPayload payload)
        => PrimaryCommandEncoder.TryPushDescriptorHeapProgramData(commandBuffer, program, payload.Dwords, payload.Dwords.Length);

    internal Vk Api => DeviceContext.Api;
    private Vk VulkanApi => Api;
    private VulkanCommandRuntime _commandRuntime => this;
    private VulkanDeviceContext _deviceContext => DeviceContext;
    private VulkanResourceRuntime _resourceRuntime => ResourceRuntime;
    private VulkanFrameTelemetry _frameTelemetry => FrameTelemetry;

    /// <summary>
    /// Admits and begins a command-buffer recording without exposing device lifetime
    /// state to the command encoder.
    /// </summary>
    internal void BeginRecording(
        Vk api,
        VulkanDeviceStateMachine deviceState,
        CommandBuffer commandBuffer,
        string operation,
        CommandBufferUsageFlags flags = 0)
    {
        if (!deviceState.IsOperational)
        {
            throw new InvalidOperationException(
                $"Cannot start Vulkan operation '{operation}' while device state is {deviceState.State}.");
        }

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = flags,
        };
        Result result = BeginTrackedCommandBuffer(
            api,
            commandBuffer,
            ref beginInfo,
            operation);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to begin Vulkan operation '{operation}': {result}.");
    }

    /// <summary>
    /// Owns command-buffer recording admission and the native begin call as one
    /// externally synchronized host transaction.
    /// </summary>
    internal Result BeginTrackedCommandBuffer(
        CommandBuffer commandBuffer,
        ref CommandBufferBeginInfo beginInfo,
        string owner)
        => BeginTrackedCommandBuffer(Api, commandBuffer, ref beginInfo, owner);

    private Result BeginTrackedCommandBuffer(
        Vk api,
        CommandBuffer commandBuffer,
        ref CommandBufferBeginInfo beginInfo,
        string owner)
    {
        if (!DeviceContext.IsOperational)
            return Result.ErrorDeviceLost;

        using (VulkanFrameLockScope.Enter(
                   Pools.Gate,
                   EVulkanFrameWaitReason.CommandPool))
        {
            ulong recordingGeneration = InitializeCommandBufferBindState(commandBuffer);
            try
            {
                BeginCommandBufferTrackingCore(
                    commandBuffer,
                    recordingGeneration,
                    owner);
            }
            catch
            {
                CommandBuffers.RemoveBindState(commandBuffer);
                throw;
            }

            Result result = api.BeginCommandBuffer(commandBuffer, ref beginInfo);
            if (result == Result.Success)
                return result;

            TryAbandonCommandBufferRecording(commandBuffer);
            CommandBuffers.RemoveBindState(commandBuffer);
            Synchronization.RemoveRecordedImageLayouts(commandBuffer);
            return result;
        }
    }

    private ulong InitializeCommandBufferBindState(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            throw new ArgumentException("A live command buffer is required.", nameof(commandBuffer));

        ulong recordingGeneration = unchecked(
            (ulong)Interlocked.Increment(ref CommandBuffers.RecordingGeneration));
        using (VulkanFrameLockScope.Enter(
                   CommandBuffers.BindStateGate,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            CommandBuffers.BindStates[handle] = new CommandBufferBindState
            {
                RecordingGeneration = recordingGeneration,
            };
        }
        return recordingGeneration;
    }

    private void BeginCommandBufferTrackingCore(
        CommandBuffer commandBuffer,
        ulong recordingGeneration,
        string owner)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (!ResourceRuntime.TryValidateCommandBufferRecordingAdmissionNoLock(
                    handle,
                    out string reason))
            {
                throw new InvalidOperationException(
                    $"Cannot begin command buffer 0x{handle:X} for {owner}: {reason}");
            }

            if (tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime.InvalidateSealedSubmissionContract();
            }

            VulkanCommandBufferTrackingBatch batch =
                CommandBuffers.TrackingBatches.GetOrAdd(handle, static _ => new());
            using (VulkanFrameLockScope.Enter(
                       batch,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Command buffer 0x{handle:X} cannot begin {owner} while its prior recording or submission is active.");
                }

                batch.Reset(recordingGeneration);
            }
        }

        ResetCommandBufferImageLayoutJournal(commandBuffer);
    }

    /// <summary>Returns a generation-owned graphics command pool for the calling thread.</summary>
    internal unsafe CommandPool GetThreadGraphicsCommandPool(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resources)
    {
        if (!deviceContext.IsOperational)
            throw new InvalidOperationException($"Cannot create a command pool while device state is {deviceContext.State}.");

        int threadId = Environment.CurrentManagedThreadId;
        using (VulkanFrameLockScope.Enter(
                   Pools.Gate,
                   EVulkanFrameWaitReason.CommandPool))
        {
            if (Pools.GraphicsByThread.TryGetValue(threadId, out CommandPool pool) && pool.Handle != 0)
                return pool;

            uint queueFamily = deviceContext.QueueFamilies.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            CommandPoolCreateInfo createInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
            };
            if (api.CreateCommandPool(deviceContext.Device, ref createInfo, null, out pool) != Result.Success)
                throw new InvalidOperationException("Failed to create a Vulkan graphics command pool.");

            resources.Lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.CommandPool, pool.Handle),
                $"CommandPool.QueueFamily.{queueFamily}",
                externallyOwned: false);
            Pools.GraphicsByThread[threadId] = pool;
            if (Pools.PrimaryGraphics.Handle == 0)
                Pools.PrimaryGraphics = pool;
            return pool;
        }
    }

    internal unsafe CommandBuffer AllocateTrackedCommandBuffer(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resources,
        CommandPool pool,
        CommandBufferLevel level,
        string owner)
    {
        if (!deviceContext.IsOperational)
            throw new InvalidOperationException($"Cannot allocate a command buffer while device state is {deviceContext.State}.");
        if (pool.Handle == 0)
            throw new ArgumentException("A live command pool is required.", nameof(pool));

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = level,
            CommandBufferCount = 1,
        };
        CommandBuffer commandBuffer = default;
        Result result = AllocateCommandBuffersWithLifetime(
            ref allocateInfo,
            &commandBuffer,
            owner);
        if (result != Result.Success || commandBuffer.Handle == 0)
        {
            throw new InvalidOperationException($"Failed to allocate Vulkan command buffer for {owner}.");
        }
        return commandBuffer;
    }

    /// <summary>
    /// Performs the native portion of a tracked queue submission under the
    /// command authority's device-admission and queue-serialization boundary.
    /// Lifetime and image-state publication are intentionally owned by the
    /// resource and synchronization authorities immediately around this call.
    /// </summary>
    internal Result SubmitToQueueTracked(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanFrameTelemetry telemetry,
        Queue queue,
        ref SubmitInfo submitInfo,
        Fence fence,
        string operation)
    {
        if (!deviceContext.IsOperational)
        {
            Synchronization.RecordQueueOperation(
                deviceContext.State,
                "submit-rejected",
                queue,
                Result.ErrorDeviceLost,
                0,
                operation);
            return Result.ErrorDeviceLost;
        }

        Result result;
        CommandBuffers.DeviceQueueAdmissionGate.EnterReadLock();
        try
        {
            using VulkanQueueOperationLease queueOperation = VulkanQueueOperationLease.TryEnter(
                CommandBuffers.OneTimeSubmitGate,
                deviceContext.StateMachine,
                telemetry);
            if (!queueOperation.Acquired)
            {
                Synchronization.RecordQueueOperation(
                    deviceContext.State,
                    "submit-rejected",
                    queue,
                    Result.ErrorDeviceLost,
                    0,
                    operation);
                return Result.ErrorDeviceLost;
            }

            using (VulkanCpuStageScope cpuStage = new(telemetry, EVulkanCpuStage.QueueSubmit))
                result = api.QueueSubmit(queue, 1, ref submitInfo, fence);
        }
        finally
        {
            CommandBuffers.DeviceQueueAdmissionGate.ExitReadLock();
        }

        deviceContext.ObserveNativeResult(operation, result);
        Synchronization.RecordQueueOperation(
            deviceContext.State,
            "submit",
            queue,
            result,
            0,
            operation);
        if (result == Result.Success)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();
        return result;
    }

    internal VulkanExactInvalidationResult InvalidateCachedCommandBuffers(
        ReadOnlySpan<ulong> dependentCommandBuffers,
        string reason)
        => InvalidateCachedCommandBuffersCore(
            dependentCommandBuffers,
            reason,
            FrameTelemetry);

    private VulkanExactInvalidationResult InvalidateCachedCommandBuffersCore(
        ReadOnlySpan<ulong> dependentCommandBuffers,
        string reason,
        VulkanFrameTelemetry telemetry)
    {
        using VulkanCpuStageScope dirtyPropagationStage =
            new(telemetry, EVulkanCpuStage.CommandDirtyPropagation);
        if (dependentCommandBuffers.IsEmpty)
            return default;

        for (int index = 0; index < dependentCommandBuffers.Length; index++)
            if (dependentCommandBuffers[index] != 0)
                CommandBuffers.InvalidatedBuffersPendingReset.TryAdd(
                    dependentCommandBuffers[index],
                    0);

        int exactVariantsDirtied = 0;
        int exactChainsDirtied = 0;
        int unrelatedVariantsPreserved = 0;
        if (CommandBuffers.PrimaryOwners is not null)
        {
            for (int index = 0; index < CommandBuffers.PrimaryOwners.Length; index++)
            {
                PrimaryCommandArtifactOwner owner = CommandBuffers.PrimaryOwners[index];
                bool dependent = ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.PrimaryCommandBuffer.Handle)) ||
                    ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.DynamicUiSecondaryCommandBuffer.Handle));
                if (!dependent)
                {
                    unrelatedVariantsPreserved++;
                    continue;
                }

                if (!owner.Dirty)
                    exactVariantsDirtied++;
                owner.Dirty = true;
                owner.DirtyReason = reason;
            }
        }

        using (VulkanFrameLockScope.Enter(
                   CommandBuffers.OpenXrPrimaryOwnersGate,
                   EVulkanFrameWaitReason.SynchronizationLock))
        {
            foreach (PrimaryCommandArtifactOwner owner in
                     CommandBuffers.OpenXrPrimaryOwners.Values)
            {
                if (!ContainsHandle(
                        dependentCommandBuffers,
                        unchecked((ulong)owner.PrimaryCommandBuffer.Handle)))
                {
                    unrelatedVariantsPreserved++;
                    continue;
                }

                if (!owner.Dirty)
                    exactVariantsDirtied++;
                owner.Dirty = true;
                owner.DirtyReason = reason;
            }
        }

        if (CommandChains.Caches is not null)
        {
            for (int cacheIndex = 0;
                 cacheIndex < CommandChains.Caches.Length;
                 cacheIndex++)
            {
                foreach (CommandChain chain in CommandChains.Caches[cacheIndex].Values)
                {
                    if (!ContainsHandle(
                            dependentCommandBuffers,
                            unchecked((ulong)chain.SecondaryCommandBuffer.Handle)))
                    {
                        continue;
                    }

                    chain.State = CommandChainState.Unrecorded;
                    chain.RecordedArtifact.Invalidate(
                        EVulkanRecordedCommandArtifactInvalidationReason.DependencyChanged);
                    chain.DirtyReason = CommandChainDirtyReason.ResourcePlan;
                    exactChainsDirtied++;
                }
            }
        }

        return new VulkanExactInvalidationResult(
            exactVariantsDirtied,
            exactChainsDirtied,
            unrelatedVariantsPreserved,
            GlobalFallbackInvalidations: 0);
    }

    internal void DrainInvalidatedCommandBufferRecordings(
        Vk api,
        VulkanResourceRuntime resourceRuntime,
        int maxItems = 64)
    {
        if (maxItems <= 0 || CommandBuffers.InvalidatedBuffersPendingReset.IsEmpty)
            return;

        int resetCount = 0;
        foreach (KeyValuePair<ulong, byte> pair in
                 CommandBuffers.InvalidatedBuffersPendingReset)
        {
            if (resetCount >= maxItems)
                break;

            ulong handle = pair.Key;
            CommandBuffer commandBuffer = new()
            {
                Handle = unchecked((nint)handle),
            };
            if (!TryResetCommandBufferWithLifetime(
                    commandBuffer,
                    "InvalidatedCommandBufferDrain",
                    out Result result))
                continue;
            if (result != Result.Success)
                continue;
            CommandBuffers.InvalidatedBuffersPendingReset.TryRemove(handle, out _);
            resetCount++;
        }
    }

    internal Result ResetCommandBufferWithLifetime(
        CommandBuffer commandBuffer,
        string owner)
    {
        if (!TryResetCommandBufferWithLifetime(commandBuffer, owner, out Result result))
        {
            throw new InvalidOperationException(
                $"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} is not resettable for {owner}.");
        }

        return result;
    }

    private bool TryResetCommandBufferWithLifetime(
        CommandBuffer commandBuffer,
        string owner,
        out Result result)
    {
        result = Result.ErrorUnknown;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        using (VulkanFrameLockScope.Enter(
                   Pools.Gate,
                   EVulkanFrameWaitReason.CommandPool))
        {
            VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
            VulkanCommandBufferTrackingBatch batch;
            using (VulkanFrameLockScope.Enter(
                       tracker.SyncRoot,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                batch = CommandBuffers.TrackingBatches.GetOrAdd(handle, static _ => new());
                using (VulkanFrameLockScope.Enter(
                           batch,
                           EVulkanFrameWaitReason.ResourceLifetimeLock))
                {
                    if (batch.IsRecording || batch.QueuedSubmissionCount != 0 ||
                        !ResourceRuntime.CanResetCommandBufferNoLock(handle))
                    {
                        return false;
                    }

                    // Submission admission observes this host-use marker while
                    // the native reset executes without the lifetime lock held.
                    batch.IsRecording = true;
                }
            }

            result = Api.ResetCommandBuffer(commandBuffer, 0);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
            if (result == Result.Success)
            {
                ResourceRuntime.CompleteCommandBufferReset(handle);
                RemoveCommandBufferState(commandBuffer);
            }

            using (VulkanFrameLockScope.Enter(
                       tracker.SyncRoot,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
            using (VulkanFrameLockScope.Enter(
                       batch,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
            {
                batch.IsRecording = false;
                if (result == Result.Success)
                    CommandBuffers.TrackingBatches.TryRemove(handle, out _);
            }

            return true;
        }
    }

    internal unsafe void DrainRetiredCommandBuffers(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        int maxItems = 128)
    {
        List<RetiredCommandBuffer> list =
            resourceRuntime.Lifetime.Retirement.CommandBuffers[frameSlot];
        List<RetiredCommandBuffer> ready = [];
        using (VulkanFrameLockScope.Enter(
                   resourceRuntime.Lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredCommandBuffer candidate = list[index];
                if (!IsCommandBufferRetirementReady(
                        resourceRuntime,
                        candidate.CommandBuffer,
                        candidate.Ticket))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    unchecked((ulong)candidate.CommandBuffer.Handle),
                    resourceRuntime.Lifetime.Retirement.CommandBufferHandles,
                    resourceRuntime.Lifetime.Retirement.AllCommandBufferHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            RetiredCommandBuffer entry = ready[index];
            CommandBuffer commandBuffer = entry.CommandBuffer;
            using (VulkanFrameLockScope.Enter(
                       Pools.Gate,
                       EVulkanFrameWaitReason.CommandPool))
                api.FreeCommandBuffers(
                    device,
                    entry.CommandPool,
                    1,
                    &commandBuffer);

            RemoveCommandBufferState(entry.CommandBuffer);
            resourceRuntime.CompleteCommandBufferDestruction(
                entry.CommandBuffer);
            if (CommandBuffers.TryReleaseOwnedSecondaryCommandBuffer(
                    entry.CommandPool,
                    entry.CommandBuffer,
                    out CommandPool poolReadyForRetirement))
            {
                QueueCommandPoolRetirementTracked(
                    poolReadyForRetirement,
                    frameSlot);
            }
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            commandBuffers: ready.Count);
    }

    internal unsafe void DrainRetiredCommandPools(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        int maxItems = 16)
    {
        List<RetiredCommandPool> list =
            resourceRuntime.Lifetime.Retirement.CommandPools[frameSlot];
        List<RetiredCommandPool> ready = [];
        using (VulkanFrameLockScope.Enter(
                   resourceRuntime.Lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int index = 0;
                 index < list.Count && ready.Count < maxItems;)
            {
                RetiredCommandPool candidate = list[index];
                if (!resourceRuntime.Lifetime.Tracker.IsRetirementReady(
                        candidate.Ticket) ||
                    !AreCommandPoolChildrenRetirementReady(
                        resourceRuntime,
                        candidate.CommandPool))
                {
                    index++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(index);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.CommandPool.Handle,
                    resourceRuntime.Lifetime.Retirement.CommandPoolHandles,
                    resourceRuntime.Lifetime.Retirement.AllCommandPoolHandles);
            }
        }

        for (int index = 0; index < ready.Count; index++)
        {
            CommandPool pool = ready[index].CommandPool;
            using (VulkanFrameLockScope.Enter(
                       Pools.Gate,
                       EVulkanFrameWaitReason.CommandPool))
                api.DestroyCommandPool(device, pool, null);
            resourceRuntime.CompleteCommandPoolChildDestructions(pool);
            resourceRuntime.CompleteCommandPoolDestruction(pool);
        }
    }

    internal unsafe void FreeTrackedCommandBuffer(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        if (commandPool.Handle == 0 || commandBuffer.Handle == 0)
            return;

        CommandBuffer retiring = commandBuffer;
        VulkanRetirementTicket ticket =
            resourceRuntime.PrepareCommandBufferRetirement(
                retiring,
                owner);
        if (!IsCommandBufferRetirementReady(
                resourceRuntime,
                retiring,
                ticket))
        {
            resourceRuntime.QueueCommandBufferRetirement(
                commandPool,
                retiring,
                ticket,
                frameSlot);
            commandBuffer = default;
            return;
        }

        using (VulkanFrameLockScope.Enter(
                   Pools.Gate,
                   EVulkanFrameWaitReason.CommandPool))
            api.FreeCommandBuffers(device, commandPool, 1, &retiring);
        RemoveCommandBufferState(retiring);
        resourceRuntime.CompleteCommandBufferDestruction(retiring);
        if (CommandBuffers.TryReleaseOwnedSecondaryCommandBuffer(
                commandPool,
                retiring,
                out CommandPool poolReadyForRetirement))
        {
            QueueCommandPoolRetirementTracked(
                poolReadyForRetirement,
                frameSlot);
        }

        commandBuffer = default;
    }

    private bool IsCommandBufferRetirementReady(
        VulkanResourceRuntime resourceRuntime,
        CommandBuffer commandBuffer,
        in VulkanRetirementTicket ticket)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;

        VulkanResourceLifetimeTracker tracker = resourceRuntime.Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (tracker.ForcedRetirementDrainDepth > 0)
                return true;

            if (CommandBuffers.TrackingBatches.TryGetValue(
                    handle,
                    out VulkanCommandBufferTrackingBatch? batch))
            {
                using (VulkanFrameLockScope.Enter(
                           batch,
                           EVulkanFrameWaitReason.ResourceLifetimeLock))
                    if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                        return false;
            }

            if (tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) &&
                lifetime.QueuedSubmissionCount != 0)
            {
                return false;
            }

            return tracker.IsRetirementReadyNoLock(ticket);
        }
    }

    private bool AreCommandPoolChildrenRetirementReady(
        VulkanResourceRuntime resourceRuntime,
        CommandPool commandPool)
    {
        if (commandPool.Handle == 0)
            return true;

        VulkanResourceLifetimeTracker tracker = resourceRuntime.Lifetime.Tracker;
        VulkanResourceLifetimeKey poolKey = new(
            ObjectType.CommandPool,
            commandPool.Handle);
        CommandBuffer[] children;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (!tracker.CommandBuffersByPool.TryGetValue(
                    poolKey,
                    out HashSet<ulong>? ownedChildren) ||
                ownedChildren.Count == 0)
            {
                return true;
            }

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
            CommandBuffer child = children[index];
            if (resourceRuntime.IsCommandBufferPendingRetirement(child) ||
                !IsCommandBufferRetirementReady(
                    resourceRuntime,
                    child,
                    VulkanRetirementTicket.None))
            {
                return false;
            }
        }

        return true;
    }

    internal void RemoveCommandBufferState(CommandBuffer commandBuffer)
    {
        CommandBuffers.RemoveBindState(commandBuffer);
        Synchronization.RemoveRecordedImageLayouts(commandBuffer);
    }

    /// <summary>
    /// Executes the common worker lifecycle for an already-frozen recording batch.
    /// </summary>
    internal void ExecuteCommandChainRecordingWorker(CommandChainRecordingWorkerState worker)
    {
        VulkanCommandChainRecordingBatch? batch = worker.Batch;
        VulkanPreparedWorkerRecordingContext? context = batch?.PreparedWorkerContext;
        if (batch is null || context is null)
            return;

        using VulkanCpuStageScope cpuStage = new(FrameTelemetry, EVulkanCpuStage.SecondaryRecording);
        long workerStart = Stopwatch.GetTimestamp();
        batch.WorkerLocalStates.Begin(worker.WorkerIndex, workerStart, batch.DispatchTimestamp);
        try
        {
            worker.LastFrameId = context.FrameId;
            for (int entryIndex = 0; entryIndex < batch.EntryCount; entryIndex++)
            {
                if (Volatile.Read(ref batch.Error) is not null ||
                    Volatile.Read(ref batch.CancelRequested) != 0)
                    break;
                ref VulkanCommandChainRecordingEntry entry = ref batch.Entries[entryIndex];
                if (!entry.NeedsRecording || entry.WorkerIndex != worker.WorkerIndex)
                    continue;

                try
                {
                    RecordPreparedMeshCommandChain(
                        batch,
                        entryIndex);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref batch.Error, ex, null);
                    break;
                }
            }
        }
        finally
        {
            long workerCompletion = Stopwatch.GetTimestamp();
            batch.WorkerLocalStates.Complete(worker.WorkerIndex, workerCompletion);
            worker.Batch = null;
            bool lastWorker = Workers.Countdown.Signal();
            if (lastWorker)
            {
                Volatile.Write(ref Workers.ActiveWorkerCount, 0);
                Workers.Idle.Set();
                if (batch.Abandoned)
                    batch.ClearReferences();
            }
        }
    }

    /// <summary>
    /// Records frozen standalone non-graphics packet classes on the same
    /// persistent worker domain as prepared mesh secondaries.
    /// </summary>
    internal void ExecuteNonGraphicsRecordingWorker(
        CommandChainRecordingWorkerState worker)
    {
        VulkanNonGraphicsRecordingBatch? batch = worker.NonGraphicsBatch;
        if (batch is null)
            return;

        using VulkanCpuStageScope cpuStage = new(
            FrameTelemetry,
            EVulkanCpuStage.SecondaryRecording);
        try
        {
            using VulkanWorkerSecondaryCommandArena.RecordingLease arenaLease =
                VulkanWorkerSecondaryCommandArena.EnterRecording(worker.Arena);
            for (int entryIndex = 0; entryIndex < batch.EntryCount; entryIndex++)
            {
                if (Volatile.Read(ref batch.Error) is not null ||
                    Volatile.Read(ref batch.CancelRequested) != 0)
                {
                    break;
                }

                ref VulkanNonGraphicsRecordingEntry entry =
                    ref batch.Entries[entryIndex];
                if (entry.WorkerIndex != worker.WorkerIndex ||
                    entry.Chain is not { } chain)
                {
                    continue;
                }

                try
                {
                    RecordPreparedNonGraphicsSecondary(
                        batch.Operations,
                        batch.ImageIndex,
                        chain,
                        entry.SecondaryBuffer,
                        batch.QueryInheritance);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref batch.Error, ex, null);
                    break;
                }
            }
        }
        finally
        {
            worker.NonGraphicsBatch = null;
            bool lastWorker = Workers.Countdown.Signal();
            if (lastWorker)
            {
                Volatile.Write(ref Workers.ActiveWorkerCount, 0);
                Workers.Idle.Set();
                if (batch.Abandoned)
                    batch.ClearReferences();
            }
        }
    }

    internal void DeferSecondaryCommandBufferFree(
        Vk api,
        Device device,
        VulkanResourceRuntime resourceRuntime,
        int frameSlot,
        uint imageIndex,
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        string owner)
    {
        ulong generation = resourceRuntime.GetPublishedGeneration(
            ObjectType.CommandBuffer,
            unchecked((ulong)commandBuffer.Handle));
        if (CommandBuffers.TryDeferSecondaryCommandBufferFree(
                imageIndex,
                commandPool,
                commandBuffer,
                generation))
        {
            return;
        }

        FreeTrackedCommandBuffer(
            api,
            device,
            resourceRuntime,
            frameSlot,
            commandPool,
            ref commandBuffer,
            owner);
    }

    private static bool ContainsHandle(
        ReadOnlySpan<ulong> handles,
        ulong candidate)
    {
        if (candidate == 0)
            return false;

        for (int index = 0; index < handles.Length; index++)
            if (handles[index] == candidate)
                return true;

        return false;
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current = Volatile.Read(ref target);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void UpdateMinimum(ref long target, long candidate)
    {
        long current = Volatile.Read(ref target);
        while (candidate < current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    public CommandChainSchedule? GetReusableSchedule(int slot, int slotCount)
    {
        EnsureScheduleCache(slotCount);
        return (uint)slot < (uint)_scheduleCache!.Length ? _scheduleCache[slot] : null;
    }

    public void CacheSchedule(int slot, int slotCount, CommandChainSchedule schedule)
    {
        EnsureScheduleCache(slotCount);
        if ((uint)slot < (uint)_scheduleCache!.Length)
            _scheduleCache[slot] = schedule;
    }

    public void InvalidateScheduleCache()
    {
        if (_scheduleCache is not null)
            Array.Clear(_scheduleCache);
    }

    public void ReleaseScheduleCache() => _scheduleCache = null;

    public int ResolveParallelRecordingBucket(
        in VulkanMeshFrameDataRendererFamilyKey rendererFamily,
        int workerCount)
    {
        if (workerCount <= 1)
            return 0;

        int rendererIdentity = RuntimeHelpers.GetHashCode(rendererFamily.Renderer);
        return unchecked((int)((uint)rendererIdentity % (uint)workerCount));
    }

    private void EnsureScheduleCache(int slotCount)
    {
        int count = Math.Max(slotCount, 1);
        if (_scheduleCache is not null && _scheduleCache.Length == count)
            return;

        _scheduleCache = new CommandChainSchedule?[count];
    }
}
