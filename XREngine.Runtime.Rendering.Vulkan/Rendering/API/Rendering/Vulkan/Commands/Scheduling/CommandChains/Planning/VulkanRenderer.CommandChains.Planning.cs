using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    // Cold pre-plan/OpenXR compatibility only. Desktop lifecycle scheduling
    // calls the stream overload below after FramePlan sealing.
    internal CommandChainSchedule? TryBuildCommandChainSchedule(
        uint imageIndex,
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong frameOpsSignature,
        ulong volatileSignature,
        ulong resourcePlanRevision,
        bool allowExternalSwapchainTarget,
        out CommandChainLoweringStats stats,
        ulong? preparedFastScheduleSignature = null,
        VulkanRecordedRenderTargetSnapshot preparedRecordingTarget = default,
        ulong resourceVersionSignature = 0UL,
        ulong sharedResourceVersionSignature = 0UL,
        ulong descriptorVersionSignature = 0UL)
        => TryBuildCommandChainSchedule(
            imageIndex,
            FrameOperationStream.CreateCompatibility(staticOps),
            FrameOperationStream.CreateCompatibility(volatileOps),
            frameOpsSignature,
            volatileSignature,
            resourcePlanRevision,
            allowExternalSwapchainTarget,
            out stats,
            preparedFastScheduleSignature,
            preparedRecordingTarget,
            resourceVersionSignature,
            sharedResourceVersionSignature,
            descriptorVersionSignature);

    internal CommandChainSchedule? TryBuildCommandChainSchedule(
        uint imageIndex,
        FrameOperationStream staticOps,
        FrameOperationStream volatileOps,
        ulong frameOpsSignature,
        ulong volatileSignature,
        ulong resourcePlanRevision,
        bool allowExternalSwapchainTarget,
        out CommandChainLoweringStats stats,
        ulong? preparedFastScheduleSignature = null,
        VulkanRecordedRenderTargetSnapshot preparedRecordingTarget = default,
        ulong resourceVersionSignature = 0UL,
        ulong sharedResourceVersionSignature = 0UL,
        ulong descriptorVersionSignature = 0UL)
    {
        stats = default;
        // Generic external targets do not have the cache/lifetime contract required
        // by command chains. OpenXR supplies its own external-image cache key and
        // frame-data slots, so its explicit call site is allowed through this gate.
        // Without this exception the OpenXR helper can never build the schedule it
        // was designed to consume and CpuQueryAsync must re-record the complete eye
        // command buffer every frame.
        bool commandChainsEnabledForTarget = allowExternalSwapchainTarget
            ? CommandChainsExplicitlyRequested
            : CommandChainsEnabledForCurrentRecording;
        if (!commandChainsEnabledForTarget)
            return null;

        // Mutable GPU publications remain inline in a freshly recorded primary.
        // Stable mesh-draw ranges on either side are still lowered to reusable
        // secondaries, producing the production mixed primary/secondary schedule.
        bool requiresFreshPrimary =
            HasMutableCommandChainFrameOps(staticOps) ||
            HasMutableCommandChainFrameOps(volatileOps);

        // Dynamic overlays are not expected to contain query brackets. Keep the
        // conservative all-inline fallback if one appears there because overlay
        // source indices occupy a separate namespace from the static frame ops.
        if (ContainsQueryFrameOp(volatileOps))
        {
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.QueryOpsInlineFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan.CommandChains] Dynamic overlay contains occlusion QueryOps; recording the frame inline.");
            return null;
        }

        bool excludeStaticQueryBrackets = ContainsQueryFrameOp(staticOps);
        if (excludeStaticQueryBrackets)
        {
            // Query begin/proxy/end spans remain in the primary. Other frame ops can
            // still use secondary command chains; executing a secondary inside the
            // query would require inheritedQueries-aware inheritance and ending a
            // secondary with a live query is invalid.
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.QueryBracketsInline.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan.CommandChains] Keeping occlusion query brackets inline while scheduling the remaining frame ops as command chains.");
        }

        bool traceCommandChains = CommandChainTraceEnabled;
        CommandChainScheduleCacheIdentity cacheIdentity = new(
            staticOps.Count,
            volatileOps.Count,
            frameOpsSignature,
            volatileSignature,
            resourcePlanRevision,
            resourceVersionSignature,
            descriptorVersionSignature,
            preparedRecordingTarget);
        if (TryReuseCachedCommandChainSchedule(
                imageIndex,
                in cacheIdentity,
                out CommandChainSchedule? cachedSchedule,
                out stats))
        {
            return cachedSchedule;
        }
        if (ShouldBypassCommandChainScheduleForStabilityGuard(
                imageIndex,
                resourcePlanRevision,
                out CommandChainStabilityBypassReason bypassReason))
        {
            LogCommandChainStabilityGuardBypass(
                imageIndex,
                resourcePlanRevision,
                staticOps.Count + volatileOps.Count,
                bypassReason);
            return null;
        }

        // A cached schedule is only a topology hint. Descriptor publication,
        // frame-data ownership, and secondary executability can change while the
        // structural operation signature stays identical (notably across shadow
        // atlas and bloom mip passes). Always lower the current immutable frame
        // operations and evaluate every chain before reusing its command buffer.
        // Packet lowering is pooled and allocation-free on the steady-state path;
        // primary and secondary command buffers remain independently reusable.
        List<RenderPacket> packets = _commandChainPacketScratch;
        packets.Clear();
        packets.EnsureCapacity(Math.Max(staticOps.Count + volatileOps.Count, 1));
        BeginCommandChainPacketPayloadPublication(staticOps.Count + volatileOps.Count);
        _commandChainPacketPoolCursor = 0;
        using (VulkanCpuStageScope cpuStage = new(_frameTelemetry, EVulkanCpuStage.CommandChainPacketLowering))
        {
            BuildCommandChainRenderPackets(
                imageIndex,
                staticOps,
                volatileOps,
                resourcePlanRevision,
                excludeStaticQueryBrackets,
                packets,
                preparedRecordingTarget);
        }
        using VulkanCpuStageScope scheduleEvaluationStage =
            new(_frameTelemetry, EVulkanCpuStage.CommandChainScheduleEvaluation);

        int loweredPacketCount = packets.Count;
        int budgetLimitedInlineFrameOpCount = 0;
        if (loweredPacketCount > MaxCommandChainsPerSchedule)
        {
            // The cache owns one command pool and secondary command buffer per
            // scheduled chain, so keep the native-resource bound finite. Do not
            // reject the complete schedule when it exceeds that bound: the primary
            // recorder already supports mixed secondary and inline islands. A
            // cliff from 1,024 reusable chains to a completely inline frame made
            // ordinary camera motion spend hundreds of milliseconds on the CPU.
            for (int packetIndex = MaxCommandChainsPerSchedule;
                 packetIndex < loweredPacketCount;
                 packetIndex++)
            {
                budgetLimitedInlineFrameOpCount += packets[packetIndex].SourceCount;
            }

            packets.RemoveRange(
                MaxCommandChainsPerSchedule,
                loweredPacketCount - MaxCommandChainsPerSchedule);
            requiresFreshPrimary = true;
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.ScheduleBudget.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.CommandChains] Lowered {0} packets; scheduling the bounded first {1} and retaining {2} source frame ops inline.",
                loweredPacketCount,
                MaxCommandChainsPerSchedule,
                budgetLimitedInlineFrameOpCount);
        }

        if (packets.Count == 0)
        {
            if (staticOps.Count != 0 || volatileOps.Count != 0)
                return null;

            stats = new CommandChainLoweringStats(0, 0, 0, 0, 0, 0, 0, 0, null, null, null);
            CommandChainSchedule emptySchedule = RentCommandChainSchedule(imageIndex);
            emptySchedule.Reset(
                0,
                resourcePlanRevision,
                ReadOnlySpan<RenderPassChainGroup>.Empty);
            emptySchedule.PublishCacheIdentity(in cacheIdentity);
            emptySchedule.PublishArtifactMutationGeneration(
                CommandChains.SnapshotArtifactMutationGeneration());
            CacheCommandChainSchedule(imageIndex, emptySchedule);
            ObserveCommandChainScheduleForStabilityGuard(imageIndex, resourcePlanRevision, in stats);
            return emptySchedule;
        }

        Dictionary<CommandChainKey, CommandChain> cache = GetCommandChainCache(imageIndex);
        CommandChainSchedule schedule = RentCommandChainSchedule(imageIndex);
        ulong scheduleGeneration = _commandRuntime.CommandChains.NextScheduleGeneration();
        List<string>? commandChainTraceRows = traceCommandChains ? [] : null;
        List<RenderPassChainGroup> groups = _commandChainGroupScratch;
        groups.Clear();
        groups.EnsureCapacity(packets.Count);
        List<CommandChainKey> currentGroupKeys = _commandChainGroupKeyScratch;
        currentGroupKeys.Clear();
        currentGroupKeys.EnsureCapacity(8);
        Dictionary<ulong, int> structuralOccurrences = _commandChainStructuralOccurrenceScratch;
        structuralOccurrences.Clear();
        int currentPass = packets[0].PassIndex;
        int currentTarget = packets[0].TargetIdentity;
        string currentTargetName = packets[0].GetDiagnosticTargetName();
        bool currentDynamicOverlay = packets[0].DynamicOverlay;
        ulong currentGroupSignature = 0;

        int chainsRecorded = 0;
        int chainsReused = 0;
        int chainsFrameDataRefreshed = 0;
        int volatileChainsRecorded = 0;
        string? firstStructuralDirtyReason = null;
        string? firstDescriptorMismatch = null;
        string? firstResourcePlanMismatch = null;
        EVulkanCommandBufferDecisionReason secondaryDecisionReasons = EVulkanCommandBufferDecisionReason.None;
        CommandChain? lastScheduledChain = null;

        for (int i = 0; i < packets.Count; i++)
        {
            RenderPacket packet = packets[i];
            if (packet.PassIndex != currentPass ||
                packet.TargetIdentity != currentTarget ||
                packet.DynamicOverlay != currentDynamicOverlay)
            {
                AddCurrentGroup();
                currentGroupKeys.Clear();
                structuralOccurrences.Clear();
                currentPass = packet.PassIndex;
                currentTarget = packet.TargetIdentity;
                currentTargetName = packet.GetDiagnosticTargetName();
                currentDynamicOverlay = packet.DynamicOverlay;
                currentGroupSignature = 0;
            }

            int chainOrdinal = BuildCommandChainOrdinal(packet, structuralOccurrences);
            ulong descriptorBindingVariant =
                ResolveCommandChainDescriptorBindingVariant(packet.DescriptorSnapshot);

            CommandChainKey key = new(
                unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                packet.ViewKey,
                packet.PassIndex,
                packet.TargetIdentity,
                descriptorBindingVariant,
                packet.DynamicOverlay,
                chainOrdinal);

            CommandChain chain = GetOrCreateCommandChain(cache, key);
            chain.ScheduledPacket = true;
            chain.LastUsedScheduleGeneration = scheduleGeneration;
            CommandChainDirtyReason dirtyReason;
            using (VulkanCpuStageScope cpuStage =
                new(_frameTelemetry, EVulkanCpuStage.CommandDependencyComparison))
            {
                dirtyReason = EvaluateCommandChainDirtyReason(chain, packet);
                // The schedule-wide resource signature includes the current visible
                // operation set. Comparing it per chain would invalidate every cached
                // secondary whenever an unrelated mesh enters or leaves the frustum.
                // Exact packet resources are checked above; this shared signature only
                // covers allocator-wide replacements such as a swapchain resize.
                if (chain.ResourceVersionSignature != sharedResourceVersionSignature)
                    dirtyReason |= CommandChainDirtyReason.ResourcePlan;
            }
            if (dirtyReason != CommandChainDirtyReason.None &&
                FrameDataReuseDiagnosticsEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.CommandChains.DependencyMismatch.{GetHashCode()}.{dirtyReason}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan.CommandChains] Secondary dependency changed key={0}: {1}",
                    key,
                    DescribeCommandChainDirtyReason(chain, packet));
            }
            if (CommandChainBenchmarkForceRerecord)
                dirtyReason |= CommandChainDirtyReason.BenchmarkForced;
            bool secondaryExecutable = chain.SecondaryCommandBuffer.Handle != 0 && chain.SecondaryCommandBufferExecutable;
            if (!secondaryExecutable &&
                chain.SecondaryCommandBuffer.Handle != 0 &&
                FrameDataReuseDiagnosticsEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.CommandChains.ArtifactInvalid.{GetHashCode()}.{chain.RecordedArtifact.InvalidationReason}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan.CommandChains] Secondary artifact is not executable key={0} commandBuffer=0x{1:X} state={2} invalidation={3} generation={4}.",
                    key,
                    chain.SecondaryCommandBuffer.Handle,
                    chain.RecordedArtifact.State,
                    chain.RecordedArtifact.InvalidationReason,
                    chain.RecordedArtifact.Generation);
            }
            VulkanImageEntryStateMismatch imageEntryFailure = default;
            if (secondaryExecutable &&
                !HasCompleteCommandChainImageEntrySnapshot(
                    chain.SecondaryCommandBuffer,
                    out imageEntryFailure))
            {
                // A first-use secondary can be executed once while its old
                // image state is unknown, but it is not a reusable artifact.
                // Re-record after successful submission establishes the
                // per-image state instead of poisoning every merged primary.
                secondaryExecutable = false;
                if (traceCommandChains || FrameDataReuseDiagnosticsEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.CommandChains.ImageEntry.{GetHashCode()}.{imageEntryFailure.Kind}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan.CommandChains] Secondary image-entry snapshot is not reusable reason={0} commandBuffer=0x{1:X} image=0x{2:X} mip={3} layer={4} aspect={5} expected={6} actual={7}.",
                        imageEntryFailure.Kind,
                        chain.SecondaryCommandBuffer.Handle,
                        imageEntryFailure.ImageHandle,
                        imageEntryFailure.MipLevel,
                        imageEntryFailure.ArrayLayer,
                        imageEntryFailure.Aspect,
                        imageEntryFailure.Expected,
                        imageEntryFailure.Actual);
                }
            }
            CommandChainDirtyReason effectiveDirtyReason = dirtyReason == CommandChainDirtyReason.None && !secondaryExecutable
                ? CommandChainDirtyReason.SecondaryCommandBufferInvalid
                : dirtyReason;
            bool canReuse = secondaryExecutable &&
                dirtyReason == CommandChainDirtyReason.None &&
                packet.Volatility is RenderPacketVolatility.StaticStructural or RenderPacketVolatility.FrameDataOnly;
            bool canRefreshFrameData = secondaryExecutable && CanRefreshCommandChainFrameData(dirtyReason, packet);
            bool refreshedFrameData = canRefreshFrameData && TryRefreshReusableCommandChainFrameData(chain, packet);

            if (packet.Volatility == RenderPacketVolatility.DynamicCommand)
            {
                secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.SecondaryRecorded |
                    EVulkanCommandBufferDecisionReason.VolatileCommand;
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = CommandChainDirtyReason.VolatileCommand;
                chain.FrameDataRefreshTouchedDescriptors = false;
                chainsRecorded++;
                volatileChainsRecorded++;
            }
            else if (canReuse || refreshedFrameData)
            {
                secondaryDecisionReasons |= refreshedFrameData
                    ? EVulkanCommandBufferDecisionReason.SecondaryFrameDataRefreshed
                    : EVulkanCommandBufferDecisionReason.SecondaryReused;
                if (CommandChainValidationEnabled && dirtyReason == CommandChainDirtyReason.None)
                    ValidateReusableCommandChainReferences(chain, packet);

                chain.State = refreshedFrameData ? CommandChainState.FrameDataRefreshed : CommandChainState.Reused;
                chain.DirtyReason = CommandChainDirtyReason.None;
                if (!refreshedFrameData)
                    chain.FrameDataRefreshTouchedDescriptors = false;
                chainsReused++;
                if (refreshedFrameData)
                    chainsFrameDataRefreshed++;
            }
            else
            {
                secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.SecondaryRecorded;
                if ((effectiveDirtyReason & CommandChainDirtyReason.Structure) != 0)
                    secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.FrameOpSignature;
                if ((effectiveDirtyReason & CommandChainDirtyReason.ResourcePlan) != 0)
                    secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.ResourcePlan;
                if ((effectiveDirtyReason & CommandChainDirtyReason.DescriptorGeneration) != 0)
                    secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.DescriptorGeneration;
                if ((effectiveDirtyReason & CommandChainDirtyReason.PipelineGeneration) != 0)
                    secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.PipelineGeneration;
                if ((effectiveDirtyReason & CommandChainDirtyReason.SecondaryCommandBufferInvalid) != 0)
                    secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.SecondaryInvalid;
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = effectiveDirtyReason == CommandChainDirtyReason.None
                    ? CommandChainDirtyReason.Structure
                    : effectiveDirtyReason;
                chain.FrameDataRefreshTouchedDescriptors = false;
                chainsRecorded++;
                if (traceCommandChains || CommandChainValidationEnabled)
                    firstStructuralDirtyReason ??= DescribeCommandChainDirtyReason(chain, packet);
                if ((chain.DirtyReason & CommandChainDirtyReason.DescriptorGeneration) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0 &&
                    (traceCommandChains || CommandChainValidationEnabled))
                    firstDescriptorMismatch ??= $"chain={key} previous={chain.DescriptorGeneration} current={packet.DescriptorSnapshot.DescriptorGeneration}";
                if ((chain.DirtyReason & CommandChainDirtyReason.ResourcePlan) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0 &&
                    (traceCommandChains || CommandChainValidationEnabled))
                    firstResourcePlanMismatch ??= $"chain={key} previous={chain.ResourcePlanRevision} current={packet.ResourcePlanSnapshot.Revision}";
            }

            if (commandChainTraceRows is not null &&
                (chain.State == CommandChainState.Recorded || chain.State == CommandChainState.FrameDataRefreshed))
            {
                FrameOp? sourceOp = ResolveCommandChainTraceSourceOp(packet, staticOps, volatileOps);
                commandChainTraceRows.Add(DescribeCommandChainTraceRow(i, packet, chain, sourceOp));
            }

            chain.StructuralSignature = packet.StructuralSignature;
            chain.FrameDataSignature = packet.FrameDataSignature;
            chain.ResourcePlanRevision = packet.ResourcePlanSnapshot.Revision;
            chain.ResourceVersionSignature = sharedResourceVersionSignature;
            chain.PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature;
            chain.FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature;
            chain.DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration;
            chain.PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration;
            chain.DependencySignature = BuildCurrentCommandChainDependencySignature(packet, chain);
            chain.DrawCount = packet.DrawCount;
            chain.DispatchCount = packet.DispatchCount;
            chain.InstanceCountSignature = ComputePacketInstanceCountSignature(packet);
            chain.DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount;
            chain.DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature;
            chain.SourceStartIndex = packet.SourceStartIndex;
            chain.SourceCount = packet.SourceCount;
            chain.LastRecordedFrameSlot = unchecked((int)Math.Min(imageIndex, int.MaxValue));
            chain.PublishPacketSnapshot(packet);
            lastScheduledChain = chain;

            currentGroupKeys.Add(key);
            currentGroupSignature = MixSignature(currentGroupSignature, packet.StructuralSignature);
        }

        AddCurrentGroup();
        TrimScheduledCommandChainCache(cache);

        ReadOnlySpan<RenderPassChainGroup> groupSpan = CollectionsMarshal.AsSpan(groups);
        int scheduledFrameOpCount = 0;
        for (int packetIndex = 0; packetIndex < packets.Count; packetIndex++)
            scheduledFrameOpCount += packets[packetIndex].SourceCount;
        int inlineFrameOpCount = Math.Max(
            0,
            staticOps.Count + volatileOps.Count - scheduledFrameOpCount);
        ulong scheduleSignature = ComputeScheduleStructuralSignature(
            groupSpan,
            requiresFreshPrimary,
            inlineFrameOpCount);
        schedule.Reset(
            scheduleSignature,
            resourcePlanRevision,
            groupSpan,
            requiresFreshPrimary,
            inlineFrameOpCount,
            budgetLimitedInlineFrameOpCount);
        int visibilityPacketCount = CountDistinctViewKeys(packets);
        RenderPacket lastPacket = packets[^1];
        CommandRecordingDependencySignature scheduleDependencySignature =
            (lastScheduledChain is null
                ? BuildCommandChainDependencySignature(
                    lastPacket,
                    new CommandChainKey(
                        unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                        lastPacket.ViewKey,
                        lastPacket.PassIndex,
                        lastPacket.TargetIdentity,
                        ResolveCommandChainDescriptorBindingVariant(
                            lastPacket.DescriptorSnapshot),
                        lastPacket.DynamicOverlay,
                        0))
                : BuildCurrentCommandChainDependencySignature(lastPacket, lastScheduledChain)) with
            {
                OutputPassAttachment = scheduleSignature,
                ResourcePlanGeneration = resourcePlanRevision,
            };
        schedule.PublishDependencySignature(scheduleDependencySignature);
        schedule.PublishCacheIdentity(in cacheIdentity);
        schedule.PublishArtifactMutationGeneration(
            CommandChains.SnapshotArtifactMutationGeneration());
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean: false,
            recorded: false,
            forcedDirty: false,
            frameOpSignatureDirty: false,
            plannerDirty: false,
            profilerDirty: false,
            dirtyReason: null,
            detailReasons: secondaryDecisionReasons,
            structuralSignature: lastPacket.StructuralSignature,
            descriptorGeneration: lastPacket.DescriptorSnapshot.DescriptorGeneration,
            swapchainSlot: unchecked((int)imageIndex));

        if (traceCommandChains)
            TraceCommandChainSchedule(schedule, packets, staticOps, volatileOps, commandChainTraceRows);

        if (CommandChainValidationEnabled)
        {
            ValidateCommandChainSchedule(schedule, packets, frameOpsSignature);
            ValidateCommandChainViewSpecialization(schedule);
            QueueFamilyIndices families = _deviceContext.QueueFamilies;
            CommandChainQueueSchedule queueSchedule = BuildCommandChainQueueSchedule(
                schedule,
                CommandChainMultiQueueEnabled,
                DeviceContext.HasSecondaryGraphicsQueue,
                families.ComputeFamilyIndex.HasValue,
                families.TransferFamilyIndex.HasValue);
            ValidateCommandChainQueueSchedule(queueSchedule);
        }

        stats = new CommandChainLoweringStats(
            visibilityPacketCount,
            loweredPacketCount,
            packets.Count,
            chainsRecorded,
            chainsReused,
            chainsFrameDataRefreshed,
            volatileChainsRecorded,
            packets.Count,
            firstStructuralDirtyReason,
            firstDescriptorMismatch,
            firstResourcePlanMismatch);
        CacheCommandChainSchedule(imageIndex, schedule);
        ObserveCommandChainScheduleForStabilityGuard(imageIndex, resourcePlanRevision, in stats);
        return schedule;

        void AddCurrentGroup()
        {
            if (currentGroupKeys.Count == 0)
                return;

            RenderPassChainGroup group = schedule.RentGroup(groups.Count);
            group.Reset(
                currentPass,
                currentTarget,
                currentTargetName,
                CollectionsMarshal.AsSpan(currentGroupKeys),
                currentGroupSignature,
                supportsSecondaryCommandBuffers: true,
                dynamicOverlay: currentDynamicOverlay);
            groups.Add(group);
        }
    }
}

