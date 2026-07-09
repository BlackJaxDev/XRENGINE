using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
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
    internal const string CommandChainsEnvVar = XREngineEnvironmentVariables.VulkanCommandChains;
    internal const string CommandChainsSingleThreadEnvVar = XREngineEnvironmentVariables.VulkanCommandChainsSingleThread;
    internal const string CommandChainValidateEnvVar = XREngineEnvironmentVariables.VulkanCommandChainValidate;
    internal const string CommandChainTraceEnvVar = XREngineEnvironmentVariables.VulkanCommandChainTrace;
    internal const string DisableParallelChainRecordingEnvVar = XREngineEnvironmentVariables.VulkanDisableParallelChainRecording;
    internal const string ParallelPacketBuildEnvVar = XREngineEnvironmentVariables.VulkanParallelPacketBuild;
    internal const string CommandChainMultiQueueEnvVar = XREngineEnvironmentVariables.VulkanCommandChainMultiQueue;
    internal const string CommandChainStabilityGuardEnvVar = XREngineEnvironmentVariables.VulkanCommandChainStabilityGuard;
    internal const int CommandChainLeftEyeViewIndex = 0;
    internal const int CommandChainRightEyeViewIndex = 1;
    internal const int CommandChainStereoMultiviewViewIndex = -1;

    private Dictionary<CommandChainKey, CommandChain>[]? _commandChainCaches;
    private Dictionary<uint, Dictionary<CommandChainKey, CommandChain>>? _externalCommandChainCaches;
    private CommandChainSchedule?[]? _commandChainScheduleCache;
    private ulong[]? _commandChainScheduleFastSignatures;
    private readonly List<RenderPacket> _commandChainPacketScratch = [];
    private readonly List<RenderPassChainGroup> _commandChainGroupScratch = [];
    private readonly List<CommandChainKey> _commandChainGroupKeyScratch = [];
    private readonly Dictionary<uint, CommandChainStabilityGuardState> _commandChainStabilityGuardStates = [];
    private CommandChainStabilityGuardState _commandChainGlobalStabilityGuardState;
    private int _commandChainTraceDumped;
    private long _commandChainTraceLastDumpTimestamp;
    private const int CommandChainZeroReuseBackoffThreshold = 1;
    private const int CommandChainZeroReuseProbeInterval = 120;

    private static bool CommandChainsEnabled => IsCommandChainFlagEnabled(CommandChainsEnvVar);
    private static bool CommandChainsSingleThread => IsCommandChainFlagEnabled(CommandChainsSingleThreadEnvVar);
    private static bool CommandChainValidationEnabled => IsCommandChainFlagEnabled(CommandChainValidateEnvVar);
    private static bool CommandChainTraceEnabled => IsCommandChainFlagEnabled(CommandChainTraceEnvVar);
    private static bool ParallelCommandChainRecordingDisabled => IsCommandChainFlagEnabled(DisableParallelChainRecordingEnvVar);
    private static bool ParallelPacketBuildEnabled => IsCommandChainFlagEnabled(ParallelPacketBuildEnvVar);
    private static bool CommandChainMultiQueueEnabled => IsCommandChainFlagEnabled(CommandChainMultiQueueEnvVar);
    private static bool CommandChainStabilityGuardEnabled =>
        !CommandChainTraceEnabled &&
        !CommandChainValidationEnabled &&
        !IsCommandChainFlagDisabled(CommandChainStabilityGuardEnvVar);
    private bool CommandChainsEnabledForCurrentRecording =>
        !IsRenderingExternalSwapchainTarget &&
        ((CommandChainsEnabled && !ShouldBypassCommandChainsForOpenXrIndependentDesktop) ||
         ShouldUseCommandChainsForOpenXrIndependentDesktop);

    private static bool ShouldBypassCommandChainsForOpenXrIndependentDesktop =>
        ShouldUseOpenXrIndependentDesktopCommandChainPolicy &&
        !IsCommandChainFlagEnabled("XRE_VULKAN_COMMAND_CHAINS_ALLOW_INDEPENDENT_DESKTOP");

    private static bool ShouldUseCommandChainsForOpenXrIndependentDesktop
    {
        get
        {
            return ShouldUseOpenXrIndependentDesktopCommandChainPolicy &&
                   IsCommandChainFlagEnabled("XRE_VULKAN_COMMAND_CHAINS_ALLOW_INDEPENDENT_DESKTOP");
        }
    }

    private static bool ShouldUseOpenXrIndependentDesktopCommandChainPolicy
    {
        get
        {
            IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
            return host.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan &&
                   host.IsInVR &&
                   host.IsOpenXRActive &&
                   host.RenderWindowsWhileInVR &&
                   host.VrMirrorMode == EVrMirrorMode.FullIndependentRender &&
                   !host.VrMirrorComposeFromEyeTextures;
        }
    }

    private readonly record struct CommandChainLoweringStats(
        int VisibilityPackets,
        int RenderPackets,
        int ChainsScheduled,
        int ChainsRecorded,
        int ChainsReused,
        int ChainsFrameDataRefreshed,
        int VolatileChainsRecorded,
        int SecondaryCommandBuffers,
        TimeSpan WorkerRecordTime,
        TimeSpan WaitForWorkersTime,
        string? FirstStructuralDirtyReason,
        string? FirstDescriptorGenerationMismatch,
        string? FirstResourcePlanRevisionMismatch);

    private enum CommandChainStabilityBypassReason
    {
        None,
        ResourcePlanRevisionChanged,
        RecentZeroReuse,
        GlobalRecentZeroReuse,
    }

    private struct CommandChainStabilityGuardState
    {
        public ulong ResourcePlanRevision;
        public int StableObservations;
        public int ScheduledAttemptsForRevision;
        public int ConsecutiveRecordedWithoutReuse;
        public int ConsecutiveBypasses;
    }

    private static bool IsCommandChainFlagEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandChainFlagDisabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsQueryFrameOp(FrameOp[] ops)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i] is QueryOp)
                return true;
        }

        return false;
    }

    private CommandChainResourcePlanReadScope BeginCommandChainResourcePlanReadScope(ulong resourcePlanRevision)
        => new(this, resourcePlanRevision);

    private CommandChainSchedule? TryBuildCommandChainSchedule(
        uint imageIndex,
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong frameOpsSignature,
        ulong volatileSignature,
        ulong resourcePlanRevision,
        out CommandChainLoweringStats stats)
    {
        stats = default;
        if (!CommandChainsEnabledForCurrentRecording)
            return null;

        // Occlusion query brackets (QueryOp Begin/End around a proxy draw) must record
        // inline into the primary command buffer: chain secondaries would need
        // inheritedQueries-aware inheritance info, and splitting a bracket across
        // secondaries ends a command buffer with an in-progress query
        // (VUID-vkEndCommandBuffer-commandBuffer-00061). Fall back to inline recording
        // for frames that contain query ops.
        if (ContainsQueryFrameOp(staticOps) || ContainsQueryFrameOp(volatileOps))
        {
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.QueryOpsInlineFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan.CommandChains] Frame contains occlusion QueryOps; recording inline instead of command chains.");
            return null;
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
        ulong fastScheduleSignature = ComputeCommandChainFastScheduleSignature(imageIndex, staticOps, volatileOps, resourcePlanRevision);
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

        long start = Stopwatch.GetTimestamp();
        List<RenderPacket> packets = _commandChainPacketScratch;
        packets.Clear();
        packets.EnsureCapacity(Math.Max(staticOps.Length + volatileOps.Length, 1));
        BuildCommandChainRenderPackets(staticOps, volatileOps, resourcePlanRevision, packets);

        if (packets.Count == 0)
        {
            stats = new CommandChainLoweringStats(0, 0, 0, 0, 0, 0, 0, 0, Stopwatch.GetElapsedTime(start), TimeSpan.Zero, null, null, null);
            CommandChainSchedule emptySchedule = new(0, resourcePlanRevision, ReadOnlyMemory<RenderPassChainGroup>.Empty);
            CacheCommandChainSchedule(imageIndex, fastScheduleSignature, emptySchedule);
            ObserveCommandChainScheduleForStabilityGuard(imageIndex, resourcePlanRevision, in stats);
            return emptySchedule;
        }

        Dictionary<CommandChainKey, CommandChain> cache = GetCommandChainCache(imageIndex);
        List<string>? commandChainTraceRows = traceCommandChains ? [] : null;
        List<RenderPassChainGroup> groups = _commandChainGroupScratch;
        groups.Clear();
        groups.EnsureCapacity(packets.Count);
        List<CommandChainKey> currentGroupKeys = _commandChainGroupKeyScratch;
        currentGroupKeys.Clear();
        currentGroupKeys.EnsureCapacity(8);
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

        for (int i = 0; i < packets.Count; i++)
        {
            RenderPacket packet = packets[i];
            if (packet.PassIndex != currentPass ||
                packet.TargetIdentity != currentTarget ||
                packet.DynamicOverlay != currentDynamicOverlay)
            {
                AddCurrentGroup();
                currentGroupKeys.Clear();
                currentPass = packet.PassIndex;
                currentTarget = packet.TargetIdentity;
                currentTargetName = packet.TargetName;
                currentDynamicOverlay = packet.DynamicOverlay;
                currentGroupSignature = 0;
            }

            int chainOrdinal = BuildCommandChainOrdinal(packet, currentGroupKeys.Count);

            CommandChainKey key = new(
                unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                packet.ViewKey,
                packet.PassIndex,
                packet.TargetIdentity,
                packet.DynamicOverlay,
                chainOrdinal);

            CommandChain chain = GetOrCreateCommandChain(cache, key);
            CommandChainDirtyReason dirtyReason = EvaluateCommandChainDirtyReason(chain, packet);
            bool secondaryExecutable = chain.SecondaryCommandBuffer.Handle != 0 && chain.SecondaryCommandBufferExecutable;
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
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = CommandChainDirtyReason.VolatileCommand;
                chain.FrameDataRefreshTouchedDescriptors = false;
                chainsRecorded++;
                volatileChainsRecorded++;
            }
            else if (canReuse || refreshedFrameData)
            {
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
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = effectiveDirtyReason == CommandChainDirtyReason.None
                    ? CommandChainDirtyReason.Structure
                    : effectiveDirtyReason;
                chain.FrameDataRefreshTouchedDescriptors = false;
                chainsRecorded++;
                firstStructuralDirtyReason ??= DescribeCommandChainDirtyReason(chain, packet);
                if ((chain.DirtyReason & CommandChainDirtyReason.DescriptorGeneration) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0)
                    firstDescriptorMismatch ??= $"chain={key} previous={chain.DescriptorGeneration} current={packet.DescriptorSnapshot.DescriptorGeneration}";
                if ((chain.DirtyReason & CommandChainDirtyReason.ResourcePlan) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0)
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

        RenderPassChainGroup[] groupArray = groups.ToArray();
        ulong scheduleSignature = ComputeScheduleStructuralSignature(groupArray);
        CommandChainSchedule schedule = new(scheduleSignature, resourcePlanRevision, groupArray);
        int visibilityPacketCount = CountDistinctViewKeys(packets);
        TimeSpan workerRecordTime = Stopwatch.GetElapsedTime(start);

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
            workerRecordTime,
            CommandChainsSingleThread || ParallelCommandChainRecordingDisabled ? TimeSpan.Zero : TimeSpan.Zero,
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

            groups.Add(new RenderPassChainGroup(
                currentPass,
                currentTarget,
                currentTargetName,
                currentGroupKeys.ToArray(),
                currentGroupSignature,
                supportsSecondaryCommandBuffers: true,
                dynamicOverlay: currentDynamicOverlay));
        }
    }

    private bool ShouldBypassCommandChainScheduleForStabilityGuard(
        uint imageIndex,
        ulong resourcePlanRevision,
        out CommandChainStabilityBypassReason reason)
    {
        reason = CommandChainStabilityBypassReason.None;
        if (!CommandChainStabilityGuardEnabled)
            return false;

        CommandChainStabilityGuardState globalState = _commandChainGlobalStabilityGuardState;
        if (globalState.ConsecutiveRecordedWithoutReuse >= CommandChainZeroReuseBackoffThreshold)
        {
            globalState.ResourcePlanRevision = resourcePlanRevision;
            globalState.StableObservations++;
            globalState.ConsecutiveBypasses++;
            if (globalState.ConsecutiveBypasses < CommandChainZeroReuseProbeInterval)
            {
                _commandChainGlobalStabilityGuardState = globalState;
                reason = CommandChainStabilityBypassReason.GlobalRecentZeroReuse;
                return true;
            }

            globalState.ConsecutiveBypasses = 0;
            _commandChainGlobalStabilityGuardState = globalState;
        }

        if (!_commandChainStabilityGuardStates.TryGetValue(imageIndex, out CommandChainStabilityGuardState state))
        {
            _commandChainStabilityGuardStates[imageIndex] = new CommandChainStabilityGuardState
            {
                ResourcePlanRevision = resourcePlanRevision,
                StableObservations = 1,
            };
            return false;
        }

        if (state.ResourcePlanRevision == 0 && resourcePlanRevision != 0)
        {
            state.ResourcePlanRevision = resourcePlanRevision;
        }
        else if (state.ResourcePlanRevision != 0 &&
                 resourcePlanRevision != 0 &&
                 state.ResourcePlanRevision != resourcePlanRevision)
        {
            state.ResourcePlanRevision = resourcePlanRevision;
            state.StableObservations = 1;
            state.ScheduledAttemptsForRevision = 0;
            state.ConsecutiveRecordedWithoutReuse = 0;
            state.ConsecutiveBypasses++;
            _commandChainStabilityGuardStates[imageIndex] = state;
            reason = CommandChainStabilityBypassReason.ResourcePlanRevisionChanged;
            return true;
        }

        state.StableObservations++;
        if (state.ConsecutiveRecordedWithoutReuse >= CommandChainZeroReuseBackoffThreshold)
        {
            state.ConsecutiveBypasses++;
            if (state.ConsecutiveBypasses < CommandChainZeroReuseProbeInterval)
            {
                _commandChainStabilityGuardStates[imageIndex] = state;
                reason = CommandChainStabilityBypassReason.RecentZeroReuse;
                return true;
            }

            state.ConsecutiveBypasses = 0;
        }

        _commandChainStabilityGuardStates[imageIndex] = state;
        return false;
    }

    private void ObserveCommandChainScheduleForStabilityGuard(
        uint imageIndex,
        ulong resourcePlanRevision,
        in CommandChainLoweringStats stats)
    {
        if (!CommandChainStabilityGuardEnabled || stats.ChainsScheduled == 0)
            return;

        ObserveGlobalCommandChainScheduleForStabilityGuard(resourcePlanRevision, in stats);

        if (!_commandChainStabilityGuardStates.TryGetValue(imageIndex, out CommandChainStabilityGuardState state) ||
            state.ResourcePlanRevision != resourcePlanRevision)
        {
            state = new CommandChainStabilityGuardState
            {
                ResourcePlanRevision = resourcePlanRevision,
                StableObservations = 1,
            };
        }

        state.ScheduledAttemptsForRevision++;
        if (stats.ChainsRecorded > 0 &&
            stats.ChainsReused == 0 &&
            stats.ChainsFrameDataRefreshed == 0 &&
            state.ScheduledAttemptsForRevision > 1)
        {
            state.ConsecutiveRecordedWithoutReuse++;
        }
        else if (stats.ChainsReused != 0 || stats.ChainsFrameDataRefreshed != 0)
        {
            state.ConsecutiveRecordedWithoutReuse = 0;
        }

        state.ConsecutiveBypasses = 0;
        _commandChainStabilityGuardStates[imageIndex] = state;
    }

    private void ObserveGlobalCommandChainScheduleForStabilityGuard(
        ulong resourcePlanRevision,
        in CommandChainLoweringStats stats)
    {
        CommandChainStabilityGuardState state = _commandChainGlobalStabilityGuardState;
        state.ResourcePlanRevision = resourcePlanRevision;
        state.StableObservations++;
        state.ScheduledAttemptsForRevision++;

        if (stats.ChainsRecorded > 0 &&
            stats.ChainsReused == 0 &&
            stats.ChainsFrameDataRefreshed == 0 &&
            state.ScheduledAttemptsForRevision > 1)
        {
            state.ConsecutiveRecordedWithoutReuse++;
        }
        else if (stats.ChainsReused != 0 || stats.ChainsFrameDataRefreshed != 0)
        {
            state.ConsecutiveRecordedWithoutReuse = 0;
        }

        state.ConsecutiveBypasses = 0;
        _commandChainGlobalStabilityGuardState = state;
    }

    private void LogCommandChainStabilityGuardBypass(
        uint imageIndex,
        ulong resourcePlanRevision,
        int opCount,
        CommandChainStabilityBypassReason reason)
    {
        CommandChainStabilityGuardState state;
        if (reason == CommandChainStabilityBypassReason.GlobalRecentZeroReuse)
        {
            state = _commandChainGlobalStabilityGuardState;
        }
        else if (!_commandChainStabilityGuardStates.TryGetValue(imageIndex, out state))
        {
            state = default;
        }

        Debug.VulkanEvery(
            $"Vulkan.CommandChains.StabilityGuard.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.CommandChains] Stability guard recording inline. reason={0} image={1} revision={2} stableObservations={3} noReuse={4} bypasses={5} ops={6}. Set {7}=0 to disable.",
            reason,
            imageIndex,
            resourcePlanRevision,
            state.StableObservations,
            state.ConsecutiveRecordedWithoutReuse,
            state.ConsecutiveBypasses,
            opCount,
            CommandChainStabilityGuardEnvVar);
    }

    private bool TryGetCachedCommandChainSchedule(
        uint imageIndex,
        ulong fastScheduleSignature,
        out CommandChainSchedule? schedule,
        out CommandChainLoweringStats stats)
    {
        schedule = null;
        stats = default;
        if (CommandChainValidationEnabled || CommandChainTraceEnabled || IsRenderingExternalSwapchainTarget)
            return false;

        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int slot))
            return false;

        if (_commandChainScheduleCache is null ||
            _commandChainScheduleFastSignatures is null ||
            slot >= _commandChainScheduleCache.Length ||
            slot >= _commandChainScheduleFastSignatures.Length)
        {
            return false;
        }

        schedule = _commandChainScheduleCache[slot];
        if (schedule is null || _commandChainScheduleFastSignatures[slot] != fastScheduleSignature)
        {
            schedule = null;
            return false;
        }

        int chainCount = CountCommandChains(schedule);
        stats = new CommandChainLoweringStats(
            VisibilityPackets: schedule.Groups.Length,
            RenderPackets: chainCount,
            ChainsScheduled: chainCount,
            ChainsRecorded: 0,
            ChainsReused: chainCount,
            ChainsFrameDataRefreshed: 0,
            VolatileChainsRecorded: 0,
            SecondaryCommandBuffers: chainCount,
            WorkerRecordTime: TimeSpan.Zero,
            WaitForWorkersTime: TimeSpan.Zero,
            FirstStructuralDirtyReason: null,
            FirstDescriptorGenerationMismatch: null,
            FirstResourcePlanRevisionMismatch: null);
        return true;
    }

    private void CacheCommandChainSchedule(uint imageIndex, ulong fastScheduleSignature, CommandChainSchedule schedule)
    {
        if (CommandChainValidationEnabled || CommandChainTraceEnabled)
            return;

        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int slot))
            return;

        EnsureCommandChainScheduleCache();
        CommandChainSchedule?[] scheduleCache = _commandChainScheduleCache!;
        ulong[] fastSignatures = _commandChainScheduleFastSignatures!;
        scheduleCache[slot] = schedule;
        fastSignatures[slot] = fastScheduleSignature;
    }

    private void EnsureCommandChainScheduleCache()
    {
        int count = Math.Max(_commandBuffers?.Length ?? 0, 1);
        if (_commandChainScheduleCache is not null &&
            _commandChainScheduleFastSignatures is not null &&
            _commandChainScheduleCache.Length == count &&
            _commandChainScheduleFastSignatures.Length == count)
        {
            return;
        }

        _commandChainScheduleCache = new CommandChainSchedule?[count];
        _commandChainScheduleFastSignatures = new ulong[count];
    }

    private static int CountCommandChains(CommandChainSchedule schedule)
    {
        int count = 0;
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int i = 0; i < groups.Length; i++)
            count += groups[i].ChainKeys.Length;
        return count;
    }

    private static ulong ComputeCommandChainFastScheduleSignature(
        uint imageIndex,
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong resourcePlanRevision)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(unchecked((int)Math.Min(imageIndex, int.MaxValue)));
        hash.Add(resourcePlanRevision);
        AddCommandChainFastScheduleSignatureParts(ref hash, staticOps, dynamicOverlay: false);
        AddCommandChainFastScheduleSignatureParts(ref hash, volatileOps, dynamicOverlay: true);
        return hash.ToHash();
    }

    private static void AddCommandChainFastScheduleSignatureParts(ref FrameOpSignatureHasher hash, FrameOp[] ops, bool dynamicOverlay)
    {
        hash.Add(ops.Length);
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            RenderViewKey viewKey = BuildRenderViewKey(op, dynamicOverlay);
            RenderPacketVolatility volatility = ClassifyRenderPacketVolatility(op, dynamicOverlay);
            hash.Add(op.PassIndex);
            hash.Add(ResolveCommandChainTargetIdentity(op));
            hash.Add(dynamicOverlay);
            hash.Add(i);
            hash.Add(viewKey.PipelineIdentity);
            hash.Add(viewKey.ViewportIdentity);
            hash.Add(viewKey.ViewIndex);
            hash.Add((int)viewKey.Kind);
            hash.Add(viewKey.LightIdentity);
            hash.Add(viewKey.CascadeIndex);
            hash.Add(ComputeFrameOpStructuralSignature(op, i, volatility));
            hash.Add(ResolvePipelineGeneration(op));
        }
    }

    private void BuildCommandChainRenderPackets(
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong resourcePlanRevision,
        List<RenderPacket> packets)
    {
        bool useParallelBuild =
            ParallelPacketBuildEnabled &&
            !CommandChainsSingleThread &&
            staticOps.Length + volatileOps.Length > 1;
        if (!useParallelBuild)
        {
            LowerFrameOpsToRenderPackets(staticOps, dynamicOverlay: false, resourcePlanRevision, packets);
            LowerFrameOpsToRenderPackets(volatileOps, dynamicOverlay: true, resourcePlanRevision, packets);
            return;
        }

        RenderPacket[] staticPackets = new RenderPacket[staticOps.Length];
        RenderPacket[] volatilePackets = new RenderPacket[volatileOps.Length];
        Parallel.For(0, staticOps.Length, i =>
        {
            staticPackets[i] = CreateRenderPacket(staticOps[i], i, dynamicOverlay: false, resourcePlanRevision);
        });
        Parallel.For(0, volatileOps.Length, i =>
        {
            volatilePackets[i] = CreateRenderPacket(volatileOps[i], i, dynamicOverlay: true, resourcePlanRevision);
        });

        packets.AddRange(staticPackets);
        packets.AddRange(volatilePackets);

        if (CommandChainValidationEnabled)
            ValidateParallelRenderPacketBuild(staticOps, volatileOps, resourcePlanRevision, packets);
    }

    private void LowerFrameOpsToRenderPackets(
        FrameOp[] ops,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets)
    {
        for (int i = 0; i < ops.Length; i++)
            packets.Add(CreateRenderPacket(ops[i], i, dynamicOverlay, resourcePlanRevision));
    }

    private static RenderPacket CreateRenderPacket(FrameOp op, int opIndex, bool dynamicOverlay, ulong resourcePlanRevision)
    {
        RenderViewKey viewKey = BuildRenderViewKey(op, dynamicOverlay);
        RenderPacketVolatility volatility = ClassifyRenderPacketVolatility(op, dynamicOverlay);
        DrawPacket firstDraw = op switch
        {
            MeshDrawOp draw => CreateDrawPacket(opIndex, draw),
            IndirectDrawOp indirect => CreateIndirectDrawPacket(opIndex, indirect),
            MeshTaskDispatchIndirectCountOp meshTask => CreateMeshTaskDrawPacket(opIndex, meshTask),
            _ => default
        };
        int drawCount = op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp ? 1 : 0;
        DispatchPacket firstDispatch = op is ComputeDispatchOp compute
            ? CreateDispatchPacket(opIndex, compute)
            : default;
        int dispatchCount = op is ComputeDispatchOp ? 1 : 0;

        ulong structuralSignature = ComputeFrameOpStructuralSignature(op, opIndex, volatility);
        ulong frameDataSignature = ComputeFrameOpFrameDataSignature(op, opIndex);
        int targetIdentity = ResolveCommandChainTargetIdentity(op);
        string targetName = ResolveCommandChainTargetName(op);
        DescriptorBindingSnapshot descriptorSnapshot = CreateDescriptorSnapshot(op);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            unchecked((ulong)targetIdentity),
            unchecked((ulong)targetName.GetHashCode(StringComparison.Ordinal)),
            ResolvePipelineGeneration(op));

        return new RenderPacket(
            viewKey,
            op.PassIndex,
            targetIdentity,
            targetName,
            volatility,
            firstDraw,
            drawCount,
            firstDispatch,
            dispatchCount,
            descriptorSnapshot,
            resourceSnapshot,
            structuralSignature,
            frameDataSignature,
            opIndex,
            1,
            dynamicOverlay);
    }

    private static int BuildCommandChainOrdinal(RenderPacket packet, int fallbackOrdinal)
    {
        int sourceOrdinal = packet.SourceStartIndex >= 0
            ? packet.SourceStartIndex
            : fallbackOrdinal;

        unchecked
        {
            ulong structuralSignature = packet.StructuralSignature;
            int foldedStructuralSignature = (int)structuralSignature ^ (int)(structuralSignature >> 32);
            return HashCode.Combine(sourceOrdinal, foldedStructuralSignature);
        }
    }

    private void ValidateParallelRenderPacketBuild(
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong resourcePlanRevision,
        List<RenderPacket> parallelPackets)
    {
        List<RenderPacket> sequential = new(staticOps.Length + volatileOps.Length);
        LowerFrameOpsToRenderPackets(staticOps, dynamicOverlay: false, resourcePlanRevision, sequential);
        LowerFrameOpsToRenderPackets(volatileOps, dynamicOverlay: true, resourcePlanRevision, sequential);
        if (sequential.Count != parallelPackets.Count)
            throw new InvalidOperationException($"Parallel command-chain packet build produced {parallelPackets.Count} packets; sequential produced {sequential.Count}.");

        for (int i = 0; i < sequential.Count; i++)
            ValidateRenderPacketEquivalent(sequential[i], parallelPackets[i], i);
    }

    private static void ValidateRenderPacketEquivalent(RenderPacket expected, RenderPacket actual, int index)
    {
        if (expected.ViewKey != actual.ViewKey ||
            expected.PassIndex != actual.PassIndex ||
            expected.TargetIdentity != actual.TargetIdentity ||
            !string.Equals(expected.TargetName, actual.TargetName, StringComparison.Ordinal) ||
            expected.Volatility != actual.Volatility ||
            expected.StructuralSignature != actual.StructuralSignature ||
            expected.FrameDataSignature != actual.FrameDataSignature ||
            expected.SourceStartIndex != actual.SourceStartIndex ||
            expected.SourceCount != actual.SourceCount ||
            expected.DynamicOverlay != actual.DynamicOverlay ||
            expected.DrawCount != actual.DrawCount ||
            expected.DispatchCount != actual.DispatchCount)
        {
            throw new InvalidOperationException($"Parallel command-chain packet build mismatch at packet {index}.");
        }
    }

    private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache(uint imageIndex)
    {
        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int index))
            return GetExternalCommandChainCache(imageIndex);

        EnsureIndexedCommandChainCaches();
        return _commandChainCaches![index];
    }

    private int ResolveIndexedCommandChainCacheCount()
        => Math.Max(_commandBuffers?.Length ?? 0, 1);

    private bool TryGetIndexedCommandChainCacheSlot(uint imageIndex, out int index)
    {
        int count = ResolveIndexedCommandChainCacheCount();
        if (imageIndex < (uint)count)
        {
            index = unchecked((int)imageIndex);
            return true;
        }

        index = -1;
        return false;
    }

    private void EnsureIndexedCommandChainCaches()
    {
        int count = ResolveIndexedCommandChainCacheCount();
        if (_commandChainCaches is not null && _commandChainCaches.Length == count)
            return;

        if (_commandChainCaches is not null)
        {
            DestroyCommandChainCaches();
            MarkOpenXrPrimaryCommandBufferVariantsDirty();
        }

        _commandChainCaches = new Dictionary<CommandChainKey, CommandChain>[count];
        for (int i = 0; i < count; i++)
            _commandChainCaches[i] = new Dictionary<CommandChainKey, CommandChain>();
    }

    internal void NotifyTextureDescriptorPublished(string reason)
    {
        bool commandChainsAvailable =
            _commandChainCaches is not null ||
            _externalCommandChainCaches is not null ||
            CommandChainsEnabledForCurrentRecording;
        if (!commandChainsAvailable)
        {
            MarkCommandBuffersDirty(reason);
            return;
        }

        // Descriptor generations are validated while rebuilding the command-chain
        // schedule. Clearing the fast schedule cache prevents stale primary reuse
        // while still letting unchanged chains survive texture streaming.
        if (_commandChainScheduleFastSignatures is not null)
            Array.Clear(_commandChainScheduleFastSignatures);
        if (_commandChainScheduleCache is not null)
            Array.Clear(_commandChainScheduleCache);
    }

    private Dictionary<CommandChainKey, CommandChain> GetExternalCommandChainCache(uint imageIndex)
    {
        Dictionary<uint, Dictionary<CommandChainKey, CommandChain>> caches = _externalCommandChainCaches ??= [];
        if (!caches.TryGetValue(imageIndex, out Dictionary<CommandChainKey, CommandChain>? cache))
        {
            cache = [];
            caches.Add(imageIndex, cache);
        }

        return cache;
    }

    private int InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease()
    {
        int invalidated = 0;
        if (_commandChainCaches is not null)
        {
            for (int i = 0; i < _commandChainCaches.Length; i++)
            {
                Dictionary<CommandChainKey, CommandChain>? cache = _commandChainCaches[i];
                if (cache is null)
                    continue;

                foreach (CommandChain chain in cache.Values)
                {
                    if (chain.SecondaryCommandBuffer.Handle == 0 || !chain.SecondaryCommandBufferExecutable)
                        continue;

                    MarkCommandChainSecondaryCommandBufferInvalid(chain);
                    MarkCommandChainSecondaryCommandBufferChanged(chain);
                    chain.DirtyReason |= CommandChainDirtyReason.DescriptorGeneration;
                    invalidated++;
                }
            }
        }

        if (_externalCommandChainCaches is not null)
        {
            foreach (Dictionary<CommandChainKey, CommandChain> cache in _externalCommandChainCaches.Values)
            {
                foreach (CommandChain chain in cache.Values)
                {
                    if (chain.SecondaryCommandBuffer.Handle == 0 || !chain.SecondaryCommandBufferExecutable)
                        continue;

                    MarkCommandChainSecondaryCommandBufferInvalid(chain);
                    MarkCommandChainSecondaryCommandBufferChanged(chain);
                    chain.DirtyReason |= CommandChainDirtyReason.DescriptorGeneration;
                    invalidated++;
                }
            }
        }

        if (_commandChainScheduleFastSignatures is not null)
            Array.Clear(_commandChainScheduleFastSignatures);
        if (_commandChainScheduleCache is not null)
            Array.Clear(_commandChainScheduleCache);

        if (invalidated > 0)
            MarkOpenXrPrimaryCommandBufferVariantsDirty();

        return invalidated;
    }

    private static CommandChain GetOrCreateCommandChain(Dictionary<CommandChainKey, CommandChain> cache, CommandChainKey key)
    {
        if (!cache.TryGetValue(key, out CommandChain? chain))
        {
            chain = new CommandChain(key);
            cache.Add(key, chain);
        }

        return chain;
    }

    private bool TryEnsureCommandChainSecondaryCommandBuffer(
        CommandChain chain,
        uint imageIndex,
        out CommandBuffer secondary)
    {
        secondary = chain.SecondaryCommandBuffer;
        if (secondary.Handle != 0 && chain.SecondaryCommandPool.Handle != 0)
            return true;

        DestroyCommandChainSecondaryCommandBuffer(chain);

        QueueFamilyIndices queueFamilyIndices = FamilyQueueIndices;
        uint graphicsFamily = queueFamilyIndices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("Graphics queue family is not available.");
        CommandPool pool = CreateCommandPoolForFamily(graphicsFamily);
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1
        };

        Result allocateResult = Api!.AllocateCommandBuffers(device, ref allocInfo, out secondary);
        if (allocateResult != Result.Success || secondary.Handle == 0)
        {
            if (pool.Handle != 0)
                Api!.DestroyCommandPool(device, pool, null);

            secondary = default;
            return false;
        }

        chain.SecondaryCommandBuffer = secondary;
        chain.SecondaryCommandPool = pool;
        chain.OwnsSecondaryCommandPool = true;
        chain.SecondaryCommandBufferExecutable = false;
        TrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);
        RegisterCommandBufferImageIndex(secondary, imageIndex);
        SetDebugObjectName(ObjectType.CommandPool, pool.Handle, BuildCommandChainSecondaryDebugName(chain, imageIndex, "Pool"));
        SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)secondary.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
        MarkCommandChainSecondaryCommandBufferChanged(chain);
        return true;
    }

    private bool TryEnsureMutableCommandChainSecondaryCommandBuffer(
        CommandChain chain,
        uint imageIndex,
        HashSet<nint> executedSecondaryHandles,
        out CommandBuffer secondary)
    {
        if (!TryEnsureCommandChainSecondaryCommandBuffer(chain, imageIndex, out secondary))
            return false;

        if (secondary.Handle == 0 || !executedSecondaryHandles.Contains(secondary.Handle))
            return true;

        CommandPool pool = chain.SecondaryCommandPool;
        if (pool.Handle == 0)
            return false;

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Secondary,
            CommandBufferCount = 1
        };

        Result allocateResult = Api!.AllocateCommandBuffers(device, ref allocInfo, out CommandBuffer replacement);
        if (allocateResult != Result.Success || replacement.Handle == 0)
            return false;

        DeferSecondaryCommandBufferFree(imageIndex, pool, secondary);
        chain.SecondaryCommandBuffer = replacement;
        chain.SecondaryCommandBufferExecutable = false;
        TrackOwnedCommandChainSecondaryCommandBuffer(pool, replacement);
        RegisterCommandBufferImageIndex(replacement, imageIndex);
        SetDebugObjectName(ObjectType.CommandBuffer, unchecked((ulong)replacement.Handle), BuildCommandChainSecondaryDebugName(chain, imageIndex, "Secondary"));
        MarkCommandChainSecondaryCommandBufferChanged(chain);

        secondary = replacement;
        return true;
    }

    private static string BuildCommandChainSecondaryDebugName(CommandChain chain, uint imageIndex, string suffix)
        => $"CommandChain.{suffix} image={imageIndex} frameSlot={chain.Key.FrameSlot} pass={chain.Key.PassIndex} target={chain.Key.TargetIdentity} view={chain.Key.ViewKey.Kind}:{chain.Key.ViewKey.ViewIndex} ordinal={chain.Key.ChainOrdinal}";

    private void MarkCommandChainSecondaryCommandBufferRecorded(CommandChain chain)
    {
        chain.SecondaryCommandBufferExecutable = true;
        MarkCommandChainSecondaryCommandBufferChanged(chain);
    }

    private static void MarkCommandChainSecondaryCommandBufferInvalid(CommandChain chain)
        => chain.SecondaryCommandBufferExecutable = false;

    private static void MarkCommandChainSecondaryCommandBufferChanged(CommandChain chain)
    {
        unchecked
        {
            chain.SecondaryCommandBufferGeneration++;
        }
    }

    private void DestroyCommandChainSecondaryCommandBuffer(CommandChain chain)
    {
        CommandBuffer secondary = chain.SecondaryCommandBuffer;
        CommandPool pool = chain.SecondaryCommandPool;
        bool ownsPool = chain.OwnsSecondaryCommandPool;

        if (ownsPool && pool.Handle != 0)
            MarkOwnedCommandChainSecondaryPoolPendingDestroy(pool);

        if (secondary.Handle != 0)
        {
            if (!_deviceLost && pool.Handle != 0)
            {
                int imageIndex = ResolveCommandBufferImageIndex(secondary);
                if (imageIndex >= 0)
                {
                    DeferSecondaryCommandBufferFree(unchecked((uint)imageIndex), pool, secondary);
                }
                else
                {
                    CommandBuffer freedSecondary = secondary;
                    Api!.FreeCommandBuffers(device, pool, 1, ref secondary);
                    RemoveCommandBufferBindState(freedSecondary);
                    UntrackOwnedCommandChainSecondaryCommandBuffer(pool, freedSecondary);
                    DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
                }
            }
            else
            {
                RemoveCommandBufferBindState(secondary);
                UntrackOwnedCommandChainSecondaryCommandBuffer(pool, secondary);
                DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
            }
        }
        else if (ownsPool && pool.Handle != 0)
        {
            DestroyPendingOwnedCommandChainSecondaryPoolIfEmpty(pool);
        }

        chain.SecondaryCommandBuffer = default;
        chain.SecondaryCommandPool = default;
        chain.OwnsSecondaryCommandPool = false;
        chain.SecondaryCommandBufferExecutable = false;
        MarkCommandChainSecondaryCommandBufferChanged(chain);
    }

    internal static CommandChainDirtyReason EvaluateCommandChainDirtyReason(CommandChain chain, RenderPacket packet)
    {
        if (chain.StructuralSignature == 0)
            return CommandChainDirtyReason.Structure;

        CommandChainDirtyReason reason = CommandChainDirtyReason.None;
        if (chain.StructuralSignature != packet.StructuralSignature)
            reason |= CommandChainDirtyReason.Structure;
        if (chain.DrawCount != packet.DrawCount ||
            chain.DispatchCount != packet.DispatchCount ||
            chain.InstanceCountSignature != ComputePacketInstanceCountSignature(packet) ||
            chain.DescriptorSetCount != packet.DescriptorSnapshot.DescriptorSetCount ||
            chain.DescriptorSetSignature != packet.DescriptorSnapshot.DescriptorSetSignature)
        {
            reason |= CommandChainDirtyReason.Structure;
        }

        if (chain.ResourcePlanRevision != packet.ResourcePlanSnapshot.Revision)
            reason |= CommandChainDirtyReason.ResourcePlan;
        if (chain.PhysicalImageSignature != packet.ResourcePlanSnapshot.PhysicalImageSignature ||
            chain.FramebufferSignature != packet.ResourcePlanSnapshot.FramebufferSignature)
        {
            reason |= CommandChainDirtyReason.ResourcePlan;
        }
        if (chain.DescriptorGeneration != packet.DescriptorSnapshot.DescriptorGeneration)
            reason |= CommandChainDirtyReason.DescriptorGeneration;
        if (chain.PipelineGeneration != packet.ResourcePlanSnapshot.PipelineGeneration)
            reason |= CommandChainDirtyReason.PipelineGeneration;
        return reason;
    }

    internal static void ValidateReusableCommandChainReferences(CommandChain chain, RenderPacket packet)
    {
        CommandChainDirtyReason reason = EvaluateCommandChainDirtyReason(chain, packet);
        if (reason == CommandChainDirtyReason.None)
            return;

        string staleKind =
            (reason & CommandChainDirtyReason.DescriptorGeneration) != 0 ||
            chain.DescriptorSetCount != packet.DescriptorSnapshot.DescriptorSetCount ||
            chain.DescriptorSetSignature != packet.DescriptorSnapshot.DescriptorSetSignature
                ? "descriptor-set"
                : chain.PhysicalImageSignature != packet.ResourcePlanSnapshot.PhysicalImageSignature
                    ? "physical-image"
                    : chain.FramebufferSignature != packet.ResourcePlanSnapshot.FramebufferSignature
                        ? "framebuffer"
                        : (reason & CommandChainDirtyReason.PipelineGeneration) != 0
                            ? "pipeline"
                            : "structure";

        throw new InvalidOperationException(
            $"Reusable command chain '{chain.Key}' references stale {staleKind} state. " +
            $"reason={reason}; previous={{descriptorGeneration={chain.DescriptorGeneration}, descriptorSets={chain.DescriptorSetCount}, descriptorSig=0x{chain.DescriptorSetSignature:X16}, resourceRevision={chain.ResourcePlanRevision}, physicalImages=0x{chain.PhysicalImageSignature:X16}, framebuffers=0x{chain.FramebufferSignature:X16}, pipelineGeneration={chain.PipelineGeneration}}}; " +
            $"current={{descriptorGeneration={packet.DescriptorSnapshot.DescriptorGeneration}, descriptorSets={packet.DescriptorSnapshot.DescriptorSetCount}, descriptorSig=0x{packet.DescriptorSnapshot.DescriptorSetSignature:X16}, resourceRevision={packet.ResourcePlanSnapshot.Revision}, physicalImages=0x{packet.ResourcePlanSnapshot.PhysicalImageSignature:X16}, framebuffers=0x{packet.ResourcePlanSnapshot.FramebufferSignature:X16}, pipelineGeneration={packet.ResourcePlanSnapshot.PipelineGeneration}}}.");
    }

    internal static bool TryRefreshReusableCommandChainFrameData(CommandChain chain, RenderPacket packet)
    {
        if (packet.Volatility != RenderPacketVolatility.FrameDataOnly)
            return false;

        CommandChainDirtyReason dirtyReason = EvaluateCommandChainDirtyReason(chain, packet);
        if (dirtyReason != CommandChainDirtyReason.None)
            return false;

        chain.FrameDataSignature = packet.FrameDataSignature;
        chain.FrameDataRefreshTouchedDescriptors = false;
        return true;
    }

    private static bool CanRefreshCommandChainFrameData(CommandChainDirtyReason dirtyReason, RenderPacket packet)
        => packet.Volatility == RenderPacketVolatility.FrameDataOnly &&
            dirtyReason == CommandChainDirtyReason.None;

    internal static PrimaryCommandBufferDirtyReason EvaluatePrimaryCommandBufferDirtyReason(
        CommandChainSchedule schedule,
        ulong recordedScheduleSignature,
        ulong recordedGroupSignature,
        int recordedGroupCount,
        ulong recordedResourcePlanRevision,
        bool recordedProfilerActive,
        int recordedProfilerFrameSlot,
        bool currentProfilerActive,
        int currentProfilerFrameSlot)
        => EvaluatePrimaryCommandBufferDirtyReason(
            schedule,
            recordedScheduleSignature,
            recordedGroupSignature,
            recordedGroupCount,
            ComputePrimaryCommandBufferGroupSignature(schedule),
            recordedResourcePlanRevision,
            recordedProfilerActive,
            recordedProfilerFrameSlot,
            currentProfilerActive,
            currentProfilerFrameSlot);

    internal static PrimaryCommandBufferDirtyReason EvaluatePrimaryCommandBufferDirtyReason(
        CommandChainSchedule schedule,
        ulong recordedScheduleSignature,
        ulong recordedGroupSignature,
        int recordedGroupCount,
        ulong currentGroupSignature,
        ulong recordedResourcePlanRevision,
        bool recordedProfilerActive,
        int recordedProfilerFrameSlot,
        bool currentProfilerActive,
        int currentProfilerFrameSlot)
    {
        PrimaryCommandBufferDirtyReason reason = PrimaryCommandBufferDirtyReason.None;
        if (recordedScheduleSignature != schedule.StructuralSignature)
            reason |= PrimaryCommandBufferDirtyReason.ScheduleStructure;
        if (recordedGroupSignature != currentGroupSignature ||
            recordedGroupCount != schedule.Groups.Length)
        {
            reason |= PrimaryCommandBufferDirtyReason.GroupStructure;
        }
        if (recordedResourcePlanRevision != schedule.ResourcePlanRevision)
            reason |= PrimaryCommandBufferDirtyReason.ResourcePlan;
        if (recordedProfilerActive != currentProfilerActive ||
            (currentProfilerActive && recordedProfilerFrameSlot != currentProfilerFrameSlot))
        {
            reason |= PrimaryCommandBufferDirtyReason.ProfilerMode;
        }

        return reason;
    }

    private static ulong ComputeScheduleStructuralSignature(ReadOnlySpan<RenderPassChainGroup> groups)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            hash.Add(group.PassIndex);
            hash.Add(group.TargetIdentity);
            hash.Add(group.StructuralSignature);
            hash.Add(group.SupportsSecondaryCommandBuffers);
            hash.Add(group.DynamicOverlay);

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            hash.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                hash.Add(key.FrameSlot);
                hash.Add(key.PassIndex);
                hash.Add(key.TargetIdentity);
                hash.Add(key.ChainOrdinal);
                hash.Add(key.ViewKey.PipelineIdentity);
                hash.Add(key.ViewKey.ViewportIdentity);
                hash.Add(key.ViewKey.ViewIndex);
                hash.Add((int)key.ViewKey.Kind);
                hash.Add(key.ViewKey.LightIdentity);
                hash.Add(key.ViewKey.CascadeIndex);
            }
        }

        return hash.ToHash();
    }

    internal static ulong ComputePrimaryCommandBufferGroupSignature(CommandChainSchedule schedule)
        => ComputePrimaryCommandBufferGroupSignature(schedule, null);

    internal static ulong ComputePrimaryCommandBufferGroupSignature(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains)
    {
        FrameOpSignatureHasher hash = new();
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        hash.Add(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            hash.Add(group.PassIndex);
            hash.Add(group.TargetIdentity);
            hash.Add(group.StructuralSignature);
            hash.Add(group.SupportsSecondaryCommandBuffers);
            hash.Add(group.DynamicOverlay);
            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            hash.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                hash.Add(key.FrameSlot);
                hash.Add(key.PassIndex);
                hash.Add(key.TargetIdentity);
                hash.Add(key.ChainOrdinal);
                hash.Add(key.ViewKey.PipelineIdentity);
                hash.Add(key.ViewKey.ViewportIdentity);
                hash.Add(key.ViewKey.ViewIndex);
                hash.Add((int)key.ViewKey.Kind);
                hash.Add(key.ViewKey.LightIdentity);
                hash.Add(key.ViewKey.CascadeIndex);
                if (chains is not null && chains.TryGetValue(key, out CommandChain? chain))
                {
                    hash.Add(chain.SecondaryCommandBuffer.Handle);
                    hash.Add(chain.SecondaryCommandBufferGeneration);
                }
                else
                {
                    hash.Add(0UL);
                    hash.Add(0UL);
                }
            }
        }

        return hash.ToHash();
    }

    internal static CommandChainKey[] BuildCommandChainKeysByFrameOpIndex(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> commandChains,
        int staticOpCount)
    {
        if (staticOpCount <= 0)
            return [];

        CommandChainKey[] keysByOpIndex = new CommandChainKey[staticOpCount];
        CommandChainKey unmappedKey = new(0, default, 0, 0, false, -1);
        Array.Fill(keysByOpIndex, unmappedKey);
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            RenderPassChainGroup group = groups[groupIndex];
            if (group.DynamicOverlay)
                continue;

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                if (!commandChains.TryGetValue(key, out CommandChain? chain) ||
                    chain.SourceStartIndex < 0 ||
                    chain.SourceCount <= 0)
                {
                    continue;
                }

                int endIndex = Math.Min(staticOpCount, chain.SourceStartIndex + chain.SourceCount);
                for (int opIndex = chain.SourceStartIndex; opIndex < endIndex; opIndex++)
                    keysByOpIndex[opIndex] = key;
            }
        }

        return keysByOpIndex;
    }

    internal static bool TryGetCommandChainScheduleFrameSlot(
        CommandChainSchedule schedule,
        out int frameSlot)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            if (keys.Length == 0)
                continue;

            frameSlot = keys[0].FrameSlot;
            return true;
        }

        frameSlot = 0;
        return false;
    }

    internal static void ValidatePrimaryCommandChainSchedule(
        CommandChainSchedule schedule,
        FrameOp[] staticOps,
        int dynamicOverlayOpCount)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        if (groups.Length == 0)
        {
            if (staticOps.Length != 0 || dynamicOverlayOpCount != 0)
                throw new InvalidOperationException("Command-chain primary schedule has no groups for non-empty frame ops.");
            return;
        }

        int groupIndex = 0;
        int opIndex = 0;
        while (opIndex < staticOps.Length)
        {
            FrameOp op = staticOps[opIndex];
            int passIndex = op.PassIndex;
            int targetIdentity = ResolveCommandChainTargetIdentity(op);
            int groupOpCount = 1;
            for (int i = opIndex + 1; i < staticOps.Length; i++)
            {
                FrameOp candidate = staticOps[i];
                if (candidate.PassIndex != passIndex ||
                    ResolveCommandChainTargetIdentity(candidate) != targetIdentity)
                {
                    break;
                }

                groupOpCount++;
            }

            if (groupIndex >= groups.Length)
                throw new InvalidOperationException("Command-chain primary schedule ended before all static frame-op groups were represented.");

            RenderPassChainGroup group = groups[groupIndex];
            if (group.DynamicOverlay)
                throw new InvalidOperationException("Command-chain primary schedule placed a dynamic overlay group before all static groups.");
            if (group.PassIndex != passIndex || group.TargetIdentity != targetIdentity)
            {
                throw new InvalidOperationException(
                    $"Command-chain primary schedule group {groupIndex} does not match static frame-op group: expected pass={passIndex} target={targetIdentity}, current pass={group.PassIndex} target={group.TargetIdentity}.");
            }
            if (group.ChainKeys.Length != groupOpCount)
            {
                throw new InvalidOperationException(
                    $"Command-chain primary schedule group {groupIndex} has {group.ChainKeys.Length} chains for {groupOpCount} static frame ops.");
            }

            groupIndex++;
            opIndex += groupOpCount;
        }

        int dynamicChainCount = 0;
        for (; groupIndex < groups.Length; groupIndex++)
        {
            RenderPassChainGroup group = groups[groupIndex];
            if (!group.DynamicOverlay)
                throw new InvalidOperationException("Command-chain primary schedule placed a static group after dynamic overlay groups.");

            dynamicChainCount += group.ChainKeys.Length;
        }

        if (dynamicChainCount != dynamicOverlayOpCount)
        {
            throw new InvalidOperationException(
                $"Command-chain primary schedule has {dynamicChainCount} dynamic overlay chains for {dynamicOverlayOpCount} dynamic overlay frame ops.");
        }
    }

    internal static ulong ComputePacketInstanceCountSignature(RenderPacket packet)
    {
        FrameOpSignatureHasher hash = new();
        for (int i = 0; i < packet.DrawCount; i++)
            hash.Add(packet.GetDraw(i).InstanceCount);

        return hash.ToHash();
    }

    private static string DescribeCommandChainDirtyReason(CommandChain chain, RenderPacket packet)
    {
        ulong currentInstanceCountSignature = ComputePacketInstanceCountSignature(packet);
        StringBuilder details = new();
        AppendIfChanged(details, "draw-count", chain.DrawCount, packet.DrawCount);
        AppendIfChanged(details, "dispatch-count", chain.DispatchCount, packet.DispatchCount);
        AppendIfChanged(details, "instance-counts", chain.InstanceCountSignature, currentInstanceCountSignature);
        AppendIfChanged(details, "descriptor-set-count", chain.DescriptorSetCount, packet.DescriptorSnapshot.DescriptorSetCount);
        AppendIfChanged(details, "descriptor-set-signature", chain.DescriptorSetSignature, packet.DescriptorSnapshot.DescriptorSetSignature);
        AppendIfChanged(details, "descriptor-generation", chain.DescriptorGeneration, packet.DescriptorSnapshot.DescriptorGeneration);
        AppendIfChanged(details, "resource-plan-revision", chain.ResourcePlanRevision, packet.ResourcePlanSnapshot.Revision);
        AppendIfChanged(details, "physical-image-signature", chain.PhysicalImageSignature, packet.ResourcePlanSnapshot.PhysicalImageSignature);
        AppendIfChanged(details, "framebuffer-signature", chain.FramebufferSignature, packet.ResourcePlanSnapshot.FramebufferSignature);
        AppendIfChanged(details, "pipeline-generation", chain.PipelineGeneration, packet.ResourcePlanSnapshot.PipelineGeneration);
        if ((chain.DirtyReason & CommandChainDirtyReason.SecondaryCommandBufferInvalid) != 0)
        {
            AppendDetail(details, "secondary-handle", $"0x{chain.SecondaryCommandBuffer.Handle:X}");
            AppendDetail(details, "secondary-executable", chain.SecondaryCommandBufferExecutable.ToString());
            AppendDetail(details, "secondary-generation", chain.SecondaryCommandBufferGeneration.ToString());
        }

        string detailText = details.Length == 0 ? string.Empty : $" details=[{details}]";
        return $"key={chain.Key} reason={chain.DirtyReason} previousSig=0x{chain.StructuralSignature:X16} currentSig=0x{packet.StructuralSignature:X16} volatility={packet.Volatility}{detailText}";

        static void AppendDetail(StringBuilder builder, string label, string value)
        {
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(label)
                .Append('=')
                .Append(value);
        }

        static void AppendIfChanged<T>(StringBuilder builder, string label, T previous, T current)
            where T : IEquatable<T>
        {
            if (previous.Equals(current))
                return;

            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(label)
                .Append('=')
                .Append(previous)
                .Append("->")
                .Append(current);
        }
    }

    internal static RenderPacketVolatility ClassifyRenderPacketVolatility(FrameOp op, bool dynamicOverlay)
    {
        if (dynamicOverlay || IsUiBatchTextDrawOp(op))
            return RenderPacketVolatility.DynamicCommand;

        if (IsOverlayLikePass(op))
            return RenderPacketVolatility.DynamicCommand;

        return op switch
        {
            MeshDrawOp => RenderPacketVolatility.FrameDataOnly,
            ClearOp => RenderPacketVolatility.StaticStructural,
            BlitOp => RenderPacketVolatility.StaticStructural,
            IndirectDrawOp => RenderPacketVolatility.FrameDataOnly,
            MeshTaskDispatchIndirectCountOp => RenderPacketVolatility.FrameDataOnly,
            ComputeDispatchOp => RenderPacketVolatility.FrameDataOnly,
            MemoryBarrierOp => RenderPacketVolatility.StaticStructural,
            PublishFramebufferForSamplingOp => RenderPacketVolatility.StaticStructural,
            TransformFeedbackOp => RenderPacketVolatility.DynamicCommand,
            DlssUpscaleOp => RenderPacketVolatility.DynamicCommand,
            DlssFrameGenerationOp => RenderPacketVolatility.DynamicCommand,
            TextureUploadFrameOp => RenderPacketVolatility.DynamicCommand,
            _ => RenderPacketVolatility.StructuralDirty,
        };
    }

    private static bool IsOverlayLikePass(FrameOp op)
    {
        string? name = TryGetPassName(op);
        return !string.IsNullOrWhiteSpace(name) &&
            (name.Contains("UI", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Profiler", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Gizmo", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    internal static RenderViewKey BuildRenderViewKey(FrameOp op, bool dynamicOverlay)
    {
        RenderViewKind kind = dynamicOverlay || IsOverlayLikePass(op)
            ? RenderViewKind.Overlay
            : ResolveRenderViewKind(op);
        int viewIndex = ResolveCommandChainViewIndex(op, kind);
        int lightIdentity = ResolveCommandChainLightIdentity(op, kind);
        int cascadeIndex = ResolveCommandChainCascadeIndex(op, kind);
        return new RenderViewKey(
            op.Context.PipelineIdentity,
            op.Context.ViewportIdentity,
            viewIndex,
            kind,
            lightIdentity,
            cascadeIndex);
    }

    private static RenderViewKind ResolveRenderViewKind(FrameOp op)
    {
        if (op is MeshDrawOp { Draw: var draw } &&
            (draw.IsStereoPass ||
             draw.Camera?.StereoEyeLeft.HasValue == true ||
             draw.StereoRightEyeCamera is not null))
        {
            return RenderViewKind.VREye;
        }

        string? passName = TryGetPassName(op);
        if (passName is not null)
        {
            if (passName.Contains("Shadow", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Shadow;
            if (passName.Contains("Reflection", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Reflection;
            if (passName.Contains("Probe", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Probe;
        }

        return RenderViewKind.Main;
    }

    private static int ResolveCommandChainViewIndex(FrameOp op, RenderViewKind kind)
    {
        if (kind == RenderViewKind.VREye && op is MeshDrawOp { Draw: var draw })
            return ResolveStereoViewIndex(draw);

        if (kind == RenderViewKind.Shadow && op is MeshDrawOp { Draw: { ShadowUniformState: var shadowState } })
        {
            if (shadowState.DirectionalCascadeInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.DirectionalCascadeShadowLayerCount - 1);
            if (shadowState.PointLightInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.PointLightShadowFaceCount - 1);
        }

        return 0;
    }

    private static int ResolveStereoViewIndex(in PendingMeshDraw draw)
    {
        if (draw.IsStereoPass)
            return CommandChainStereoMultiviewViewIndex;

        bool? cameraEyeLeft = draw.Camera?.StereoEyeLeft;
        if (cameraEyeLeft.HasValue)
            return cameraEyeLeft.Value ? CommandChainLeftEyeViewIndex : CommandChainRightEyeViewIndex;

        if (draw.StereoRightEyeCamera is not null && ReferenceEquals(draw.Camera, draw.StereoRightEyeCamera))
            return CommandChainRightEyeViewIndex;

        return CommandChainLeftEyeViewIndex;
    }

    private static int ResolveCommandChainLightIdentity(FrameOp op, RenderViewKind kind)
    {
        if (kind != RenderViewKind.Shadow)
            return 0;

        int identity = HashCode.Combine(
            op.Context.SchedulingIdentity,
            ResolveCommandChainTargetIdentity(op));
        return identity == 0 ? 1 : identity;
    }

    private static int ResolveCommandChainCascadeIndex(FrameOp op, RenderViewKind kind)
    {
        if (kind != RenderViewKind.Shadow)
            return -1;

        if (op is MeshDrawOp { Draw: { ShadowUniformState: var shadowState } })
        {
            if (shadowState.DirectionalCascadeInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.DirectionalCascadeShadowLayerCount - 1);
            if (shadowState.PointLightInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.PointLightShadowFaceCount - 1);
        }

        return Math.Max(0, op.PassIndex);
    }

    private static string? TryGetPassName(FrameOp op)
    {
        if (op.Context.PassMetadata is null)
            return null;

        foreach (RenderPassMetadata pass in op.Context.PassMetadata)
        {
            if (pass.PassIndex == op.PassIndex)
                return pass.Name;
        }

        return null;
    }

    private static string ResolvePassName(IReadOnlyCollection<RenderPassMetadata>? passMetadata, int passIndex)
    {
        if (passMetadata is null)
            return "<unknown>";

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (pass.PassIndex == passIndex)
                return pass.Name;
        }

        return "<unknown>";
    }

    internal static int ResolveCommandChainTargetIdentity(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.GetHashCode() ?? op.Context.OutputTargetIdentity,
            _ => op.Target?.GetHashCode() ?? op.Context.OutputTargetIdentity,
        };

    internal static string ResolveCommandChainTargetName(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.Name ?? op.Context.OutputTargetName ?? "<swapchain>",
            _ => op.Target?.Name ?? op.Context.OutputTargetName ?? "<swapchain>",
        };

    private static ulong ResolvePipelineGeneration(FrameOp op)
        => unchecked((ulong)op.Context.PipelineIdentity);

    private static DescriptorBindingSnapshot CreateDescriptorSnapshot(FrameOp op)
    {
        return op switch
        {
            MeshDrawOp draw => CreateMeshDrawDescriptorSnapshot(draw),
            ComputeDispatchOp compute => CreateComputeDispatchDescriptorSnapshot(compute),
            IndirectDrawOp indirect => CreateDescriptorSnapshotFromSignature(unchecked((ulong)(indirect.BindlessMaterialTextures?.Program.GetHashCode() ?? 0))),
            MeshTaskDispatchIndirectCountOp meshTask => CreateDescriptorSnapshotFromSignature(unchecked((ulong)(meshTask.BindlessMaterialTextures?.Program.GetHashCode() ?? 0))),
            _ => default,
        };
    }

    private static DescriptorBindingSnapshot CreateMeshDrawDescriptorSnapshot(MeshDrawOp draw)
    {
        ulong descriptorGeneration = 0UL;
        ulong descriptorSetSignature = 0UL;
        int setCount = 0;

        if (draw.Draw.ProgramBindingSnapshot is { } snapshot)
        {
            descriptorGeneration = ComputeDispatchSnapshotSignature(snapshot);
            descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(snapshot);
            setCount = descriptorSetSignature == 0 ? 0 : 1;
        }

        XRMaterial? material = draw.Draw.MaterialOverride ?? draw.Draw.Renderer.MeshRenderer.Material;
        if (material is not null)
        {
            ulong descriptorResourceSignature = draw.Draw.Renderer.ComputeRecordedDescriptorResourceSignature(
                material,
                draw.Draw.PreparedProgram);
            if (descriptorResourceSignature != 0UL)
                descriptorGeneration = MixSignature(descriptorGeneration, descriptorResourceSignature);
        }

        ulong descriptorSchemaSignature = draw.Draw.Renderer.ComputeRecordedDescriptorSchemaSignature(draw.Draw.PreparedProgram);
        if (descriptorSchemaSignature != 0UL)
            descriptorSetSignature = MixSignature(descriptorSetSignature, descriptorSchemaSignature);

        int recordedSetCount = draw.Draw.Renderer.GetRecordedDescriptorSetCount(draw.Draw.PreparedProgram);
        if (recordedSetCount > setCount)
            setCount = recordedSetCount;
        return new DescriptorBindingSnapshot(descriptorGeneration, setCount, descriptorSetSignature);
    }

    private static DescriptorBindingSnapshot CreateComputeDispatchDescriptorSnapshot(ComputeDispatchOp compute)
    {
        ulong descriptorGeneration = ComputeDispatchSnapshotSignature(compute.Snapshot);
        ulong descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(compute.Snapshot);
        int setCount = descriptorSetSignature == 0 ? 0 : 1;
        return new DescriptorBindingSnapshot(descriptorGeneration, setCount, descriptorSetSignature);
    }

    private static DescriptorBindingSnapshot CreateDescriptorSnapshotFromSignature(ulong signature)
    {
        int setCount = signature == 0 ? 0 : 1;
        return new DescriptorBindingSnapshot(signature, setCount, signature);
    }

    private static DrawPacket CreateDrawPacket(int opIndex, MeshDrawOp op)
    {
        XRMaterial? material = op.Draw.MaterialOverride ?? op.Draw.Renderer.MeshRenderer.Material;
        int meshIdentity = op.Draw.Renderer.MeshRenderer.Mesh?.GetHashCode() ?? 0;
        int materialIdentity = material?.GetHashCode() ?? 0;
        int programIdentity = material?.RenderOptions?.GetHashCode() ?? materialIdentity;
        return new DrawPacket(
            opIndex,
            op.Draw.Renderer.GetHashCode(),
            meshIdentity,
            materialIdentity,
            programIdentity,
            op.Draw.Instances,
            op.Draw.BlendEnabled,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));
    }

    private static DrawPacket CreateIndirectDrawPacket(int opIndex, IndirectDrawOp op)
        => new(
            opIndex,
            op.IndirectBuffer.GetHashCode(),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.IndirectBuffer)),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.ParameterBuffer)),
            op.BindlessMaterialTextures?.Program.GetHashCode() ?? 0,
            op.DrawCount,
            false,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static DrawPacket CreateMeshTaskDrawPacket(int opIndex, MeshTaskDispatchIndirectCountOp op)
        => new(
            opIndex,
            op.IndirectBuffer.GetHashCode(),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.CountBuffer)),
            0,
            op.BindlessMaterialTextures?.Program.GetHashCode() ?? 0,
            op.MaxDrawCount,
            false,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static DispatchPacket CreateDispatchPacket(int opIndex, ComputeDispatchOp op)
        => new(
            opIndex,
            op.Program.GetHashCode(),
            op.GroupsX,
            op.GroupsY,
            op.GroupsZ,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static ulong ComputeReusableComputeDescriptorBindingKey(ComputeDispatchOp op, int opIndex)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x434F4D5055444553UL);
        hash.Add(opIndex);
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(op.Context.PipelineIdentity);
        hash.Add(op.Context.ViewportIdentity);
        hash.Add(op.Program.GetHashCode());
        hash.Add(op.GroupsX);
        hash.Add(op.GroupsY);
        hash.Add(op.GroupsZ);
        hash.Add(ComputeDispatchSnapshotDescriptorSetSignature(op.Snapshot));
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpStructuralSignature(FrameOp op, int opIndex, RenderPacketVolatility volatility)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(GetFrameOpKindId(op));
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(op.Context.PipelineIdentity);
        hash.Add(op.Context.ViewportIdentity);
        hash.Add((int)volatility);

        switch (op)
        {
            case MeshDrawOp draw:
                RenderViewKind drawKind = ResolveRenderViewKind(draw);
                hash.Add((int)drawKind);
                hash.Add(ResolveCommandChainViewIndex(draw, drawKind));
                hash.Add(ResolveCommandChainLightIdentity(draw, drawKind));
                hash.Add(ResolveCommandChainCascadeIndex(draw, drawKind));
                hash.Add(draw.Draw.Renderer.GetHashCode());
                hash.Add(draw.Draw.MaterialOverride?.GetHashCode() ?? 0);
                hash.Add(draw.Draw.Instances);
                hash.Add(draw.Draw.BlendEnabled);
                hash.Add(draw.Draw.AlphaToCoverageEnabled);
                hash.Add((int)draw.Draw.ColorBlendOp);
                hash.Add((int)draw.Draw.AlphaBlendOp);
                hash.Add((int)draw.Draw.SrcColorBlendFactor);
                hash.Add((int)draw.Draw.DstColorBlendFactor);
                hash.Add((int)draw.Draw.SrcAlphaBlendFactor);
                hash.Add((int)draw.Draw.DstAlphaBlendFactor);
                hash.Add((int)draw.Draw.ColorWriteMask);
                hash.Add((int)draw.Draw.CullMode);
                hash.Add((int)draw.Draw.FrontFace);
                hash.Add((int)draw.Draw.RasterizationSamples);
                hash.Add(draw.Draw.DepthTestEnabled);
                hash.Add(draw.Draw.DepthWriteEnabled);
                hash.Add((int)draw.Draw.DepthCompareOp);
                hash.Add(draw.Draw.StencilTestEnabled);
                hash.Add(draw.Draw.StencilWriteMask);
                AddViewportScissorSignature(ref hash, draw.Draw);
                hash.Add(draw.Draw.PreparedProgramIdentity);
                hash.Add(ComputeShadowCommandChainStructuralSignature(draw.Draw.ShadowUniformState));
                break;
            case QueryOp query:
                hash.Add(query.Query.GetHashCode());
                hash.Add((int)query.QueryTarget);
                hash.Add((int)query.Operation);
                break;
            case ClearOp clear:
                hash.Add(clear.ClearColor);
                hash.Add(clear.ClearDepth);
                hash.Add(clear.ClearStencil);
                break;
            case BlitOp blit:
                hash.Add(blit.InFbo?.GetHashCode() ?? 0);
                hash.Add(blit.OutFbo?.GetHashCode() ?? 0);
                hash.Add(blit.ColorBit);
                hash.Add(blit.DepthBit);
                hash.Add(blit.StencilBit);
                break;
            case PublishFramebufferForSamplingOp publish:
                hash.Add(publish.FrameBuffer.GetHashCode());
                break;
            case IndirectDrawOp indirect:
                hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
                hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
                hash.Add(indirect.DrawCount);
                hash.Add(indirect.Stride);
                hash.Add(indirect.ByteOffset);
                hash.Add(indirect.CountByteOffset);
                hash.Add(indirect.UseCount);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.IndirectBuffer));
                hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.CountBuffer));
                hash.Add(meshTask.MaxDrawCount);
                hash.Add(meshTask.Stride);
                break;
            case ComputeDispatchOp compute:
                hash.Add(compute.Program.GetHashCode());
                hash.Add(compute.GroupsX);
                hash.Add(compute.GroupsY);
                hash.Add(compute.GroupsZ);
                break;
            case TextureUploadFrameOp upload:
                hash.Add(upload.Upload.PublicationToken);
                hash.Add(upload.Upload.Request.StreamingGeneration);
                hash.Add(upload.Upload.Image.Handle);
                hash.Add(upload.Upload.ImageView.Handle);
                hash.Add(upload.Upload.Sampler.Handle);
                hash.Add(upload.Upload.Extent.Width);
                hash.Add(upload.Upload.Extent.Height);
                hash.Add(upload.Upload.MipLevels);
                hash.Add((ulong)Math.Max(upload.Upload.CommittedBytes, 0L));
                hash.Add(upload.Upload.StagingResources.Length);
                break;
            default:
                hash.Add(opIndex);
                break;
        }

        return hash.ToHash();
    }

    internal static ulong ComputeShadowCommandChainStructuralSignature(in LayeredShadowUniformState shadowState)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(shadowState.IsShadowPass);
        hash.Add(shadowState.DirectionalCascadeInstancedLayeredShadowPass);
        hash.Add(shadowState.DirectionalCascadeShadowLayerCount);
        hash.Add(shadowState.PointLightInstancedLayeredShadowPass);
        hash.Add(shadowState.PointLightShadowFaceCount);
        for (int i = 0; i < shadowState.PointLightShadowFaceCount; i++)
        {
            shadowState.TryGetPointLightShadowFaceIndex(i, out int faceIndex);
            hash.Add(faceIndex);
        }

        return hash.ToHash();
    }

    internal static void ValidateCommandChainShadowFallbackMode(ShadowFallbackMode fallbackMode, bool shadowTileResident)
    {
        if (shadowTileResident)
        {
            if (fallbackMode is not ShadowFallbackMode.None and not ShadowFallbackMode.StaleTile)
            {
                throw new InvalidOperationException(
                    $"Command-chain shadow validation rejected resident shadow tile with fallback mode {fallbackMode}.");
            }

            return;
        }

        if (fallbackMode == ShadowFallbackMode.None)
            throw new InvalidOperationException("Command-chain shadow validation rejected non-resident shadow tile without an explicit fallback mode.");
    }

    private static ulong ComputeFrameOpFrameDataSignature(FrameOp op, int opIndex)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(opIndex);
        switch (op)
        {
            case MeshDrawOp draw:
                AddMatrixSignature(ref hash, draw.Draw.ModelMatrix);
                AddMatrixSignature(ref hash, draw.Draw.PreviousModelMatrix);
                AddMatrixSignature(ref hash, draw.Draw.ViewProjectionMatrix);
                AddMatrixSignature(ref hash, draw.Draw.PreviousViewProjectionMatrix);
                if (draw.Draw.IsStereoPass)
                {
                    AddMatrixSignature(ref hash, draw.Draw.RightEyeViewProjectionMatrix);
                    AddMatrixSignature(ref hash, draw.Draw.PreviousRightEyeViewProjectionMatrix);
                }
                AddVector3Signature(ref hash, draw.Draw.CameraPosition);
                AddVector3Signature(ref hash, draw.Draw.CameraForward);
                AddVector3Signature(ref hash, draw.Draw.CameraUp);
                AddVector3Signature(ref hash, draw.Draw.CameraRight);
                hash.Add(draw.Draw.TransformId);
                hash.Add(draw.Draw.RenderAreaWidth);
                hash.Add(draw.Draw.RenderAreaHeight);
                break;
            case ComputeDispatchOp compute:
                hash.Add(HashUniformBindings(compute.Snapshot.Uniforms));
                break;
            case ClearOp clear:
                hash.Add(clear.Color.R);
                hash.Add(clear.Color.G);
                hash.Add(clear.Color.B);
                hash.Add(clear.Color.A);
                hash.Add(clear.Depth);
                hash.Add(clear.Stencil);
                break;
        }

        return hash.ToHash();
    }

    private static void AddViewportScissorSignature(ref FrameOpSignatureHasher hash, in PendingMeshDraw draw)
    {
        AddViewportSignature(ref hash, draw.Viewport);
        AddRectSignature(ref hash, draw.Scissor);
        hash.Add(draw.ViewportScissorCount);
        if (draw.ViewportScissorCount <= 1 ||
            draw.IndexedViewports is not { } indexedViewports ||
            draw.IndexedScissors is not { } indexedScissors)
        {
            return;
        }

        int indexedCount = (int)Math.Min(
            draw.ViewportScissorCount,
            (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
        hash.Add(indexedCount);
        for (int i = 0; i < indexedCount; i++)
        {
            AddViewportSignature(ref hash, indexedViewports[i]);
            AddRectSignature(ref hash, indexedScissors[i]);
        }
    }

    private static void AddViewportSignature(ref FrameOpSignatureHasher hash, in Viewport viewport)
    {
        hash.Add(viewport.X);
        hash.Add(viewport.Y);
        hash.Add(viewport.Width);
        hash.Add(viewport.Height);
        hash.Add(viewport.MinDepth);
        hash.Add(viewport.MaxDepth);
    }

    private static void AddRectSignature(ref FrameOpSignatureHasher hash, in Rect2D rect)
    {
        hash.Add(rect.Offset.X);
        hash.Add(rect.Offset.Y);
        hash.Add(rect.Extent.Width);
        hash.Add(rect.Extent.Height);
    }

    private static void AddMatrixSignature(ref FrameOpSignatureHasher hash, in Matrix4x4 matrix)
    {
        hash.Add(matrix.M11);
        hash.Add(matrix.M12);
        hash.Add(matrix.M13);
        hash.Add(matrix.M14);
        hash.Add(matrix.M21);
        hash.Add(matrix.M22);
        hash.Add(matrix.M23);
        hash.Add(matrix.M24);
        hash.Add(matrix.M31);
        hash.Add(matrix.M32);
        hash.Add(matrix.M33);
        hash.Add(matrix.M34);
        hash.Add(matrix.M41);
        hash.Add(matrix.M42);
        hash.Add(matrix.M43);
        hash.Add(matrix.M44);
    }

    private static void AddVector3Signature(ref FrameOpSignatureHasher hash, in Vector3 vector)
    {
        hash.Add(vector.X);
        hash.Add(vector.Y);
        hash.Add(vector.Z);
    }

    private static ulong ComputeDispatchSnapshotSignature(ComputeDispatchSnapshot snapshot)
    {
        FrameOpSignatureHasher hash = new();
        HashProgramBindingSnapshot(ref hash, snapshot, includeMutableFrameSourceDescriptors: true);
        return hash.ToHash();
    }

    private static ulong ComputeDispatchSnapshotDescriptorSetSignature(ComputeDispatchSnapshot snapshot)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(1);
        hash.Add(HashUniformBindingLayout(snapshot.Uniforms));
        hash.Add(HashSamplerUnitBindingLayout(snapshot.Samplers, snapshot.SamplerNamesByUnit));
        hash.Add(HashSamplerNameBindingLayout(snapshot.SamplersByName));
        hash.Add(HashImageBindingLayout(snapshot.Images));
        hash.Add(HashBufferBindingLayout(snapshot.Buffers));
        return hash.ToHash();
    }

    private static ulong HashSamplerUnitBindingLayout(Dictionary<uint, XRTexture> samplers, Dictionary<uint, string> samplerNamesByUnit)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, XRTexture> pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(samplerNamesByUnit.TryGetValue(pair.Key, out string? name) ? name : string.Empty);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    private static ulong HashSamplerNameBindingLayout(Dictionary<string, XRTexture> samplers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<string, XRTexture> pair in samplers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(samplers.Count, xor, sum);
    }

    private static ulong HashImageBindingLayout(Dictionary<uint, ProgramImageBinding> images)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, ProgramImageBinding> pair in images)
        {
            ProgramImageBinding binding = pair.Value;
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            item.Add(binding.Level);
            item.Add(binding.Layered);
            item.Add(binding.Layer);
            item.Add((int)binding.Access);
            item.Add((int)binding.Format);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(images.Count, xor, sum);
    }

    private static ulong HashBufferBindingLayout(Dictionary<uint, XRDataBuffer> buffers)
    {
        ulong xor = 0;
        ulong sum = 0;
        foreach (KeyValuePair<uint, XRDataBuffer> pair in buffers)
        {
            FrameOpSignatureHasher item = new();
            item.Add(pair.Key);
            AddUnorderedItemHash(ref xor, ref sum, item.ToHash());
        }

        return FinishUnorderedHash(buffers.Count, xor, sum);
    }

    private static ulong MixSignature(ulong left, ulong right)
    {
        unchecked
        {
            ulong value = left == 0 ? 14695981039346656037UL : left;
            value ^= right;
            value *= 1099511628211UL;
            value ^= right >> 32;
            return value;
        }
    }

    private static int CountDistinctViewKeys(List<RenderPacket> packets)
    {
        HashSet<RenderViewKey> keys = new();
        for (int i = 0; i < packets.Count; i++)
            keys.Add(packets[i].ViewKey);
        return keys.Count;
    }

    private void TraceCommandChainSchedule(
        CommandChainSchedule schedule,
        List<RenderPacket> packets,
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        List<string>? commandChainTraceRows)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _commandChainTraceLastDumpTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1))
            return;

        Interlocked.Exchange(ref _commandChainTraceLastDumpTimestamp, now);
        int dumpIndex = Interlocked.Increment(ref _commandChainTraceDumped);
        if (dumpIndex > 12)
            return;

        StringBuilder builder = new(1024);
        builder.Append("[Vulkan.CommandChains] dump=")
            .Append(dumpIndex)
            .Append(" schedule=0x")
            .Append(schedule.StructuralSignature.ToString("X16"))
            .Append(" groups=")
            .Append(schedule.Groups.Length)
            .Append(" packets=")
            .Append(packets.Count)
            .Append(" staticOps=")
            .Append(staticOps.Length)
            .Append(" volatileOps=")
            .Append(volatileOps.Length)
            .Append(" dirtyRows=")
            .Append(commandChainTraceRows?.Count ?? 0);

        if (commandChainTraceRows is { Count: > 0 })
        {
            int dirtyRowLimit = Math.Min(commandChainTraceRows.Count, 96);
            for (int i = 0; i < dirtyRowLimit; i++)
            {
                builder.AppendLine()
                    .Append("  dirty ")
                    .Append(commandChainTraceRows[i]);
            }

            if (dirtyRowLimit < commandChainTraceRows.Count)
            {
                builder.AppendLine()
                    .Append("  ... ")
                    .Append(commandChainTraceRows.Count - dirtyRowLimit)
                    .Append(" more dirty rows omitted");
            }
        }

        int packetLimit = dumpIndex == 1 ? packets.Count : Math.Min(packets.Count, 32);
        for (int i = 0; i < packetLimit; i++)
        {
            RenderPacket packet = packets[i];
            FrameOp? sourceOp = ResolveCommandChainTraceSourceOp(packet, staticOps, volatileOps);
            builder.AppendLine()
                .Append("  #")
                .Append(i)
                .Append(" pass=")
                .Append(packet.PassIndex)
                .Append(" passName=")
                .Append(sourceOp is null ? "<unknown>" : TryGetPassName(sourceOp) ?? "<unnamed>")
                .Append(" target=")
                .Append(packet.TargetName)
                .Append(" view=")
                .Append(packet.ViewKey.Kind)
                .Append(" op=")
                .Append(sourceOp is null ? "<unknown>" : DescribeCommandChainTraceOp(sourceOp))
                .Append(" draws=")
                .Append(packet.DrawCount)
                .Append(" dispatches=")
                .Append(packet.DispatchCount)
                .Append(" volatility=")
                .Append(packet.Volatility)
                .Append(" structural=0x")
                .Append(packet.StructuralSignature.ToString("X16"))
                .Append(" frame=0x")
                .Append(packet.FrameDataSignature.ToString("X16"));
        }

        if (packetLimit < packets.Count)
        {
            builder.AppendLine()
                .Append("  ... ")
                .Append(packets.Count - packetLimit)
                .Append(" more packets omitted");
        }

        Debug.Vulkan(builder.ToString());
    }

    private static string DescribeCommandChainTraceRow(int packetIndex, RenderPacket packet, CommandChain chain, FrameOp? sourceOp)
    {
        string dirtyDetails = chain.State == CommandChainState.Recorded && chain.DirtyReason != CommandChainDirtyReason.VolatileCommand
            ? " " + DescribeCommandChainDirtyReason(chain, packet)
            : string.Empty;
        string passName = sourceOp is null ? "<unknown>" : TryGetPassName(sourceOp) ?? "<unnamed>";
        string opDescription = sourceOp is null ? "<unknown>" : DescribeCommandChainTraceOp(sourceOp);

        return $"#{packetIndex} state={chain.State} reason={chain.DirtyReason} pass={packet.PassIndex} passName={passName} target={packet.TargetName} view={packet.ViewKey.Kind}:{packet.ViewKey.ViewIndex} op={opDescription} draws={packet.DrawCount} dispatches={packet.DispatchCount} volatility={packet.Volatility}{dirtyDetails}";
    }

    private static FrameOp? ResolveCommandChainTraceSourceOp(RenderPacket packet, FrameOp[] staticOps, FrameOp[] volatileOps)
    {
        FrameOp[] sourceOps = packet.DynamicOverlay ? volatileOps : staticOps;
        int index = packet.SourceStartIndex;
        return index >= 0 && index < sourceOps.Length ? sourceOps[index] : null;
    }

    private static string DescribeCommandChainTraceOp(FrameOp op)
        => op switch
        {
            MeshDrawOp draw => $"MeshDraw[{draw.Draw.Renderer?.MeshRenderer?.Name ?? "<unnamed renderer>"}]",
            ComputeDispatchOp compute => $"ComputeDispatch[{compute.Program?.Data?.Name ?? "<unnamed program>"} {compute.GroupsX}x{compute.GroupsY}x{compute.GroupsZ}]",
            IndirectDrawOp indirect => $"IndirectDraw[count={indirect.DrawCount}]",
            MeshTaskDispatchIndirectCountOp meshTask => $"MeshTaskDispatch[max={meshTask.MaxDrawCount}]",
            BlitOp => "Blit",
            ClearOp => "Clear",
            MemoryBarrierOp barrier => $"MemoryBarrier[{barrier.Mask}]",
            PublishFramebufferForSamplingOp publish => $"PublishFramebufferForSampling[{publish.FrameBuffer?.Name ?? "<unnamed>"}]",
            TransformFeedbackOp => "TransformFeedback",
            DlssUpscaleOp => "DlssUpscale",
            DlssFrameGenerationOp => "DlssFrameGeneration",
            TextureUploadFrameOp => "TextureUpload",
            _ => op.GetType().Name,
        };

    private static void ValidateCommandChainSchedule(CommandChainSchedule schedule, List<RenderPacket> packets, ulong frameOpsSignature)
    {
        if (packets.Count > 0 && schedule.StructuralSignature == 0)
            throw new InvalidOperationException("Command-chain schedule produced a zero structural signature for a non-empty frame.");

        if (frameOpsSignature == 0 && packets.Count > 0)
            throw new InvalidOperationException("Command-chain lowering saw packets but the source frame-op signature was zero.");
    }

    internal static void ValidateCommandChainViewSpecialization(CommandChainSchedule schedule)
    {
        bool sawStereoMultiview = false;
        bool sawSeparateStereoEye = false;
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            int lastEyeIndex = int.MinValue;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                RenderViewKey viewKey = keys[keyIndex].ViewKey;
                if (viewKey.Kind == RenderViewKind.VREye)
                {
                    if (viewKey.ViewIndex != CommandChainLeftEyeViewIndex &&
                        viewKey.ViewIndex != CommandChainRightEyeViewIndex &&
                        viewKey.ViewIndex != CommandChainStereoMultiviewViewIndex)
                    {
                        throw new InvalidOperationException(
                            $"Command-chain VR eye key has invalid view index {viewKey.ViewIndex}.");
                    }

                    if (viewKey.ViewIndex == CommandChainStereoMultiviewViewIndex)
                    {
                        sawStereoMultiview = true;
                        if (sawSeparateStereoEye)
                            throw new InvalidOperationException("Command-chain schedule mixes separate VR eye chains with multiview stereo chains.");
                    }
                    else
                    {
                        sawSeparateStereoEye = true;
                        if (sawStereoMultiview)
                            throw new InvalidOperationException("Command-chain schedule mixes multiview stereo chains with separate VR eye chains.");
                        if (lastEyeIndex > viewKey.ViewIndex)
                            throw new InvalidOperationException("Command-chain VR eye chains must be ordered left eye before right eye.");

                        lastEyeIndex = viewKey.ViewIndex;
                    }
                }
                else if (viewKey.Kind == RenderViewKind.Shadow)
                {
                    if (viewKey.LightIdentity == 0)
                        throw new InvalidOperationException("Command-chain shadow key is missing a light identity.");
                    if (viewKey.CascadeIndex < 0)
                        throw new InvalidOperationException("Command-chain shadow key is missing a cascade or face identity.");
                }
            }
        }
    }

    internal static CommandChainQueueSchedule BuildCommandChainQueueSchedule(
        CommandChainSchedule schedule,
        bool multiQueueRequested,
        bool hasSecondaryGraphicsQueue,
        bool hasAsyncComputeQueue,
        bool hasTransferQueue)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        int[] allGroupIndices = new int[groups.Length];
        CommandChainQueueEligibility aggregateEligibility = CommandChainQueueEligibility.None;
        for (int i = 0; i < groups.Length; i++)
        {
            allGroupIndices[i] = i;
            aggregateEligibility |= IdentifyCommandChainQueueEligibility(groups[i]);
        }

        if (aggregateEligibility == CommandChainQueueEligibility.None)
            aggregateEligibility = CommandChainQueueEligibility.Graphics;

        string diagnostics = multiQueueRequested
            ? "multi-queue requested but disabled until single-queue command-chain measurements justify sidecar submission; using graphics queue fallback"
            : "multi-queue disabled; using graphics queue fallback";
        diagnostics += $"; eligible={aggregateEligibility}; queues secondaryGraphics={hasSecondaryGraphicsQueue} compute={hasAsyncComputeQueue} transfer={hasTransferQueue}";

        CommandChainQueueNode graphicsFallback = new(
            CommandChainQueueKind.Graphics,
            aggregateEligibility | CommandChainQueueEligibility.Graphics,
            allGroupIndices,
            timelineWaitValue: 0,
            timelineSignalValue: 0,
            diagnosticLabel: "CommandChainQueue.GraphicsFallback");

        return new CommandChainQueueSchedule(
            multiQueueEnabled: false,
            singleQueueFallbackAvailable: true,
            nodes: new[] { graphicsFallback },
            dependencies: ReadOnlyMemory<CommandChainQueueDependency>.Empty,
            diagnostics);
    }

    internal static CommandChainQueueEligibility IdentifyCommandChainQueueEligibility(RenderPassChainGroup group)
    {
        CommandChainQueueEligibility eligibility = CommandChainQueueEligibility.Graphics;
        if (!group.DynamicOverlay && group.ChainKeys.Length > 1)
            eligibility |= CommandChainQueueEligibility.SecondaryGraphics;

        string targetName = group.TargetName;
        if (targetName.Contains("Compute", StringComparison.OrdinalIgnoreCase) ||
            targetName.Contains("Cull", StringComparison.OrdinalIgnoreCase) ||
            targetName.Contains("Skin", StringComparison.OrdinalIgnoreCase))
        {
            eligibility |= CommandChainQueueEligibility.Compute;
        }

        if (targetName.Contains("Upload", StringComparison.OrdinalIgnoreCase) ||
            targetName.Contains("Transfer", StringComparison.OrdinalIgnoreCase))
        {
            eligibility |= CommandChainQueueEligibility.Transfer;
        }

        return eligibility;
    }

    internal static void ValidateCommandChainQueueSchedule(CommandChainQueueSchedule schedule)
    {
        if (!schedule.SingleQueueFallbackAvailable)
            throw new InvalidOperationException("Command-chain queue schedule is missing its single-queue fallback.");

        ReadOnlySpan<CommandChainQueueNode> nodes = schedule.Nodes.Span;
        if (nodes.Length == 0)
            throw new InvalidOperationException("Command-chain queue schedule has no queue nodes.");

        if (!schedule.MultiQueueEnabled)
        {
            if (nodes.Length != 1 || nodes[0].QueueKind != CommandChainQueueKind.Graphics)
                throw new InvalidOperationException("Disabled command-chain multi-queue schedule must use one graphics fallback node.");
            return;
        }

        ReadOnlySpan<CommandChainQueueDependency> dependencies = schedule.Dependencies.Span;
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            CommandChainQueueNode node = nodes[nodeIndex];
            if (node.QueueKind == CommandChainQueueKind.Graphics)
                continue;
            if (node.TimelineSignalValue == 0)
                throw new InvalidOperationException("Command-chain sidecar queue node is missing a timeline semaphore signal value.");

            bool hasDependency = false;
            for (int dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
            {
                CommandChainQueueDependency dependency = dependencies[dependencyIndex];
                if (dependency.TimelineSignalValue == 0)
                    throw new InvalidOperationException("Command-chain sidecar dependency is missing a timeline semaphore value.");
                if (dependency.SourceNodeIndex == nodeIndex || dependency.DestinationNodeIndex == nodeIndex)
                    hasDependency = true;
            }

            if (!hasDependency)
                throw new InvalidOperationException("Command-chain sidecar queue node is missing a dependency edge.");
        }
    }
}
