using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns query capabilities, pool arenas, provider registrations, and query
/// completion for one Vulkan resource generation.
/// </summary>
internal unsafe sealed partial class VulkanQueryAuthority : IVulkanQueryArenaFacility
{
    private VulkanBackendObjectContext? _backendContext;

    internal VulkanQueryCommandService? Commands { get; private set; }

    internal void BindCommands(VulkanQueryCommandService commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (Commands is not null && !ReferenceEquals(Commands, commands))
            throw new InvalidOperationException(
                "Vulkan query command service has already been bound.");
        Commands = commands;
    }

    /// <summary>
    /// Publishes the generation-local native services used by pool allocation
    /// and host result reads.  Query wrappers retain this authority rather than
    /// a renderer facade.
    /// </summary>
    internal void BindBackendContext(VulkanBackendObjectContext backendContext)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        if (_backendContext is not null && !ReferenceEquals(_backendContext, backendContext))
            throw new InvalidOperationException(
                "Vulkan query authority has already been bound to another backend-object context.");
        _backendContext = backendContext;
    }

    private readonly object _sync = new();
    private readonly Dictionary<ERenderQueryKind, IVulkanSpecializedQueryProvider> _providers = [];
    internal bool OcclusionPreciseAdvertised;
    internal bool OcclusionPreciseEnabled;
    internal bool PipelineStatisticsAdvertised;
    internal bool PipelineStatisticsEnabled;
    internal bool InheritedQueriesAdvertised;
    internal bool InheritedQueriesEnabled;
    internal bool HostResetAdvertised;
    internal bool MeshShaderQueriesEnabled;
    internal bool PrimitivesGeneratedAdvertised;
    internal bool PrimitivesGeneratedEnabled;
    internal bool PrimitivesGeneratedNonZeroStreamsEnabled;
    internal VulkanQueryCapabilities Capabilities = VulkanQueryCapabilities.Unsupported;
    internal VulkanQueryPoolArenaManager? Arenas;

    internal VulkanQueryPoolArenaManager PoolArenas
        => Arenas ??= new VulkanQueryPoolArenaManager(this);

    internal QueryArenaTelemetry CaptureArenaTelemetry()
        => Arenas?.CaptureTelemetry() ?? default;

    internal bool TryAllocate(
        in VulkanQueryPoolKey key,
        uint queryCount,
        out VulkanQueryPoolAllocation allocation,
        out string? reason)
        => PoolArenas.TryAllocate(key, queryCount, out allocation, out reason);

    internal void Release(in VulkanQueryPoolAllocation allocation)
        => Arenas?.Release(allocation);

    internal void RecordResetEpoch()
        => PoolArenas.RecordResetEpoch();

    internal void DisposeArenas()
    {
        Arenas?.Dispose();
        Arenas = null;
    }

    internal bool IsSubmissionCompleted(in VulkanLifetimeSubmission submission)
    {
        if (submission.QueueSequence == 0ul)
            return false;

        VulkanResourceLifetimeTracker lifetime = RequireBackendContext().Resources.Lifetime.Tracker;
        lock (lifetime.SyncRoot)
        {
            return submission.QueueDomain switch
            {
                EVulkanLifetimeQueueDomain.Graphics => submission.QueueSequence <= lifetime.CompletedGraphicsSequence,
                EVulkanLifetimeQueueDomain.Transfer => submission.QueueSequence <= lifetime.CompletedTransferSequence,
                _ => submission.QueueSequence <= lifetime.CompletedOtherSequence,
            };
        }
    }

    internal bool WaitForSubmissionCompletion(
        in VulkanLifetimeSubmission submission,
        string reason)
    {
        VulkanBackendObjectContext context = RequireBackendContext();
        if (!context.IsDeviceOperational)
            return false;

        if (submission.TimelineSemaphoreHandle != 0 && submission.TimelineValue != 0)
        {
            Silk.NET.Vulkan.Semaphore semaphore = new(submission.TimelineSemaphoreHandle);
            ulong timelineValue = submission.TimelineValue;
            SemaphoreWaitInfo waitInfo = new()
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &timelineValue,
            };
            Result timelineResult = context.Api.WaitSemaphores(
                context.Device,
                in waitInfo,
                ulong.MaxValue);
            if (timelineResult == Result.Success)
            {
                MarkCompletedTimelineSubmission(submission);
                return true;
            }

            HandleWaitFailure(timelineResult, reason, "vkWaitSemaphores.TrackedQuerySubmission");
            return false;
        }

        if (submission.FenceHandle == 0)
        {
            Debug.VulkanWarning(
                "[Vulkan.Query] Cannot wait for tracked submission {0}/{1} ({2}): no timeline or fence completion primitive was published.",
                submission.QueueDomain,
                submission.QueueSequence,
                reason);
            return false;
        }

        Fence fence = new(submission.FenceHandle);
        Result fenceResult = context.Api.WaitForFences(
            context.Device,
            1,
            &fence,
            true,
            ulong.MaxValue);
        if (fenceResult == Result.Success)
        {
            MarkCompletedFenceSubmission(submission.FenceHandle);
            return true;
        }

        HandleWaitFailure(fenceResult, reason, "vkWaitForFences.TrackedQuerySubmission");
        return false;
    }

    internal void MarkDeviceLost(string reason, string operation, Result result)
    {
        // Query host waits are resource-authority observations. The frame loop
        // settles the cross-authority terminal transition when it next observes
        // the device context state.
        RequireBackendContext().DeviceContext.ObserveNativeResult(operation, result);
    }

    internal void Register(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_sync)
            _providers[provider.Kind] = provider;
    }

    internal void Unregister(IVulkanSpecializedQueryProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_sync)
        {
            if (_providers.TryGetValue(provider.Kind, out IVulkanSpecializedQueryProvider? registered) &&
                ReferenceEquals(registered, provider))
            {
                _providers.Remove(provider.Kind);
            }
        }
    }

    internal bool TryGet(ERenderQueryKind kind, out IVulkanSpecializedQueryProvider provider)
    {
        lock (_sync)
            return _providers.TryGetValue(kind, out provider!);
    }

    /// <summary>
    /// Publishes query-pool ownership into the generation-local lifetime ledger.
    /// Submission processing uses this index to transition the exact recorded
    /// epoch once the command buffer is accepted by a queue.
    /// </summary>
    internal static void RegisterRenderQuery(
        VulkanResourceLifetimeTracker lifetime,
        QueryPool queryPool,
        VkRenderQuery query)
    {
        if (queryPool.Handle == 0)
            return;

        lock (lifetime.SyncRoot)
        {
            if (!lifetime.RenderQueriesByPool.TryGetValue(queryPool.Handle, out List<VkRenderQuery>? queries))
            {
                queries = new List<VkRenderQuery>(32);
                lifetime.RenderQueriesByPool.Add(queryPool.Handle, queries);
            }

            if (!queries.Contains(query))
                queries.Add(query);
        }
    }

    internal static void UnregisterRenderQuery(
        VulkanResourceLifetimeTracker lifetime,
        QueryPool queryPool,
        VkRenderQuery query)
    {
        if (queryPool.Handle == 0)
            return;

        lock (lifetime.SyncRoot)
        {
            if (!lifetime.RenderQueriesByPool.TryGetValue(queryPool.Handle, out List<VkRenderQuery>? queries))
                return;

            queries.Remove(query);
            if (queries.Count == 0)
                lifetime.RenderQueriesByPool.Remove(queryPool.Handle);
        }
    }

    /// <summary>
    /// Records a specialized query through the frozen command encoder captured by
    /// the caller. Providers therefore cannot depend on the mutable renderer facade.
    /// </summary>
    internal bool TryRecord(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        QueryPool queryPool,
        uint firstQuery,
        in RenderQueryDescriptor descriptor,
        ReadOnlySpan<ulong> sourceHandles,
        out string? reason)
    {
        if (!TryGet(descriptor.Kind, out IVulkanSpecializedQueryProvider provider))
        {
            reason = "No specialized provider is registered.";
            return false;
        }

        return provider.TryRecord(
            encoder,
            commandBuffer,
            queryPool,
            firstQuery,
            in descriptor,
            sourceHandles,
            out reason);
    }

    bool IVulkanQueryArenaFacility.IsDeviceLost
        => !RequireBackendContext().IsDeviceOperational;

    bool IVulkanQueryArenaFacility.IsLogicalDeviceReady
        => RequireBackendContext().IsLogicalDeviceReady;

    Result IVulkanQueryArenaFacility.CreateQueryPool(
        ref QueryPoolCreateInfo createInfo,
        out QueryPool pool)
    {
        VulkanBackendObjectContext context = RequireBackendContext();
        return context.Api.CreateQueryPool(context.Device, ref createInfo, null, out pool);
    }

    void IVulkanQueryArenaFacility.RegisterQueryPool(QueryPool pool, string owner)
    {
        if (pool.Handle != 0)
            RequireBackendContext().Resources.Lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.QueryPool, pool.Handle),
                owner,
                externallyOwned: false);
    }

    void IVulkanQueryArenaFacility.RetireQueryPool(QueryPool pool)
        => RequireBackendContext().Resources.RetireQueryPool(pool, "QueryArena");

    private VulkanBackendObjectContext RequireBackendContext()
        => _backendContext ?? throw new InvalidOperationException(
            "Vulkan query authority has not been bound to a backend-object context.");

    private void HandleWaitFailure(Result result, string reason, string operation)
    {
        if (result == Result.ErrorDeviceLost)
        {
            MarkDeviceLost(
                $"Waiting for tracked query submission ({reason}) returned ErrorDeviceLost",
                operation,
                result);
            return;
        }

        Debug.VulkanWarning(
            "[Vulkan.Query] Waiting for tracked submission ({0}) failed: {1}.",
            reason,
            result);
    }

    private void MarkCompletedFenceSubmission(ulong fenceHandle)
    {
        if (fenceHandle == 0)
            return;

        VulkanResourceLifetimeTracker lifetime = RequireBackendContext().Resources.Lifetime.Tracker;
        lock (lifetime.SyncRoot)
        {
            for (int index = lifetime.LifetimeSubmissions.Count - 1; index >= 0; index--)
            {
                VulkanLifetimeSubmission submission = lifetime.LifetimeSubmissions[index];
                if (submission.FenceHandle != fenceHandle)
                    continue;

                lifetime.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                lifetime.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }

    private void MarkCompletedTimelineSubmission(in VulkanLifetimeSubmission completed)
    {
        VulkanResourceLifetimeTracker lifetime = RequireBackendContext().Resources.Lifetime.Tracker;
        lock (lifetime.SyncRoot)
        {
            for (int index = lifetime.LifetimeSubmissions.Count - 1; index >= 0; index--)
            {
                VulkanLifetimeSubmission submission = lifetime.LifetimeSubmissions[index];
                if (submission.TimelineSemaphoreHandle != completed.TimelineSemaphoreHandle ||
                    submission.TimelineValue == 0 ||
                    submission.TimelineValue > completed.TimelineValue)
                {
                    continue;
                }

                lifetime.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                lifetime.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }
}
