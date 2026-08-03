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

public unsafe partial class VulkanRenderer
{
    private CommandChainSchedule? TryBuildCommandChainSchedule(
        uint imageIndex,
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong frameOpsSignature,
        ulong volatileSignature,
        ulong resourcePlanRevision,
        bool allowExternalSwapchainTarget,
        out CommandChainLoweringStats stats,
        ulong? preparedFastScheduleSignature = null)
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
            HasMutableGpuDrivenFrameOps(staticOps) ||
            HasMutableGpuDrivenFrameOps(volatileOps);

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
        FrameOpResourcePlannerSwitchingState frameOpSwitchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (frameOpSwitchingState.SwitchingActive && traceCommandChains)
        {
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.ResourcePlannerSwitching.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.CommandChains] Scheduling with {0} active frame-op resource planner states.",
                frameOpSwitchingState.ActiveKeys.Count);
        }

        using CommandChainResourcePlanReadScope resourcePlanReadScope = BeginCommandChainResourcePlanReadScope(resourcePlanRevision);
        ulong fastScheduleSignature = preparedFastScheduleSignature ?? 0UL;
        if (!preparedFastScheduleSignature.HasValue)
        {
            using VulkanCpuStageScope cpuStage =
                new(EVulkanCpuStage.CommandChainFastSignature);
            fastScheduleSignature = ComputeCommandChainFastScheduleSignature(
                imageIndex,
                staticOps,
                volatileOps,
                resourcePlanRevision);
        }
        if (TryGetCachedCommandChainSchedule(
                imageIndex,
                fastScheduleSignature,
                out CommandChainSchedule? cachedSchedule,
                out stats))
        {
            ObserveCommandChainScheduleForStabilityGuard(imageIndex, resourcePlanRevision, in stats);
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
                staticOps.Length + volatileOps.Length,
                bypassReason);
            return null;
        }

        List<RenderPacket> packets = _commandChainPacketScratch;
        packets.Clear();
        packets.EnsureCapacity(Math.Max(staticOps.Length + volatileOps.Length, 1));
        _commandChainPacketPoolCursor = 0;
        using (VulkanCpuStageScope cpuStage = new(EVulkanCpuStage.CommandChainPacketLowering))
        {
            BuildCommandChainRenderPackets(
                staticOps,
                volatileOps,
                resourcePlanRevision,
                excludeStaticQueryBrackets,
                packets);
        }
        using VulkanCpuStageScope scheduleEvaluationStage =
            new(EVulkanCpuStage.CommandChainScheduleEvaluation);

        if (packets.Count > MaxCommandChainsPerSchedule)
        {
            // The current cache owns one command pool and secondary command buffer
            // per chain. Large per-draw schedules therefore multiply resource and
            // retirement pressure across outputs and swapchain images. Keep those
            // frames inline until command chains are grouped into bounded arenas.
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.ScheduleBudget.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.CommandChains] Recording {0} frame ops inline because the per-schedule command-chain budget is {1}.",
                packets.Count,
                MaxCommandChainsPerSchedule);
            return null;
        }

        if (packets.Count == 0)
        {
            if (staticOps.Length != 0 || volatileOps.Length != 0)
                return null;

            stats = new CommandChainLoweringStats(0, 0, 0, 0, 0, 0, 0, 0, null, null, null);
            CommandChainSchedule emptySchedule = RentCommandChainSchedule(imageIndex);
            emptySchedule.Reset(
                0,
                resourcePlanRevision,
                ReadOnlySpan<RenderPassChainGroup>.Empty);
            CacheCommandChainSchedule(imageIndex, fastScheduleSignature, emptySchedule);
            ObserveCommandChainScheduleForStabilityGuard(imageIndex, resourcePlanRevision, in stats);
            return emptySchedule;
        }

        Dictionary<CommandChainKey, CommandChain> cache = GetCommandChainCache(imageIndex);
        CommandChainSchedule schedule = RentCommandChainSchedule(imageIndex);
        ulong scheduleGeneration = _commandScheduler.NextScheduleGeneration();
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
        string currentTargetName = packets[0].TargetName;
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
                currentTargetName = packet.TargetName;
                currentDynamicOverlay = packet.DynamicOverlay;
                currentGroupSignature = 0;
            }

            int chainOrdinal = BuildCommandChainOrdinal(packet, structuralOccurrences);

            CommandChainKey key = new(
                unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                packet.ViewKey,
                packet.PassIndex,
                packet.TargetIdentity,
                packet.DynamicOverlay,
                chainOrdinal);

            CommandChain chain = GetOrCreateCommandChain(cache, key);
            chain.ScheduledPacket = true;
            chain.LastUsedScheduleGeneration = scheduleGeneration;
            CommandChainDirtyReason dirtyReason;
            using (VulkanCpuStageScope cpuStage =
                new(EVulkanCpuStage.CommandDependencyComparison))
            {
                dirtyReason = EvaluateCommandChainDirtyReason(chain, packet);
            }
            if (CommandChainBenchmarkForceRerecord)
                dirtyReason |= CommandChainDirtyReason.BenchmarkForced;
            bool secondaryExecutable = chain.SecondaryCommandBuffer.Handle != 0 && chain.SecondaryCommandBufferExecutable;
            if (secondaryExecutable &&
                !HasCompleteRecordedImageEntrySnapshot(
                    chain.SecondaryCommandBuffer,
                    out _))
            {
                // A first-use secondary can be executed once while its old
                // image state is unknown, but it is not a reusable artifact.
                // Re-record after successful submission establishes the
                // per-image state instead of poisoning every merged primary.
                secondaryExecutable = false;
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
            chain.PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature;
            chain.FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature;
            chain.DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration;
            chain.PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration;
            chain.DependencySignature = BuildCommandChainDependencySignature(packet, key);
            chain.DrawCount = packet.DrawCount;
            chain.DispatchCount = packet.DispatchCount;
            chain.InstanceCountSignature = ComputePacketInstanceCountSignature(packet);
            chain.DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount;
            chain.DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature;
            chain.SourceStartIndex = packet.SourceStartIndex;
            chain.SourceCount = packet.SourceCount;
            chain.LastRecordedFrameSlot = unchecked((int)Math.Min(imageIndex, int.MaxValue));

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
            staticOps.Length + volatileOps.Length - scheduledFrameOpCount);
        ulong scheduleSignature = ComputeScheduleStructuralSignature(
            groupSpan,
            requiresFreshPrimary,
            inlineFrameOpCount);
        schedule.Reset(
            scheduleSignature,
            resourcePlanRevision,
            groupSpan,
            requiresFreshPrimary,
            inlineFrameOpCount);
        int visibilityPacketCount = CountDistinctViewKeys(packets);
        RenderPacket lastPacket = packets[^1];
        CommandRecordingDependencySignature scheduleDependencySignature =
            BuildCommandChainDependencySignature(
                lastPacket,
                new CommandChainKey(
                    unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                    lastPacket.ViewKey,
                    lastPacket.PassIndex,
                    lastPacket.TargetIdentity,
                    lastPacket.DynamicOverlay,
                    0)) with
            {
                OutputPassAttachment = scheduleSignature,
                ResourcePlanGeneration = resourcePlanRevision,
            };
        schedule.PublishDependencySignature(scheduleDependencySignature);
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
            QueueFamilyIndices families = FamilyQueueIndices;
            CommandChainQueueSchedule queueSchedule = BuildCommandChainQueueSchedule(
                schedule,
                CommandChainMultiQueueEnabled,
                HasSecondaryGraphicsQueue,
                families.ComputeFamilyIndex.HasValue,
                families.TransferFamilyIndex.HasValue);
            ValidateCommandChainQueueSchedule(queueSchedule);
        }

        stats = new CommandChainLoweringStats(
            visibilityPacketCount,
            packets.Count,
            packets.Count,
            chainsRecorded,
            chainsReused,
            chainsFrameDataRefreshed,
            volatileChainsRecorded,
            packets.Count,
            firstStructuralDirtyReason,
            firstDescriptorMismatch,
            firstResourcePlanMismatch);
        CacheCommandChainSchedule(imageIndex, fastScheduleSignature, schedule);
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

