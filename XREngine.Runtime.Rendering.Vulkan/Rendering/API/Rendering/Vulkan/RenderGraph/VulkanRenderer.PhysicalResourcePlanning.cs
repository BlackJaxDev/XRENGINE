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
    private void UpdateResourcePlannerFromContext(
        in FrameOpContext context,
        HashSet<int>? activePassIndices = null,
        HashSet<string>? activeFrameBufferNames = null,
        int activeResourceSetSignature = 0,
        bool constrainToActivePassSet = false)
    {
        if (!IsDeviceOperational)
            return;

        if (IsCommandChainResourcePlanFrozen)
            throw new InvalidOperationException(
                $"Resource planner cannot be replaced while command-chain readers are using frozen plan revision {_renderGraphRuntime.FrozenResourcePlanRevision}.");
        
        int activePassSetSignature = ComputeActivePassSetSignature(activePassIndices);
        ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(
            context,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);

        if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))
        {
            RecordPhysicalPlanCacheTelemetry(hit: true, planningInputs.CompiledGraph.Plan.Generation);
            return;
        }

        ulong plannerSignature = ComputeResourcePlannerSignature(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        if (plannerSignature == ActiveResourcePlannerSignature)
        {
            RecordPhysicalPlanCacheTelemetry(hit: true, planningInputs.CompiledGraph.Plan.Generation);
            RememberResourcePlannerFastPath(planningInputs.FastPathKey);
            return;
        }

        RecordPhysicalPlanCacheTelemetry(hit: false, planningInputs.CompiledGraph.Plan.Generation);

        ResourcePlannerSignatureBreakdown signatureBreakdown = ComputeResourcePlannerSignatureBreakdown(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.SignatureChange.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Signature changed for context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} Old=0x{4:X16} New=0x{5:X16} ChangedFields=[{6}] OldComponents=[{7}] NewComponents=[{8}] GraphPlanGeneration={9} GraphPlanIdentity=0x{10:X16}",
            context.ContextKind,
            context.ContextId,
            signatureBreakdown.CompatibilityFingerprint,
            ActiveResourcePlannerRevision,
            ActiveResourcePlannerSignature,
            plannerSignature,
            signatureBreakdown.DescribeDelta(ActiveResourcePlannerSignatureBreakdown),
            ActiveResourcePlannerSignatureBreakdown,
            signatureBreakdown,
            planningInputs.CompiledGraph.Plan.Generation,
            planningInputs.CompiledGraph.Plan.CompatibilityIdentity);

        VulkanResourcePlanner pendingPlanner = BuildResourceDescriptorPlan(context, planningInputs.ActivePassMetadata);
        PhysicalAllocationPlan allocationPlan = BuildPhysicalAllocationPlan(
            context,
            pendingPlanner,
            planningInputs.ActivePassMetadata);
        LogPhysicalAllocationPlanStatus(context, pendingPlanner, allocationPlan, planningInputs.ActivePassMetadata);

        VulkanResourceAllocator oldAllocator = ResourceAllocator;
        VulkanResourceAllocator? pendingAllocator = null;
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups = null;
        int retiredImageCount = 0;
        int retiredBufferCount = 0;
        if (allocationPlan.Changed)
        {
            if (TryDescribeRecentResourceAllocationFailure(out string recentAllocationFailureReason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.DeferRecentAllocationRetry.{context.PipelineIdentity}.{context.ViewportIdentity}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Deferring physical resource plan after recent allocation failure. Planner=0x{0:X16} Allocation=0x{1:X16}. Reason={2}",
                    plannerSignature,
                    allocationPlan.Signature,
                    recentAllocationFailureReason);
                return;
            }

            if (!TryBuildPhysicalAllocator(
                context,
                pendingPlanner,
                allocationPlan.ExtentContext,
                planningInputs.ActivePassMetadata,
                out pendingAllocator,
                out reusedImageGroups,
                out retiredImageCount,
                out retiredBufferCount))
            {
                RecordResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
                return;
            }

            ClearResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
        }

        ActiveResourcePlanner = pendingPlanner;
        if (pendingAllocator is not null)
        {
            ActiveResourceAllocator = pendingAllocator;
            pendingAllocator.CommitReusedPhysicalImageMetadata();
        }

        CommitPhysicalAllocatorPlan(
            allocationPlan.Changed,
            oldAllocator,
            reusedImageGroups,
            retiredImageCount,
            retiredBufferCount,
            plannerSignature,
            allocationPlan.Signature);
        RebuildRenderGraphAndBarriers(planningInputs, plannerSignature, allocationPlan.Signature);

        ActiveResourcePlannerSignature = plannerSignature;
        ActiveResourceAllocationSignature = allocationPlan.Signature;
        ActiveResourcePlannerSignatureBreakdown = signatureBreakdown;
        ActiveResourcePlannerRevision++;
        if (!HasThreadResourcePlannerRuntimeState)
            _renderGraphRuntime.PublishPlan();
        RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(
                PhysicalPlanGenerations: 1,
                PhysicalPlanAliasReuses: reusedImageGroups?.Count ?? 0,
                PlannerArenaHighWater: ActiveFrameOpResourcePlannerSwitchingState.States.Count,
                RenderGraphPlanGeneration: ClampGenerationToInt64(planningInputs.CompiledGraph.Plan.Generation)));
        RememberResourcePlannerFastPath(planningInputs.FastPathKey);
    }

    private void RecordPhysicalPlanCacheTelemetry(bool hit, ulong renderGraphPlanGeneration)
        => RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(
                PhysicalPlanCacheHits: hit ? 1 : 0,
                PhysicalPlanCacheMisses: hit ? 0 : 1,
                PlannerArenaHighWater: ActiveFrameOpResourcePlannerSwitchingState.States.Count,
                RenderGraphPlanGeneration: ClampGenerationToInt64(renderGraphPlanGeneration)));

    private static long ClampGenerationToInt64(ulong generation)
        => generation > long.MaxValue ? long.MaxValue : (long)generation;

    private ResourcePlanningInputs PrepareResourcePlanningInputs(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = FilterActivePassMetadata(
            context.PassMetadata,
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(activePassMetadata);
        ResourcePlannerFastPathKey fastPathKey = new(
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassMetadata,
            ComputePassMetadataRevisionStamp(activePassMetadata),
            activePassSetSignature,
            activeResourceSetSignature,
            ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            ResolveResourcePlanOutputTargetIdentity(context),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            queueOwnership,
            SupportsTransformFeedback);

        return new ResourcePlanningInputs(activePassMetadata, compiledGraph, queueOwnership, fastPathKey);
    }

    private bool CanReuseResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
        => ActiveHasResourcePlannerFastPathKey
            && ActiveResourcePlannerSignature != ulong.MaxValue
            && key.Matches(ActiveResourcePlannerFastPathKey);

    private void RememberResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
    {
        ActiveResourcePlannerFastPathKey = key;
        ActiveHasResourcePlannerFastPathKey = true;
    }

    private static VulkanResourcePlanner BuildResourceDescriptorPlan(
        in FrameOpContext context,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        VulkanResourcePlanner pendingPlanner = new();
        pendingPlanner.Sync(context.ResourceRegistry, context.OutputFrameBufferName);
        ValidateVulkanResourcePlanMetadata(activePassMetadata, pendingPlanner);
        return pendingPlanner;
    }

    private PhysicalAllocationPlan BuildPhysicalAllocationPlan(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        VulkanResourceExtentContext extentContext = BuildResourceExtentContext(context);
        ulong allocationSignature = ComputeResourceAllocationSignature(
            context,
            pendingPlanner,
            passMetadata,
            extentContext,
            SupportsTransformFeedback);
        return new PhysicalAllocationPlan(
            extentContext,
            allocationSignature,
            allocationSignature != ActiveResourceAllocationSignature);
    }

    private void LogPhysicalAllocationPlanStatus(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        in PhysicalAllocationPlan allocationPlan,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        if (allocationPlan.Changed)
        {
            ResourceAllocationSignatureBreakdown allocationBreakdown = ComputeResourceAllocationSignatureBreakdown(
                context,
                pendingPlanner,
                activePassMetadata,
                allocationPlan.ExtentContext,
                SupportsTransformFeedback);
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.PhysicalPlanChange.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Physical resource plan changed for context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} Old=0x{4:X16} New=0x{5:X16} Components=[{6}]",
                context.ContextKind,
                context.ContextId,
                context.RecordingFingerprint,
                ActiveResourcePlannerRevision,
                ActiveResourceAllocationSignature,
                allocationPlan.Signature,
                allocationBreakdown);
            return;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.PhysicalPlanReuse.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Reusing physical resource plan for metadata-only graph change in context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} AllocationSignature=0x{4:X16}",
            context.ContextKind,
            context.ContextId,
            context.RecordingFingerprint,
            ActiveResourcePlannerRevision,
            allocationPlan.Signature);
    }

    private bool ShouldDeferFailedResourceAllocationRetry(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature ||
            ActiveFailedResourceAllocationTimestamp == 0)
        {
            return false;
        }

        return Stopwatch.GetElapsedTime(ActiveFailedResourceAllocationTimestamp) <
            ResolveResourceAllocationFailureRetryDelay();
    }

    internal bool TryDescribeRecentResourceAllocationFailure(out string reason)
    {
        reason = string.Empty;

        long failureTimestamp = ActiveFailedResourceAllocationTimestamp;
        if (failureTimestamp == 0)
            return false;

        TimeSpan elapsed = Stopwatch.GetElapsedTime(failureTimestamp);
        TimeSpan retryDelay = ResolveResourceAllocationFailureRetryDelay();
        if (elapsed >= retryDelay)
            return false;

        reason =
            $"Vulkan resource planner is backing off after a failed physical allocation ({elapsed.TotalMilliseconds:F0}/{retryDelay.TotalMilliseconds:F0} ms, planner=0x{ActiveFailedResourcePlannerSignature:X16}, allocation=0x{ActiveFailedResourceAllocationSignature:X16})";
        return true;
    }

    private static TimeSpan ResolveResourceAllocationFailureRetryDelay()
    {
        IRuntimeRenderPresentationServices host = RuntimeRenderingHostServices.Presentation;
        return host.IsOpenXRActive || host.IsInVR
            ? OpenXrResourceAllocationFailureRetryDelay
            : ResourceAllocationFailureRetryDelay;
    }

    private void RecordResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        ActiveFailedResourcePlannerSignature = plannerSignature;
        ActiveFailedResourceAllocationSignature = allocationSignature;
        ActiveFailedResourceAllocationTimestamp = Stopwatch.GetTimestamp();
    }

    private void ClearResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature)
        {
            return;
        }

        ActiveFailedResourcePlannerSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationTimestamp = 0;
    }

    private bool TryBuildPhysicalAllocator(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        VulkanResourceExtentContext extentContext,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata,
        out VulkanResourceAllocator? pendingAllocator,
        out HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        out int retiredImageCount,
        out int retiredBufferCount)
    {
        pendingAllocator = new();
        reusedImageGroups = null;
        retiredImageCount = 0;
        retiredBufferCount = 0;

        try
        {
            pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                this,
                activePassMetadata,
                pendingPlanner,
                extentContext);
            int reusedImageCount = pendingAllocator.ReuseCompatiblePhysicalImagesFrom(
                ResourceAllocator,
                out reusedImageGroups);
            if (reusedImageCount > 0)
            {
                Debug.VulkanEvery(
                    "Vulkan.ResourcePlanner.PhysicalImageReuse",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Reused {0} compatible physical image groups from active plan before allocating pending plan.",
                    reusedImageCount);
            }

            if (!pendingAllocator.TryAllocatePhysicalImages(this, out string imageAllocationFailureReason))
            {
                if (IsExpectedVulkanImageAllocationDeferral(imageAllocationFailureReason))
                {
                    Debug.VulkanEvery(
                        "Vulkan.ResourcePlanner.PhysicalImageAllocationDeferred",
                        TimeSpan.FromSeconds(1),
                        "[VulkanResourcePlanner] Deferred pending physical image allocation. Keeping active plan revision={0}. Reason={1}",
                        ActiveResourcePlannerRevision,
                        imageAllocationFailureReason);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[VulkanResourcePlanner] Pending physical image allocation failed. Keeping active plan revision={0}. Reason={1}",
                        ActiveResourcePlannerRevision,
                        imageAllocationFailureReason);
                }

                pendingAllocator.DestroyPhysicalImagesImmediate(this, reusedImageGroups);
                pendingAllocator.DestroyPhysicalBuffersImmediate(this);
                pendingAllocator = null;
                return false;
            }

            pendingAllocator.AllocatePhysicalBuffers(this);
        }
        catch (Exception ex)
        {
            pendingAllocator?.DestroyPhysicalImagesImmediate(this, reusedImageGroups);
            pendingAllocator?.DestroyPhysicalBuffersImmediate(this);
            pendingAllocator = null;
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                ActiveResourcePlannerRevision,
                ex.Message);
            return false;
        }

        HashSet<VulkanPhysicalImageGroup>? reusedGroups = reusedImageGroups;
        retiredImageCount = ResourceAllocator
            .EnumeratePhysicalGroups()
            .Count(g => g.IsAllocated && (reusedGroups is null || !reusedGroups.Contains(g)));
        retiredBufferCount = ResourceAllocator.EnumeratePhysicalBufferGroups().Count(static g => g.IsAllocated);
        return true;
    }

    internal static bool IsExpectedVulkanImageAllocationDeferral(Exception exception)
        => IsExpectedVulkanImageAllocationDeferral(exception.Message);

    internal static bool IsExpectedVulkanImageAllocationDeferral(string failureReason)
        => failureReason.Contains("Vulkan image allocation deferred under", StringComparison.OrdinalIgnoreCase) ||
            failureReason.Contains("allocation deferred under allocator pressure", StringComparison.OrdinalIgnoreCase);

    private void CommitPhysicalAllocatorPlan(
        bool physicalPlanChanged,
        VulkanResourceAllocator oldAllocator,
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        int retiredImageCount,
        int retiredBufferCount,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (!physicalPlanChanged)
            return;

        if (retiredImageCount > 0 || retiredBufferCount > 0)
        {
            _lastResourcePlanReplacementRevision = ActiveResourcePlannerRevision + 1;
            _lastResourcePlanReplacementSignature = plannerSignature;
            _lastResourcePlanReplacementAllocationSignature = allocationSignature;
            _lastResourcePlanReplacementRetiredImageCount = retiredImageCount;
            _lastResourcePlanReplacementRetiredBufferCount = retiredBufferCount;
            LogDeferredResourcePlanReplacementRetirement(
                retiredImageCount,
                retiredBufferCount,
                plannerSignature,
                allocationSignature);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourcePlanReplacement(retiredImageCount, retiredBufferCount);
        }

        if (IsDeviceLost)
            return;

        VulkanPhysicalImageGroup? retainedAutoExposureGroup = PreserveAutoExposureHistory(oldAllocator);

        EvictFrameOpResourcePlannerStatesReferencingAllocator(oldAllocator);
        _ = oldAllocator.TryRetirePhysicalResources(this, retainedAutoExposureGroup, reusedImageGroups);
    }

    private void EvictFrameOpResourcePlannerStatesReferencingAllocator(VulkanResourceAllocator allocator)
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        List<VulkanFrameOpPlannerStateKey> staleKeys = _frameOpPlannerStateEvictionScratch;
        staleKeys.Clear();
        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            if (ReferenceEquals(pair.Value.ResourceAllocator, allocator))
                staleKeys.Add(pair.Key);
        }

        for (int i = 0; i < staleKeys.Count; i++)
        {
            VulkanFrameOpPlannerStateKey key = staleKeys[i];
            switchingState.States.Remove(key);
            switchingState.LastUsedSerials.Remove(key);
            switchingState.ActiveKeys.Remove(key);
            if (switchingState.HasActiveKey && switchingState.ActiveKey.Equals(key))
            {
                switchingState.HasActiveKey = false;
                switchingState.HasActiveContext = false;
                switchingState.ActiveKey = default;
            }
        }

        bool preparationReferencedAllocator = switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
        if (preparationReferencedAllocator)
        {
            switchingState.PreparationState = default;
            switchingState.HasPreparationState = false;
        }

        if (staleKeys.Count > 0 || preparationReferencedAllocator)
            InvalidatePreparedFrameOpResourcePlan(switchingState);

        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        if (staleKeys.Count > 0)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.RetiredAllocatorCacheEviction.{allocator.OwnershipId}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Evicted {0} cached frame-op planner state(s) before retiring allocator owner {1}. PreparationReferenced={2} FirstRegistry=0x{3:X8}.",
                staleKeys.Count,
                allocator.OwnershipId,
                preparationReferencedAllocator,
                staleKeys[0].ResourceRegistrySignature);
        }

        staleKeys.Clear();
    }

    private VulkanPhysicalImageGroup? PreserveAutoExposureHistory(VulkanResourceAllocator oldAllocator)
    {
        if (ShouldSkipAutoExposureHistoryPreserve())
            return null;

        bool hasOldGroup = TryGetAutoExposurePhysicalGroup(oldAllocator, out VulkanPhysicalImageGroup? oldGroup);
        bool hasNewGroup = TryGetAutoExposurePhysicalGroup(ResourceAllocator, out VulkanPhysicalImageGroup? newGroup);
        if (hasOldGroup && hasNewGroup && ReferenceEquals(oldGroup, newGroup))
            return null;

        if (hasNewGroup && newGroup is not null)
        {
            if (TryCopyAutoExposureHistory(oldGroup, newGroup, "active-plan"))
            {
                DestroyRetainedAutoExposureHistory("superseded by active-plan copy");
                return null;
            }

            if (TryCopyAutoExposureHistory(_retainedAutoExposureHistoryGroup, newGroup, "retained-plan-gap"))
            {
                DestroyRetainedAutoExposureHistory("restored into active plan");
                return null;
            }

            DestroyRetainedAutoExposureHistory("new active plan could not use retained history");
            return null;
        }

        if (hasOldGroup && IsUsableAutoExposureHistoryGroup(oldGroup))
            return RetainAutoExposureHistory(oldGroup!);

        return null;
    }

    private static bool TryGetAutoExposurePhysicalGroup(
        VulkanResourceAllocator allocator,
        out VulkanPhysicalImageGroup? group)
        => allocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.AutoExposureTextureName, out group) &&
           group is not null;

    private bool TryCopyAutoExposureHistory(
        VulkanPhysicalImageGroup? oldGroup,
        VulkanPhysicalImageGroup newGroup,
        string sourceLabel)
    {
        if (!IsUsableAutoExposureHistoryGroup(oldGroup) ||
            !IsUsableAutoExposureTargetGroup(newGroup) ||
            ReferenceEquals(oldGroup, newGroup) ||
            oldGroup!.Format != newGroup.Format ||
            oldGroup.ResolvedExtent.Width != newGroup.ResolvedExtent.Width ||
            oldGroup.ResolvedExtent.Height != newGroup.ResolvedExtent.Height ||
            oldGroup.ResolvedExtent.Depth != newGroup.ResolvedExtent.Depth)
        {
            return false;
        }

        ImageLayout oldLayout = oldGroup.LastKnownLayout;
        ImageLayout newCurrentLayout = newGroup.LastKnownLayout;
        ImageLayout newRestoreLayout = newCurrentLayout == ImageLayout.Undefined
            ? ResolveInitialPhysicalGroupLayout(newGroup.Usage, VulkanResourceAllocator.IsDepthStencilFormat(newGroup.Format))
            : newCurrentLayout;

        using var scope = NewCommandScope();

        // The auto-exposure texture is only touched by compute (storage writes/reads)
        // and fragment sampling, so those stages fully cover prior access without
        // resorting to AllCommands.
        const PipelineStageFlags autoExposureStages =
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            oldGroup,
            oldLayout,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderWriteBit,
            AccessFlags.TransferReadBit,
            autoExposureStages,
            PipelineStageFlags.TransferBit);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            newCurrentLayout,
            ImageLayout.TransferDstOptimal,
            newCurrentLayout == ImageLayout.Undefined ? AccessFlags.None : AccessFlags.ShaderWriteBit,
            AccessFlags.TransferWriteBit,
            newCurrentLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : autoExposureStages,
            PipelineStageFlags.TransferBit);

        ImageCopy copy = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            Extent = new Extent3D(
                Math.Max(1u, oldGroup.ResolvedExtent.Width),
                Math.Max(1u, oldGroup.ResolvedExtent.Height),
                Math.Max(1u, oldGroup.ResolvedExtent.Depth))
        };

        CmdCopyImageTracked(
            scope.CommandBuffer,
            oldGroup.Image,
            ImageLayout.TransferSrcOptimal,
            newGroup.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            ImageLayout.TransferDstOptimal,
            newRestoreLayout,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            autoExposureStages);

        oldGroup.LastKnownLayout = ImageLayout.TransferSrcOptimal;
        newGroup.LastKnownLayout = newRestoreLayout;
        Debug.VulkanEvery(
            $"Vulkan.AutoExposure.HistoryPreserve.{sourceLabel}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Preserved auto exposure history via {0}: src=0x{1:X} dst=0x{2:X} layout={3}->{4}.",
            sourceLabel,
            oldGroup.Image.Handle,
            newGroup.Image.Handle,
            oldLayout,
            newRestoreLayout);
        return true;
    }

    private VulkanPhysicalImageGroup RetainAutoExposureHistory(VulkanPhysicalImageGroup oldGroup)
    {
        if (!ReferenceEquals(_retainedAutoExposureHistoryGroup, oldGroup))
            DestroyRetainedAutoExposureHistory("replaced by newer active history");

        _retainedAutoExposureHistoryGroup = oldGroup;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetain",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Retained auto exposure history while switching to a planner context without AutoExposureTex: image=0x{0:X} layout={1}.",
            oldGroup.Image.Handle,
            oldGroup.LastKnownLayout);
        return oldGroup;
    }

    private void DestroyRetainedAutoExposureHistory(string reason)
    {
        VulkanPhysicalImageGroup? group = _retainedAutoExposureHistoryGroup;
        if (group is null)
            return;

        group.Destroy(this);
        _retainedAutoExposureHistoryGroup = null;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetainedDestroy",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Destroyed retained auto exposure history ({0}).",
            reason);
    }

    private bool ShouldSkipAutoExposureHistoryPreserve()
        => IsDeviceLost ||
           ActiveResourcePlannerRevision == 0 ||
           RuntimeRenderingHostServices.Presentation.IsInVR;

    private static bool IsUsableAutoExposureHistoryGroup(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0 &&
           group.LastKnownLayout != ImageLayout.Undefined;

    private static bool IsUsableAutoExposureTargetGroup(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0;

    private void TransitionPhysicalGroupForCopy(
        CommandBuffer commandBuffer,
        VulkanPhysicalImageGroup group,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = group.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = Math.Max(1u, group.MipLevels),
                BaseArrayLayer = 0,
                LayerCount = Math.Max(1u, group.Template.Layers),
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }


}
