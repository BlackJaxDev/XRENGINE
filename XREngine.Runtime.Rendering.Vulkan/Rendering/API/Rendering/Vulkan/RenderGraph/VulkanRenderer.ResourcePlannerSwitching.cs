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
    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group)
        => TryEnsurePhysicalImageForTextureResource(resourceName, out group, out _);

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group,
        out string? failureReason)
    {
        group = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group?.IsAllocated == true)
        {
            return true;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.ContainsKey(resourceName))
        {
            group = null;
            return false;
        }

        if (_commandRuntime.Recorder.IsRecording)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringRecord.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Deferring lazy physical-image plan rebuild for '{0}' during command-buffer recording.",
                resourceName);
            failureReason = "resource planner rebuild is deferred during command-buffer recording";
            group = null;
            return false;
        }

        if (IsCommandChainResourcePlanFrozen)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringFrozenCommandChainPlan.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Refusing lazy physical-image plan rebuild for '{0}' while command-chain readers are using frozen plan revision {1}.",
                resourceName,
                _framePlanner.FrozenResourcePlanRevision);
            failureReason = $"resource planner rebuild is deferred while command-chain readers are using frozen plan revision {_framePlanner.FrozenResourcePlanRevision}";
            group = null;
            return false;
        }

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
            Debug.Vulkan(
                "[VulkanResourcePlanner] Lazy physical-image rebuild resource='{0}' registry=0x{1:X8} owner={2} revision={3} textures={4} buffers={5}.",
                resourceName,
                ResolveFrameOpContextResourceRegistrySignature(context),
                plannerState.ResourceAllocator.OwnershipId,
                plannerState.ResourcePlannerRevision,
                plannerState.ResourceAllocator.LogicalTextureAllocations.Count,
                plannerState.ResourceAllocator.LogicalBufferAllocations.Count);
        }

        UpdateResourcePlannerFromContext(context);

        ResourcePlannerRuntimeState updatedPlannerState = CaptureResourcePlannerRuntimeState();
        if (updatedPlannerState.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group is not null)
        {
            if (!group.TryEnsureAllocated(this, out string allocationFailureReason))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.ResourcePlanner.LazyPhysicalImageAllocationFailed.{resourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Lazy physical-image allocation failed for '{0}': {1}",
                    resourceName,
                    allocationFailureReason);
                failureReason = allocationFailureReason;
                group = null;
                return false;
            }

            return group.IsAllocated;
        }

        group = null;
        return false;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops, ulong frameOpsSignature = 0)
    {
        if (ops.Length == 0)
        {
            FrameOpContext context = CaptureFrameOpContext();
            if (context.ResourceRegistry is null && context.PassMetadata is null)
                return context;

            UpdateResourcePlannerFromContext(context);
            return context;
        }

        FrameOpContext primary = SelectPrimaryPlannerContext(ops);
        RejectMixedFrameOpPlannerContexts(ops);
        FrameOpContext plannerContext = primary;

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);

        // Descriptor snapshots are captured against the full pipeline resource
        // plan before the command buffer is recorded. Keep frame-op recording on
        // that same plan so FBO writes, sampled descriptors, and readback all
        // resolve the same physical image groups.
        UpdateResourcePlannerFromContext(plannerContext);

        return plannerContext;
    }

    private bool TryReusePreparedFrameOpResourcePlannerStates(
        ulong frameOpsSignature,
        out ulong plannerRevision)
    {
        plannerRevision = ResourcePlannerRevision;
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!_deviceContext.IsOperational ||
            !FrameOpResourcePlannerSwitchingEnabled ||
            frameOpsSignature == 0 ||
            !switchingState.HasPreparedPlan ||
            switchingState.PreparedFrameOpsSignature != frameOpsSignature ||
            !TryGetPreparedFrameOpResourcePlannerState(switchingState, out ResourcePlannerRuntimeState preparedState))
        {
            InvalidatePreparedFrameOpResourcePlan(switchingState);
            return false;
        }

        if (!IsReusableFrameOpResourcePlannerState(preparedState) ||
            !preparedState.HasResourcePlannerFastPathKey ||
            preparedState.ResourcePlannerSignature == ulong.MaxValue)
        {
            InvalidatePreparedFrameOpResourcePlan(switchingState);
            return false;
        }

        ResourcePlannerFastPathKey fastPathKey = preparedState.ResourcePlannerFastPathKey;
        int currentRegistryRevision = fastPathKey.Registry?.DescriptorRevision ?? 0;
        int currentPassMetadataRevision = ComputePassMetadataRevisionStamp(fastPathKey.ActivePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig currentQueueOwnership =
            _framePlanner.BuildQueueOwnershipConfig(
                _deviceContext,
                fastPathKey.ActivePassMetadata,
                VulkanFeatureProfile.ActiveProfile);
        if (currentRegistryRevision != fastPathKey.RegistryDescriptorRevision ||
            currentPassMetadataRevision != fastPathKey.ActivePassMetadataRevision ||
            !currentQueueOwnership.Equals(fastPathKey.QueueOwnership) ||
            SupportsTransformFeedback != fastPathKey.SupportsTransformFeedback)
        {
            InvalidatePreparedFrameOpResourcePlan(switchingState);
            return false;
        }

        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
        {
            if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state) ||
                !IsReusableFrameOpResourcePlannerState(state))
            {
                InvalidatePreparedFrameOpResourcePlan(switchingState);
                return false;
            }
        }

        ResetActiveFrameOpResourcePlannerState(switchingState);
        switchingState.RecordingScopeActive = false;
        switchingState.SwitchingActive = false;
        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);

        plannerRevision = switchingState.PreparedPlanRevision;
        RecordPhysicalPlanCacheTelemetry(
            hit: true,
            preparedState.CompiledRenderGraph.Plan.Generation);
        AssertFrameOpPlannerAllocatorOwnership(switchingState);
        return true;
    }

    private void RememberPreparedFrameOpResourcePlannerStates(
        ulong frameOpsSignature,
        ulong plannerRevision)
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!_deviceContext.IsOperational ||
            !FrameOpResourcePlannerSwitchingEnabled ||
            frameOpsSignature == 0 ||
            !TryGetPreparedFrameOpResourcePlannerState(switchingState, out ResourcePlannerRuntimeState preparedState) ||
            !IsReusableFrameOpResourcePlannerState(preparedState))
        {
            InvalidatePreparedFrameOpResourcePlan(switchingState);
            return;
        }

        switchingState.PreparedFrameOpsSignature = frameOpsSignature;
        switchingState.PreparedPlanRevision = plannerRevision;
        switchingState.HasPreparedPlan = true;
    }

    private static bool IsReusableFrameOpResourcePlannerState(in ResourcePlannerRuntimeState state)
        => state.ResourcePlanner is not null &&
           state.ResourceAllocator is not null &&
           !state.ResourceAllocator.IsRetired &&
           state.ResourceAllocator.OwnershipId == state.AllocatorOwnershipId &&
           state.BarrierPlanner is not null &&
           state.CompiledRenderGraph is not null;

    private static bool TryGetPreparedFrameOpResourcePlannerState(
        FrameOpResourcePlannerSwitchingState switchingState,
        out ResourcePlannerRuntimeState state)
    {
        if (switchingState.ActiveKeys.Count == 0)
        {
            state = switchingState.PreparationState;
            return switchingState.HasPreparationState;
        }

        if (switchingState.ActiveKeys.Count != 1)
        {
            state = default;
            return false;
        }

        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
            return switchingState.States.TryGetValue(key, out state);

        state = default;
        return false;
    }

    private static void InvalidatePreparedFrameOpResourcePlan(
        FrameOpResourcePlannerSwitchingState switchingState)
    {
        switchingState.PreparedFrameOpsSignature = 0;
        switchingState.PreparedPlanRevision = 0;
        switchingState.HasPreparedPlan = false;
    }

    private ulong PrepareFrameOpResourcePlannerStatesForFrameOps(
        FrameOp[] ops,
        ulong frameOpsSignature = 0,
        bool preserveActiveKeys = false)
    {
        if (!_deviceContext.IsOperational)
            return ResourcePlannerRevision;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        InvalidatePreparedFrameOpResourcePlan(switchingState);
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        if (!preserveActiveKeys)
            switchingState.ActiveKeys.Clear();

        if (!FrameOpResourcePlannerSwitchingEnabled)
        {
            DestroyFrameOpResourcePlannerStates();
            return ResourcePlannerRevision;
        }

        if (ops.Length == 0)
            return ResourcePlannerRevision;

        List<VulkanFrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(ops, keys);
        if (keys.Count == 0)
        {
            keys.Clear();
            PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
            return ResourcePlannerRevision;
        }

        if (keys.Count > 1)
        {
            keys.Clear();
            throw new VulkanPlanPreconditionException(
                "Frame-plan preparation rejected mixed Vulkan planner contexts; independent partitions are required.");
        }

        VulkanFrameOpPlannerStateKey key = keys[0];
        ResourcePlannerRuntimeState preparedState = CaptureResourcePlannerRuntimeState();
        preparedState.LastActiveFrameOpContext = SelectPrimaryPlannerContext(ops, key);
        switchingState.States[key] = preparedState;
        switchingState.ActiveKeys.Add(key);
        MarkFrameOpResourcePlannerStateUsed(switchingState, key);
        keys.Clear();
        switchingState.SwitchingActive = false;
        PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
        AssertFrameOpPlannerAllocatorOwnership(switchingState);

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.SingleFrameOpContextState.{key.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Prepared single-context state registry=0x{0:X8} generation={1} owner={2} revision={3} signature=0x{4:X16}.",
                key.ResourceRegistrySignature,
                key.ResourceGeneration,
                preparedState.AllocatorOwnershipId,
                preparedState.ResourcePlannerRevision,
                preparedState.ResourcePlannerSignature);
        }

        return preparedState.ResourcePlannerRevision;
    }

    private bool TryValidateFrameOpPlannerContextSet(
        FrameOp[] operations,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!FrameOpResourcePlannerSwitchingEnabled || operations.Length == 0)
            return true;

        List<VulkanFrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(operations, keys);
        int contextCount = keys.Count;
        keys.Clear();
        if (contextCount <= MaxFrameOpResourcePlannerSwitchingStates)
            return true;

        failureReason =
            $"frame-plan rejected: {contextCount} Vulkan planner contexts exceed the bounded independent-plan capacity {MaxFrameOpResourcePlannerSwitchingStates}";
        RecordFrameOpPlannerContextRejection(contextCount, failureReason);
        return false;
    }

    private bool TryRestoreSealedFramePlanPlannerStates(
        FramePlan framePlan,
        out string failureReason)
    {
        failureReason = string.Empty;
        ReadOnlySpan<VulkanFrameOpPlannerStateKey> requiredKeys =
            framePlan.StaticPlannerContextKeys;
        if (!FrameOpResourcePlannerSwitchingEnabled || requiredKeys.IsEmpty)
            return true;

        if (requiredKeys.Length > MaxFrameOpResourcePlannerSwitchingStates)
        {
            failureReason =
                $"sealed frame plan requires {requiredKeys.Length} Vulkan planner contexts, exceeding capacity {MaxFrameOpResourcePlannerSwitchingStates}";
            return false;
        }

        FrameOpResourcePlannerSwitchingState switchingState =
            ActiveFrameOpResourcePlannerSwitchingState;
        for (int keyIndex = 0; keyIndex < requiredKeys.Length; keyIndex++)
        {
            VulkanFrameOpPlannerStateKey key = requiredKeys[keyIndex];
            if (switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState plannerState) &&
                IsReusableFrameOpResourcePlannerState(plannerState))
            {
                continue;
            }

            failureReason =
                $"sealed frame-plan planner context is not prepared pipe={key.PipelineIdentity} viewport={key.ViewportIdentity}";
            return false;
        }

        switchingState.ActiveKeys.Clear();
        for (int keyIndex = 0; keyIndex < requiredKeys.Length; keyIndex++)
        {
            VulkanFrameOpPlannerStateKey key = requiredKeys[keyIndex];
            switchingState.ActiveKeys.Add(key);
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
        }

        ResetActiveFrameOpResourcePlannerState(switchingState);
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        return true;
    }

    private void RejectMixedFrameOpPlannerContexts(FrameOp[] operations)
    {
        if (!FrameOpResourcePlannerSwitchingEnabled || operations.Length == 0)
            return;

        List<VulkanFrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(operations, keys);
        int contextCount = keys.Count;
        keys.Clear();
        if (contextCount <= 1)
            return;

        throw new VulkanPlanPreconditionException(
            $"Frame-plan preparation rejected {contextCount} mixed Vulkan planner contexts; independent partitions are required.");
    }

    private bool TryPrepareIndependentFrameOpResourcePlannerStates(
        FrameOp[] operations,
        ulong frameOperationsSignature,
        string refreshReason,
        out bool handled,
        out ulong plannerRevision,
        out string failureReason)
    {
        handled = false;
        plannerRevision = CaptureResourcePlannerRuntimeState().ResourcePlannerRevision;
        failureReason = string.Empty;
        if (!FrameOpResourcePlannerSwitchingEnabled || operations.Length == 0)
            return true;

        List<VulkanFrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(operations, keys);
        if (keys.Count <= 1)
        {
            keys.Clear();
            return true;
        }

        handled = true;
        if (keys.Count > MaxFrameOpResourcePlannerSwitchingStates)
        {
            failureReason =
                $"frame-plan rejected: {keys.Count} Vulkan planner contexts exceed independent-plan capacity {MaxFrameOpResourcePlannerSwitchingStates}";
            keys.Clear();
            return false;
        }

        // Preserve the sorted key set while the shared scratch list is reused by
        // the single-context preparation helpers. The bounded renderer-owned
        // buffer avoids a per-frame array allocation.
        int planKeyCount = keys.Count;
        for (int keyIndex = 0; keyIndex < planKeyCount; keyIndex++)
            _frameOpPlannerPartitionKeyBuffer[keyIndex] = keys[keyIndex];
        keys.Clear();
        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        FrameOpResourcePlannerSwitchingState switchingState =
            ActiveFrameOpResourcePlannerSwitchingState;
        InvalidatePreparedFrameOpResourcePlan(switchingState);
        switchingState.ActiveKeys.Clear();
        ResetActiveFrameOpResourcePlannerState(switchingState);

        for (int keyIndex = 0; keyIndex < planKeyCount; keyIndex++)
        {
            VulkanFrameOpPlannerStateKey key =
                _frameOpPlannerPartitionKeyBuffer[keyIndex];
            FrameOp[] partition = GetFrameOpPlannerPartition(
                operations,
                frameOperationsSignature,
                key);
            ulong partitionSignature = ComputeFrameOpPlannerPartitionSignature(
                frameOperationsSignature,
                key);

            using FrameOpResourcePlannerPreparationScope preparationScope =
                new(this, partition);
            FrameOpContext plannerContext =
                PrepareResourcePlannerForFrameOps(partition, partitionSignature);
            if (TryDescribeRecentResourceAllocationFailure(out failureReason) ||
                !TryRefreshFrameOpResourceWrappers(
                    partition,
                    plannerContext,
                    refreshReason,
                    AllowSynchronousResourceUploads,
                    out failureReason))
            {
                RestoreUsableFrameOpPlannerState(previousState);
                return false;
            }

            preparationScope.PublishCurrentState();
            _ = PrepareFrameOpResourcePlannerStatesForFrameOps(
                partition,
                partitionSignature,
                preserveActiveKeys: true);
            if (!switchingState.States.ContainsKey(key))
            {
                failureReason =
                    $"frame-plan preparation did not publish context-local planner state pipe={key.PipelineIdentity} viewport={key.ViewportIdentity}";
                RestoreUsableFrameOpPlannerState(previousState);
                return false;
            }
        }

        switchingState.ActiveKeys.Clear();
        for (int keyIndex = 0; keyIndex < planKeyCount; keyIndex++)
        {
            VulkanFrameOpPlannerStateKey key =
                _frameOpPlannerPartitionKeyBuffer[keyIndex];
            switchingState.ActiveKeys.Add(key);
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
        }

        ResetActiveFrameOpResourcePlannerState(switchingState);
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        plannerRevision = ComputeActiveFrameOpResourcePlannerStatesSignature();
        RestoreUsableFrameOpPlannerState(previousState);
        AssertFrameOpPlannerAllocatorOwnership(switchingState);
        return true;
    }

    private void RestoreUsableFrameOpPlannerState(in ResourcePlannerRuntimeState state)
    {
        RestoreResourcePlannerRuntimeState(
            state.ResourceAllocator is not null && state.ResourceAllocator.IsRetired
                ? ResourcePlannerRuntimeState.CreateEmpty()
                : state);
    }

    private FrameOp[] GetFrameOpPlannerPartition(
        FrameOp[] operations,
        ulong frameOperationsSignature,
        in VulkanFrameOpPlannerStateKey key)
    {
        if (_frameOpPlannerPartitionSignature != frameOperationsSignature)
        {
            _frameOpPlannerPartitionCache.Clear();
            _frameOpPlannerPartitionSignature = frameOperationsSignature;
        }

        if (_frameOpPlannerPartitionCache.TryGetValue(key, out FrameOp[]? cached))
            return cached;

        int count = 0;
        for (int index = 0; index < operations.Length; index++)
            if (FrameOpContextMatchesPlannerStateKey(operations[index].Context, key))
                count++;

        FrameOp[] partition = new FrameOp[count];
        int writeIndex = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            if (FrameOpContextMatchesPlannerStateKey(operation.Context, key))
                partition[writeIndex++] = operation;
        }

        _frameOpPlannerPartitionCache[key] = partition;
        return partition;
    }

    private static ulong ComputeFrameOpPlannerPartitionSignature(
        ulong frameOperationsSignature,
        in VulkanFrameOpPlannerStateKey key)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(frameOperationsSignature);
        hash.Add((int)key.ContextKind);
        hash.Add(key.PipelineIdentity);
        hash.Add(key.ViewportIdentity);
        hash.Add(key.DisplayWidth);
        hash.Add(key.DisplayHeight);
        hash.Add(key.InternalWidth);
        hash.Add(key.InternalHeight);
        hash.Add(key.OutputFrameBufferIdentity);
        hash.Add(key.OutputTargetIdentity);
        hash.Add(key.ResourceRegistrySignature);
        hash.Add(key.PassMetadataSignature);
        hash.Add(key.ResourceGeneration);
        hash.Add(key.SubmissionQueueFamily);
        return hash.ToHash();
    }

    private void RecordFrameOpPlannerContextRejection(
        int contextCount,
        string reason)
    {
        Debug.VulkanWarningEvery(
            $"Vulkan.ResourcePlanner.IncompatibleContextSet.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Rejecting frame before recording. Contexts={0} Reason={1}",
            contextCount,
            reason);
    }

    private static void ResetActiveFrameOpResourcePlannerState(FrameOpResourcePlannerSwitchingState switchingState)
    {
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.ActiveKey = default;
    }

    private void MarkFrameOpResourcePlannerStateUsed(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey key)
    {
        switchingState.LastUsedSerials[key] = ++switchingState.UsageSerial;
    }

    private static bool IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey key,
        VulkanResourceAllocator? allocator)
    {
        bool allocatorIsUsable = allocator is not null && !allocator.IsRetired;
        bool preparationOwnsAllocator =
            allocatorIsUsable &&
            switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
        bool anotherKeyOwnsAllocator = false;

        if (allocatorIsUsable)
        {
            foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
            {
                if (pair.Key.Equals(key))
                    continue;

                if (!ReferenceEquals(pair.Value.ResourceAllocator, allocator))
                    continue;

                anotherKeyOwnsAllocator = true;
                break;
            }
        }

        return CanReuseFrameOpPlannerAllocator(
            allocatorIsUsable,
            preparationOwnsAllocator,
            anotherKeyOwnsAllocator);
    }

    internal static bool CanReuseFrameOpPlannerAllocator(
        bool allocatorIsUsable,
        bool preparationOwnsAllocator,
        bool anotherKeyOwnsAllocator)
        => allocatorIsUsable &&
           !preparationOwnsAllocator &&
           !anotherKeyOwnsAllocator;

    private void PruneFrameOpResourcePlannerStatesToCapacity(FrameOpResourcePlannerSwitchingState switchingState)
    {
        if (switchingState.States.Count <= MaxFrameOpResourcePlannerSwitchingStates)
            return;

        List<VulkanFrameOpPlannerStateKey> staleKeys = _frameOpPlannerStateEvictionScratch;
        staleKeys.Clear();
        foreach (VulkanFrameOpPlannerStateKey key in switchingState.States.Keys)
        {
            if (switchingState.ActiveKeys.Contains(key))
                continue;

            staleKeys.Add(key);
        }

        int pruneCount = Math.Min(
            staleKeys.Count,
            switchingState.States.Count - MaxFrameOpResourcePlannerSwitchingStates);
        if (pruneCount <= 0)
        {
            staleKeys.Clear();
            return;
        }

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        int prunedCount = 0;
        int retirementDeferralCount = 0;
        for (int i = 0; i < pruneCount; i++)
        {
            if (!TryPopOldestFrameOpResourcePlannerStateKey(switchingState, staleKeys, out VulkanFrameOpPlannerStateKey key))
                break;

            if (!switchingState.States.Remove(key, out ResourcePlannerRuntimeState state))
                continue;

            switchingState.LastUsedSerials.Remove(key);
            if (!IsAllocatorOwnedByFrameOpPlannerState(switchingState, state.ResourceAllocator))
            {
                RestoreResourcePlannerRuntimeState(state);
                if (ResourceAllocator.TryRetirePhysicalResources(this))
                    retirementDeferralCount++;
            }
            prunedCount++;
        }

        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);

        if (prunedCount > 0)
        {
            InvalidatePreparedFrameOpResourcePlan(switchingState);
            RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                new FrameOutputWorkTelemetry(
                    PlannerPrunes: prunedCount,
                    PlannerEvictionDeferrals: retirementDeferralCount));
        }

        staleKeys.Clear();
        if (prunedCount == 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.FrameOpContextStatePruned.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Pruned {0} cached frame-op planner state(s) to stay under capacity; physical resources remain timeline-retired. Remaining={1} Cap={2}",
            prunedCount,
            switchingState.States.Count,
            MaxFrameOpResourcePlannerSwitchingStates);
    }

    private static ulong GetFrameOpResourcePlannerStateLastUsedSerial(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey key)
        => switchingState.LastUsedSerials.TryGetValue(key, out ulong serial)
            ? serial
            : 0UL;

    private static bool TryPopOldestFrameOpResourcePlannerStateKey(
        FrameOpResourcePlannerSwitchingState switchingState,
        List<VulkanFrameOpPlannerStateKey> keys,
        out VulkanFrameOpPlannerStateKey key)
    {
        int oldestIndex = -1;
        ulong oldestSerial = ulong.MaxValue;
        for (int i = 0; i < keys.Count; i++)
        {
            ulong serial = GetFrameOpResourcePlannerStateLastUsedSerial(switchingState, keys[i]);
            if (oldestIndex >= 0 && serial >= oldestSerial)
                continue;

            oldestIndex = i;
            oldestSerial = serial;
        }

        if (oldestIndex < 0)
        {
            key = default;
            return false;
        }

        key = keys[oldestIndex];
        keys.RemoveAt(oldestIndex);
        return true;
    }

    private void CollectFrameOpPlannerStateKeys(FrameOp[] ops, List<VulkanFrameOpPlannerStateKey> keys)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOpContext context = ops[i].Context;
            if (!FrameOpContextHasPlannerResources(context))
                continue;

            VulkanFrameOpPlannerStateKey key = BuildFrameOpPlannerStateKey(context);
            if (!keys.Contains(key))
                keys.Add(key);
        }

        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ContextKind.CompareTo(right.ContextKind);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.DisplayWidth.CompareTo(right.DisplayWidth);
            if (compare != 0)
                return compare;

            compare = left.DisplayHeight.CompareTo(right.DisplayHeight);
            if (compare != 0)
                return compare;

            compare = left.InternalWidth.CompareTo(right.InternalWidth);
            if (compare != 0)
                return compare;

            compare = left.InternalHeight.CompareTo(right.InternalHeight);
            if (compare != 0)
                return compare;

            compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
            if (compare != 0)
                return compare;

            compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistrySignature.CompareTo(right.ResourceRegistrySignature);
            if (compare != 0)
                return compare;

            compare = left.PassMetadataSignature.CompareTo(right.PassMetadataSignature);
            if (compare != 0)
                return compare;

            compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
            if (compare != 0)
                return compare;

            compare = left.SubmissionQueueFamily.CompareTo(right.SubmissionQueueFamily);
            return compare;
        });
    }

    private static bool TryGetSingleFrameOpPlannerStateKey(
        FrameOp[] ops,
        out VulkanFrameOpPlannerStateKey key)
    {
        key = default;
        bool found = false;
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOpContext context = ops[i].Context;
            if (!FrameOpContextHasPlannerResources(context))
                continue;

            VulkanFrameOpPlannerStateKey candidate = BuildFrameOpPlannerStateKey(context);
            if (!found)
            {
                key = candidate;
                found = true;
                continue;
            }

            if (!candidate.Equals(key))
            {
                key = default;
                return false;
            }
        }

        return found;
    }


    private ulong ComputeActiveFrameOpResourcePlannerStatesSignature()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        FrameOpSignatureHasher hash = new();
        List<VulkanFrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
            keys.Add(key);
        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ContextKind.CompareTo(right.ContextKind);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.DisplayWidth.CompareTo(right.DisplayWidth);
            if (compare != 0)
                return compare;

            compare = left.DisplayHeight.CompareTo(right.DisplayHeight);
            if (compare != 0)
                return compare;

            compare = left.InternalWidth.CompareTo(right.InternalWidth);
            if (compare != 0)
                return compare;

            compare = left.InternalHeight.CompareTo(right.InternalHeight);
            if (compare != 0)
                return compare;

            compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
            if (compare != 0)
                return compare;

            compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistrySignature.CompareTo(right.ResourceRegistrySignature);
            if (compare != 0)
                return compare;

            compare = left.PassMetadataSignature.CompareTo(right.PassMetadataSignature);
            if (compare != 0)
                return compare;

            compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
            if (compare != 0)
                return compare;

            compare = left.SubmissionQueueFamily.CompareTo(right.SubmissionQueueFamily);
            return compare;
        });

        // This value keys recorded command-chain work, so describe the active
        // planner/allocation states rather than their input-cache keys. Registry
        // and resource generations can change for descriptor/data publication
        // without replacing a physical plan or its barrier topology.
        hash.Add(keys.Count);

        for (int i = 0; i < keys.Count; i++)
        {
            VulkanFrameOpPlannerStateKey key = keys[i];
            hash.Add((int)key.ContextKind);
            hash.Add(key.PipelineIdentity);
            hash.Add(key.ViewportIdentity);
            hash.Add(key.DisplayWidth);
            hash.Add(key.DisplayHeight);
            hash.Add(key.InternalWidth);
            hash.Add(key.InternalHeight);
            hash.Add(key.OutputFrameBufferIdentity);
            hash.Add(key.OutputTargetIdentity);
            hash.Add(key.SubmissionQueueFamily);

            if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            {
                hash.Add(0);
                continue;
            }

            hash.Add(state.AllocatorOwnershipId);
            hash.Add(state.ResourcePlannerRevision);
            hash.Add(state.ResourcePlannerSignature);
            hash.Add(state.ResourceAllocationSignature);
        }

        keys.Clear();
        return hash.ToHash();
    }

    private FrameOpResourcePlannerRecordingScope EnterFrameOpResourcePlannerRecordingScope()
        => new(this);

    private bool TryActivateFrameOpResourcePlannerState(in FrameOpContext context)
    {
        if (!_deviceContext.IsOperational)
            return false;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (switchingState.ActiveKeys.Count == 0 ||
            !FrameOpContextHasPlannerResources(context))
        {
            return false;
        }

        if (!TryFindActiveFrameOpPlannerStateKey(context, switchingState, out VulkanFrameOpPlannerStateKey key))
            return false;

        if (switchingState.HasActiveKey &&
            key.Equals(switchingState.ActiveKey))
        {
            switchingState.ActiveContext = context;
            switchingState.HasActiveContext = true;
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
            return true;
        }

        SaveActiveFrameOpResourcePlannerState();

        if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            return false;

        AssertFrameOpPlannerStateMatchesContext(state, key, context);
        RestoreResourcePlannerRuntimeState(state);
        switchingState.ActiveKey = key;
        switchingState.HasActiveKey = true;
        switchingState.ActiveContext = context;
        switchingState.HasActiveContext = true;
        MarkFrameOpResourcePlannerStateUsed(switchingState, key);
        return true;
    }

    private static bool TryFindActiveFrameOpPlannerStateKey(
        in FrameOpContext context,
        FrameOpResourcePlannerSwitchingState switchingState,
        out VulkanFrameOpPlannerStateKey key)
    {
        foreach (VulkanFrameOpPlannerStateKey activeKey in switchingState.ActiveKeys)
        {
            if (!FrameOpContextMatchesPlannerStateKey(context, activeKey))
                continue;

            key = activeKey;
            return true;
        }

        key = default;
        return false;
    }

    private string DescribeActiveFrameOpPlannerStateKeys(
        in FrameOpContext requestedContext,
        FramePlan? framePlan)
    {
        VulkanFrameOpPlannerStateKey requested = BuildFrameOpPlannerStateKey(requestedContext);
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        StringBuilder builder = new();
        builder.Append("requested=").Append(requested)
            .Append(" sealedCount=").Append(framePlan?.StaticPlannerContextKeys.Length ?? 0)
            .Append(" activeCount=").Append(switchingState.ActiveKeys.Count)
            .Append(" active=[");
        bool first = true;
        foreach (VulkanFrameOpPlannerStateKey key in switchingState.ActiveKeys)
        {
            if (!first)
                builder.Append("; ");
            first = false;
            builder.Append(key);
        }
        builder.Append(']');
        return builder.ToString();
    }

    private void SaveActiveFrameOpResourcePlannerState()
    {
        if (!_deviceContext.IsOperational)
            return;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!switchingState.RecordingScopeActive ||
            !switchingState.HasActiveKey ||
            !switchingState.HasActiveContext)
            return;

        ResourcePlannerRuntimeState state = CaptureResourcePlannerRuntimeState();
        state.LastActiveFrameOpContext = switchingState.ActiveContext;
        switchingState.States[switchingState.ActiveKey] = state;
        MarkFrameOpResourcePlannerStateUsed(switchingState, switchingState.ActiveKey);
    }

    private void DestroyFrameOpResourcePlannerStates()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        InvalidatePreparedFrameOpResourcePlan(switchingState);
        if (switchingState.States.Count == 0 && !switchingState.HasPreparationState)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        HashSet<VulkanResourceAllocator> retiredAllocators = new(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                pair.Value,
                retiredAllocators,
                $"FrameOpResourcePlannerStateDestroy.pipe{pair.Key.PipelineIdentity}.vp{pair.Key.ViewportIdentity}");
        }

        if (switchingState.HasPreparationState)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                switchingState.PreparationState,
                retiredAllocators,
                "FrameOpResourcePlannerPreparationStateDestroy");
        }

        switchingState.States.Clear();
        switchingState.LastUsedSerials.Clear();
        switchingState.ActiveKeys.Clear();
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.PreparationState = default;
        switchingState.HasPreparationState = false;
        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
    }

    private static bool IsAllocatorOwnedByFrameOpPlannerState(
        FrameOpResourcePlannerSwitchingState switchingState,
        VulkanResourceAllocator allocator)
    {
        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
        {
            if (ReferenceEquals(state.ResourceAllocator, allocator))
                return true;
        }

        return switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
    }

    private void RetireResourcePlannerRuntimeStateAllocator(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        VulkanResourceAllocator allocator = state.ResourceAllocator;
        if (allocator is null || !retiredAllocators.Add(allocator) || allocator.IsRetired)
            return;

        RestoreResourcePlannerRuntimeState(state);
        _ = allocator.TryRetirePhysicalResources(this);
    }

    private void RetireResourcePlannerRuntimeStateAllocators(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        RetireResourcePlannerRuntimeStateAllocator(state, retiredAllocators, reason);

        FrameOpResourcePlannerSwitchingState? switchingState = state.FrameOpResourcePlannerSwitchingState;
        if (switchingState is null)
            return;

        foreach (ResourcePlannerRuntimeState nestedState in switchingState.States.Values)
            RetireResourcePlannerRuntimeStateAllocator(nestedState, retiredAllocators, reason);

        if (switchingState.HasPreparationState)
            RetireResourcePlannerRuntimeStateAllocator(switchingState.PreparationState, retiredAllocators, reason);
    }

    [Conditional("DEBUG")]
    private static void AssertResourcePlannerRuntimeStateCanBeRestored(in ResourcePlannerRuntimeState state)
    {
        if (state.ResourcePlanner is null)
            throw new InvalidOperationException("A cached frame-op planner state has no resource planner.");
        if (state.ResourceAllocator is null)
            throw new InvalidOperationException("A cached frame-op planner state has no resource allocator.");
        if (state.BarrierPlanner is null)
            throw new InvalidOperationException("A cached frame-op planner state has no barrier planner.");
        if (state.ResourceAllocator.OwnershipId != state.AllocatorOwnershipId)
            throw new InvalidOperationException(
                $"Cached frame-op planner allocator ownership changed from {state.AllocatorOwnershipId} to {state.ResourceAllocator.OwnershipId}.");

        if (state.ResourceAllocator.IsRetired)
            throw new InvalidOperationException($"Cached frame-op planner allocator owner {state.AllocatorOwnershipId} is retired.");
    }

    [Conditional("DEBUG")]
    private void AssertFrameOpPlannerAllocatorOwnership(FrameOpResourcePlannerSwitchingState switchingState)
    {
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> first in switchingState.States)
        {
            AssertResourcePlannerRuntimeStateCanBeRestored(first.Value);
            foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> second in switchingState.States)
            {
                if (first.Key.Equals(second.Key))
                    continue;

                if (ReferenceEquals(first.Value.ResourceAllocator, second.Value.ResourceAllocator))
                    throw new InvalidOperationException(
                        $"Frame-op planner states {first.Key} and {second.Key} share allocator owner {first.Value.AllocatorOwnershipId} without an explicit sharing policy.");
            }

            if (switchingState.HasPreparationState)
            {
                if (ReferenceEquals(first.Value.ResourceAllocator, switchingState.PreparationState.ResourceAllocator))
                    throw new InvalidOperationException(
                        $"Frame-op planner state {first.Key} shares allocator owner {first.Value.AllocatorOwnershipId} with the merged preparation state.");
            }
        }

        if (switchingState.HasPreparationState)
            AssertResourcePlannerRuntimeStateCanBeRestored(switchingState.PreparationState);
    }

    [Conditional("DEBUG")]
    private static void AssertFrameOpPlannerStateMatchesContext(
        in ResourcePlannerRuntimeState state,
        in VulkanFrameOpPlannerStateKey key,
        in FrameOpContext context)
    {
        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        if (context.ResourceGeneration != key.ResourceGeneration)
            throw new InvalidOperationException(
                $"Frame-op planner context generation {context.ResourceGeneration} does not match key generation {key.ResourceGeneration} for {key}.");

        if (state.LastActiveFrameOpContext is not FrameOpContext lastContext)
            return;

        // Keyed allocators intentionally retain the merged registry used to build
        // the physical plan. Its registry signature is therefore a superset of
        // the original frame-op key even though the stable execution identity
        // must continue to match that key.
        if (!FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(lastContext, key))
            throw new InvalidOperationException($"Cached frame-op planner context does not match key {key}.");
        if (!FrameOpContextMatchesPlannerStateKey(context, key))
            throw new InvalidOperationException($"Active frame-op planner context does not match key {key}.");
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops, in VulkanFrameOpPlannerStateKey key)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops, in VulkanFrameOpPlannerStateKey key)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static void AddFrameBufferName(HashSet<string> frameBufferNames, XRFrameBuffer? frameBuffer)
    {
        string? name = frameBuffer?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            frameBufferNames.Add(name);
    }

    private static bool FrameOpContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null ||
            context.PassMetadata is { Count: > 0 };

    internal static VulkanFrameOpPlannerStateKey BuildFrameOpPlannerStateKey(in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            context.LogicalViewId,
            ResolveFrameOpContextResourceRegistrySignature(context),
            ComputePassMetadataSignature(context.PassMetadata),
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.SubmissionQueueFamily);

    internal static VulkanInteractiveResizePlannerContextKey BuildInteractiveResizePlannerContextKey(
        in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context));

    /// <summary>
    /// Returns the physical-plan identity for an output. Command recording continues to use the
    /// concrete target identity, but rotating desktop target/FBO instances must not manufacture a
    /// new allocator owner when their pipeline, named attachment contract, and extent are compatible.
    /// </summary>
    internal static int ResolveResourcePlanOutputTargetIdentity(in FrameOpContext context)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return context.OutputTargetIdentity;

        if (context.OutputFrameBufferIdentity != 0)
            return context.OutputFrameBufferIdentity;

        return HashCode.Combine(
            (int)context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity);
    }

    private static bool FrameOpMatchesPlannerStateKey(FrameOp op, in VulkanFrameOpPlannerStateKey key)
        => FrameOpContextHasPlannerResources(op.Context) &&
            FrameOpContextMatchesPlannerStateKey(op.Context, key);

    private static bool FrameOpContextMatchesPlannerStateKey(in FrameOpContext context, in VulkanFrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            context.LogicalViewId == key.LogicalViewId &&
            ResolveFrameOpContextResourceRegistrySignature(context) == key.ResourceRegistrySignature &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.DescriptorGeneration == key.DescriptorGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    private static bool FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(
        in FrameOpContext context,
        in VulkanFrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            context.LogicalViewId == key.LogicalViewId &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.DescriptorGeneration == key.DescriptorGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    private static int ComputeOutputFrameBufferIdentity(string? outputFrameBufferName)
        => string.IsNullOrWhiteSpace(outputFrameBufferName)
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(outputFrameBufferName!);

    private static int ResolveFrameOpContextResourceRegistrySignature(in FrameOpContext context)
        => context.ResourceRegistrySignatureSnapshot ?? ComputeResourceRegistrySignature(context.ResourceRegistry);

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(FrameOpContext context, FrameOp[] ops)
    {
        VulkanFrameOpPlannerStateKey ignoredKey = default;
        return RefreshPlannerExtentsFromLiveContext(context, ops, filterByPlannerKey: false, ignoredKey);
    }

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(
        FrameOpContext context,
        FrameOp[] ops,
        bool filterByPlannerKey,
        in VulkanFrameOpPlannerStateKey plannerKey)
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var (DisplayWidth, DisplayHeight, InternalWidth, InternalHeight) = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                context.PipelineInstance?.ResourceInternalWidth,
                context.PipelineInstance?.ResourceInternalHeight,
                viewportInternalWidth: null,
                viewportInternalHeight: null,
                contextInternalWidth: context.InternalWidth,
                contextInternalHeight: context.InternalHeight);
            if (context.DisplayWidth == DisplayWidth &&
                context.DisplayHeight == DisplayHeight &&
                context.InternalWidth == InternalWidth &&
                context.InternalHeight == InternalHeight)
                return context;

            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.ExternalFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Refreshing external swapchain frame-op planner extents. Old={0}x{1}/{2}x{3} New={4}x{5}/{6}x{7}.",
                context.DisplayWidth,
                context.DisplayHeight,
                context.InternalWidth,
                context.InternalHeight,
                DisplayWidth,
                DisplayHeight,
                InternalWidth,
                InternalHeight);

            return RefreshFrameOpContextRecordingFingerprint(context with
            {
                DisplayWidth = DisplayWidth,
                DisplayHeight = DisplayHeight,
                InternalWidth = InternalWidth,
                InternalHeight = InternalHeight
            });
        }

        if (XRWindow.IsInteractiveResizeInProgress)
            return ApplyInteractiveResizePlannerFreeze(context);

        if (IsRenderingExternalSwapchainTarget)
            return context;

        FrameOpContext live = CaptureFrameOpContextOrLastActive();
        bool refreshExtents =
            ReferenceEquals(context.PipelineInstance, live.PipelineInstance) ||
            ReferenceEquals(context.ResourceRegistry, live.ResourceRegistry);

        if (!refreshExtents)
        {
            foreach (FrameOp op in ops)
            {
                if (filterByPlannerKey && !FrameOpMatchesPlannerStateKey(op, plannerKey))
                    continue;

                if (VulkanSwapchainContextCoalescer.TargetsSwapchain(op))
                {
                    refreshExtents = true;
                    break;
                }
            }
        }

        if (!refreshExtents)
            return context;

        uint displayWidth = live.DisplayWidth > 0 ? live.DisplayWidth : context.DisplayWidth;
        uint displayHeight = live.DisplayHeight > 0 ? live.DisplayHeight : context.DisplayHeight;
        // A swapchain-target operation may refresh its display dimensions from
        // the acquired target, but its internal render allocation belongs to
        // the captured pipeline/viewport context. Never borrow the ambient
        // live viewport's internal extent for a different planner owner.
        bool exactPlannerOwner =
            ReferenceEquals(context.PipelineInstance, live.PipelineInstance) &&
            context.ViewportIdentity == live.ViewportIdentity &&
            context.OutputTargetIdentity == live.OutputTargetIdentity &&
            context.OutputFrameBufferIdentity == live.OutputFrameBufferIdentity;
        uint internalWidth = exactPlannerOwner && live.InternalWidth > 0
            ? live.InternalWidth
            : context.PipelineInstance?.ResourceInternalWidth is uint pipelineInternalWidth && pipelineInternalWidth > 0
                ? pipelineInternalWidth
                : context.InternalWidth;
        uint internalHeight = exactPlannerOwner && live.InternalHeight > 0
            ? live.InternalHeight
            : context.PipelineInstance?.ResourceInternalHeight is uint pipelineInternalHeight && pipelineInternalHeight > 0
                ? pipelineInternalHeight
                : context.InternalHeight;

        if (displayWidth == context.DisplayWidth &&
            displayHeight == context.DisplayHeight &&
            internalWidth == context.InternalWidth &&
            internalHeight == context.InternalHeight)
        {
            return context;
        }

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.RefreshFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Refreshing frame-op planner extents from live viewport. Old={0}x{1}/{2}x{3} Live={4}x{5}/{6}x{7}.",
                context.DisplayWidth,
                context.DisplayHeight,
                context.InternalWidth,
                context.InternalHeight,
                displayWidth,
                displayHeight,
                internalWidth,
                internalHeight);
        }

        return RefreshFrameOpContextRecordingFingerprint(context with
        {
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
            InternalWidth = internalWidth,
            InternalHeight = internalHeight
        });
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)
    {
        FrameOpContext fallback = ops[0].Context;
        FrameOpContext best = fallback;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            FrameOpContext context = op.Context;
            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanSwapchainContextCoalescer.TargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return best;
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops, in VulkanFrameOpPlannerStateKey key)
    {
        FrameOpContext best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            FrameOpContext context = op.Context;
            if (!hasBest)
            {
                best = context;
                hasBest = true;
            }

            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanSwapchainContextCoalescer.TargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return hasBest ? best : SelectPrimaryPlannerContext(ops);
    }

    private static int ScoreFrameOpFrameBufferTargets(FrameOp op, RenderResourceRegistry registry)
    {
        int score = ScoreFrameOpFrameBufferTarget(op.Context.OutputFrameBuffer, registry);
        score += ScoreFrameOpFrameBufferTarget(op.Target, registry);
        if (op is BlitOp blit)
        {
            score += ScoreFrameOpFrameBufferTarget(blit.InFbo, registry);
            score += ScoreFrameOpFrameBufferTarget(blit.OutFbo, registry);
        }

        return score;
    }

    private static int ScoreFrameOpFrameBufferTarget(XRFrameBuffer? target, RenderResourceRegistry registry)
    {
        if (target is null)
            return 0;

        return !string.IsNullOrWhiteSpace(target.Name) &&
            registry.FrameBufferRecords.ContainsKey(target.Name)
                ? 256
                : 32;
    }

    private static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
    {
        int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
        if (compare != 0)
            return compare;

        compare = ((int)left.ContextKind).CompareTo((int)right.ContextKind);
        if (compare != 0)
            return compare;

        compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
        if (compare != 0)
            return compare;

        compare = ResolveFrameOpContextResourceRegistrySignature(left)
            .CompareTo(ResolveFrameOpContextResourceRegistrySignature(right));
        if (compare != 0)
            return compare;

        compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
        if (compare != 0)
            return compare;

        compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
        if (compare != 0)
            return compare;

        compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
        if (compare != 0)
            return compare;

        compare = left.DescriptorGeneration.CompareTo(right.DescriptorGeneration);
        if (compare != 0)
            return compare;

        return ComputePassMetadataSignature(left.PassMetadata).CompareTo(ComputePassMetadataSignature(right.PassMetadata));
    }

    private static uint ResolvePositiveDimension(uint? primary, int? secondary, uint tertiary, uint fallback)
    {
        if (primary.HasValue && primary.Value > 0)
            return primary.Value;

        if (secondary.HasValue && secondary.Value > 0)
            return (uint)secondary.Value;

        return tertiary > 0 ? tertiary : fallback;
    }

    internal static (uint DisplayWidth, uint DisplayHeight, uint InternalWidth, uint InternalHeight) ResolveExternalFrameOpResourceDimensions(
        in Extent2D externalExtent,
        uint? pipelineInternalWidth,
        uint? pipelineInternalHeight,
        int? viewportInternalWidth,
        int? viewportInternalHeight,
        uint contextInternalWidth = 0u,
        uint contextInternalHeight = 0u)
    {
        uint displayWidth = Math.Max(externalExtent.Width, 1u);
        uint displayHeight = Math.Max(externalExtent.Height, 1u);
        uint internalWidth = ResolvePositiveDimension(
            pipelineInternalWidth,
            viewportInternalWidth,
            contextInternalWidth,
            displayWidth);
        uint internalHeight = ResolvePositiveDimension(
            pipelineInternalHeight,
            viewportInternalHeight,
            contextInternalHeight,
            displayHeight);

        return (displayWidth, displayHeight, internalWidth, internalHeight);
    }

    private Extent2D ResolveFrameOpContextFallbackExtent()
        => TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent) ? externalExtent : OutputRuntime.Desktop.Extent;

    private VulkanResourceExtentContext BuildResourceExtentContext(in FrameOpContext context)
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var (DisplayWidth, DisplayHeight, InternalWidth, InternalHeight) = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                context.PipelineInstance?.ResourceInternalWidth,
                context.PipelineInstance?.ResourceInternalHeight,
                viewportInternalWidth: null,
                viewportInternalHeight: null,
                contextInternalWidth: context.InternalWidth,
                contextInternalHeight: context.InternalHeight);
            return new VulkanResourceExtentContext(
                DisplayWidth,
                DisplayHeight,
                InternalWidth,
                InternalHeight);
        }

        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        uint displayWidth = context.DisplayWidth > 0
            ? context.DisplayWidth
            : Math.Max(fallbackExtent.Width, 1u);
        uint displayHeight = context.DisplayHeight > 0
            ? context.DisplayHeight
            : Math.Max(fallbackExtent.Height, 1u);
        uint internalWidth = context.InternalWidth > 0
            ? context.InternalWidth
            : displayWidth;
        uint internalHeight = context.InternalHeight > 0
            ? context.InternalHeight
            : displayHeight;

        return new VulkanResourceExtentContext(
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);
    }

    private bool TryResolveExternalSwapchainTargetExtent(out Extent2D extent)
    {
        if (TryGetExternalSwapchainTargetRegion(out BoundingRectangle region) &&
            region.Width > 0 &&
            region.Height > 0)
        {
            extent = new Extent2D(
                (uint)region.Width,
                (uint)region.Height);
            return true;
        }

        if (IsRenderingExternalSwapchainTarget)
            throw new InvalidOperationException("OpenXR external swapchain rendering is active, but no valid external target extent is bound.");

        extent = default;
        return false;
    }


}
