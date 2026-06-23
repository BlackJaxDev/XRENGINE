using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal const string CommandChainsEnvVar = "XRE_VULKAN_COMMAND_CHAINS";
    internal const string CommandChainsSingleThreadEnvVar = "XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD";
    internal const string CommandChainValidateEnvVar = "XRE_VULKAN_COMMAND_CHAIN_VALIDATE";
    internal const string CommandChainTraceEnvVar = "XRE_VULKAN_COMMAND_CHAIN_TRACE";
    internal const string DisableParallelChainRecordingEnvVar = "XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING";
    internal const string ParallelPacketBuildEnvVar = "XRE_VULKAN_PARALLEL_PACKET_BUILD";
    internal const string CommandChainMultiQueueEnvVar = "XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE";
    internal const int CommandChainLeftEyeViewIndex = 0;
    internal const int CommandChainRightEyeViewIndex = 1;
    internal const int CommandChainStereoMultiviewViewIndex = -1;

    private Dictionary<CommandChainKey, CommandChain>[]? _commandChainCaches;
    private CommandChainSchedule?[]? _commandChainScheduleCache;
    private ulong[]? _commandChainScheduleFastSignatures;
    private readonly List<RenderPacket> _commandChainPacketScratch = [];
    private readonly List<RenderPassChainGroup> _commandChainGroupScratch = [];
    private readonly List<CommandChainKey> _commandChainGroupKeyScratch = [];
    private int _commandChainTraceDumped;

    private static bool CommandChainsEnabled => IsCommandChainFlagEnabled(CommandChainsEnvVar);
    private static bool CommandChainsSingleThread => IsCommandChainFlagEnabled(CommandChainsSingleThreadEnvVar);
    private static bool CommandChainValidationEnabled => IsCommandChainFlagEnabled(CommandChainValidateEnvVar);
    private static bool CommandChainTraceEnabled => IsCommandChainFlagEnabled(CommandChainTraceEnvVar);
    private static bool ParallelCommandChainRecordingDisabled => IsCommandChainFlagEnabled(DisableParallelChainRecordingEnvVar);
    private static bool ParallelPacketBuildEnabled => IsCommandChainFlagEnabled(ParallelPacketBuildEnvVar);
    private static bool CommandChainMultiQueueEnabled => IsCommandChainFlagEnabled(CommandChainMultiQueueEnvVar);

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

    private static bool IsCommandChainFlagEnabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
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
        if (!CommandChainsEnabled)
            return null;

        using CommandChainResourcePlanReadScope resourcePlanReadScope = BeginCommandChainResourcePlanReadScope(resourcePlanRevision);
        ulong fastScheduleSignature = ComputeCommandChainFastScheduleSignature(imageIndex, staticOps, volatileOps, resourcePlanRevision);
        if (TryGetCachedCommandChainSchedule(
                imageIndex,
                fastScheduleSignature,
                out CommandChainSchedule? cachedSchedule,
                out stats))
        {
            return cachedSchedule;
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
            return emptySchedule;
        }

        Dictionary<CommandChainKey, CommandChain> cache = GetCommandChainCache(imageIndex);
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

            int chainOrdinal = packet.SourceStartIndex >= 0
                ? packet.SourceStartIndex
                : currentGroupKeys.Count;

            CommandChainKey key = new(
                unchecked((int)Math.Min(imageIndex, int.MaxValue)),
                packet.ViewKey,
                packet.PassIndex,
                packet.TargetIdentity,
                chainOrdinal);

            CommandChain chain = GetOrCreateCommandChain(cache, key);
            CommandChainDirtyReason dirtyReason = EvaluateCommandChainDirtyReason(chain, packet);
            bool canReuse = dirtyReason == CommandChainDirtyReason.None &&
                packet.Volatility is RenderPacketVolatility.StaticStructural or RenderPacketVolatility.FrameDataOnly;
            bool refreshedFrameData = canReuse && TryRefreshReusableCommandChainFrameData(chain, packet);

            if (packet.Volatility == RenderPacketVolatility.DynamicCommand)
            {
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = CommandChainDirtyReason.VolatileCommand;
                chainsRecorded++;
                volatileChainsRecorded++;
            }
            else if (canReuse)
            {
                if (CommandChainValidationEnabled)
                    ValidateReusableCommandChainReferences(chain, packet);

                chain.State = refreshedFrameData ? CommandChainState.FrameDataRefreshed : CommandChainState.Reused;
                chain.DirtyReason = CommandChainDirtyReason.None;
                chainsReused++;
                if (refreshedFrameData)
                    chainsFrameDataRefreshed++;
            }
            else
            {
                chain.State = CommandChainState.Recorded;
                chain.DirtyReason = dirtyReason == CommandChainDirtyReason.None
                    ? CommandChainDirtyReason.Structure
                    : dirtyReason;
                chainsRecorded++;
                firstStructuralDirtyReason ??= DescribeCommandChainDirtyReason(chain, packet);
                if ((chain.DirtyReason & CommandChainDirtyReason.DescriptorGeneration) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0)
                    firstDescriptorMismatch ??= $"chain={key} previous={chain.DescriptorGeneration} current={packet.DescriptorSnapshot.DescriptorGeneration}";
                if ((chain.DirtyReason & CommandChainDirtyReason.ResourcePlan) != 0 &&
                    (chain.DirtyReason & CommandChainDirtyReason.Structure) == 0)
                    firstResourcePlanMismatch ??= $"chain={key} previous={chain.ResourcePlanRevision} current={packet.ResourcePlanSnapshot.Revision}";
            }

            chain.StructuralSignature = packet.StructuralSignature;
            chain.FrameDataSignature = packet.FrameDataSignature;
            chain.ResourcePlanRevision = packet.ResourcePlanSnapshot.Revision;
            chain.PhysicalImageSignature = packet.ResourcePlanSnapshot.PhysicalImageSignature;
            chain.FramebufferSignature = packet.ResourcePlanSnapshot.FramebufferSignature;
            chain.DescriptorGeneration = packet.DescriptorSnapshot.DescriptorGeneration;
            chain.PipelineGeneration = packet.ResourcePlanSnapshot.PipelineGeneration;
            chain.DrawCount = packet.Draws.Length;
            chain.DispatchCount = packet.Dispatches.Length;
            chain.InstanceCountSignature = ComputePacketInstanceCountSignature(packet);
            chain.DescriptorSetCount = packet.DescriptorSnapshot.DescriptorSetCount;
            chain.DescriptorSetSignature = packet.DescriptorSnapshot.DescriptorSetSignature;
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

        if (CommandChainTraceEnabled)
            TraceCommandChainScheduleOnce(schedule, packets, staticOps.Length, volatileOps.Length);

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

    private bool TryGetCachedCommandChainSchedule(
        uint imageIndex,
        ulong fastScheduleSignature,
        out CommandChainSchedule? schedule,
        out CommandChainLoweringStats stats)
    {
        schedule = null;
        stats = default;
        if (CommandChainValidationEnabled || CommandChainTraceEnabled)
            return false;

        int slot = unchecked((int)Math.Min(imageIndex, int.MaxValue));
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

        EnsureCommandChainScheduleCache();
        int slot = unchecked((int)Math.Min(imageIndex, (uint)(_commandChainScheduleCache!.Length - 1)));
        _commandChainScheduleCache[slot] = schedule;
        _commandChainScheduleFastSignatures![slot] = fastScheduleSignature;
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
        DrawPacket[] draws = op switch
        {
            MeshDrawOp draw => [CreateDrawPacket(opIndex, draw)],
            IndirectDrawOp indirect => [CreateIndirectDrawPacket(opIndex, indirect)],
            MeshTaskDispatchIndirectCountOp meshTask => [CreateMeshTaskDrawPacket(opIndex, meshTask)],
            _ => []
        };
        DispatchPacket[] dispatches = op is ComputeDispatchOp compute
            ? [CreateDispatchPacket(opIndex, compute)]
            : [];

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
            draws,
            dispatches,
            descriptorSnapshot,
            resourceSnapshot,
            structuralSignature,
            frameDataSignature,
            opIndex,
            1,
            dynamicOverlay);
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
            expected.Draws.Length != actual.Draws.Length ||
            expected.Dispatches.Length != actual.Dispatches.Length)
        {
            throw new InvalidOperationException($"Parallel command-chain packet build mismatch at packet {index}.");
        }
    }

    private Dictionary<CommandChainKey, CommandChain> GetCommandChainCache(uint imageIndex)
    {
        if (_commandChainCaches is null || _commandBuffers is null || _commandChainCaches.Length != _commandBuffers.Length)
        {
            int count = Math.Max(_commandBuffers?.Length ?? 0, 1);
            _commandChainCaches = new Dictionary<CommandChainKey, CommandChain>[count];
            for (int i = 0; i < count; i++)
                _commandChainCaches[i] = new Dictionary<CommandChainKey, CommandChain>();
        }

        int index = unchecked((int)Math.Min(imageIndex, (uint)(_commandChainCaches.Length - 1)));
        return _commandChainCaches[index];
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

    internal static CommandChainDirtyReason EvaluateCommandChainDirtyReason(CommandChain chain, RenderPacket packet)
    {
        if (chain.StructuralSignature == 0)
            return CommandChainDirtyReason.Structure;

        CommandChainDirtyReason reason = CommandChainDirtyReason.None;
        if (chain.StructuralSignature != packet.StructuralSignature)
            reason |= CommandChainDirtyReason.Structure;
        if (chain.DrawCount != packet.Draws.Length ||
            chain.DispatchCount != packet.Dispatches.Length ||
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

        if (EvaluateCommandChainDirtyReason(chain, packet) != CommandChainDirtyReason.None)
            return false;

        chain.FrameDataSignature = packet.FrameDataSignature;
        return true;
    }

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
                    hash.Add(chain.SecondaryCommandBuffer.Handle);
                else
                    hash.Add(0UL);
            }
        }

        return hash.ToHash();
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
        ReadOnlySpan<DrawPacket> draws = packet.Draws.Span;
        for (int i = 0; i < draws.Length; i++)
            hash.Add(draws[i].InstanceCount);

        return hash.ToHash();
    }

    private static string DescribeCommandChainDirtyReason(CommandChain chain, RenderPacket packet)
    {
        ulong currentInstanceCountSignature = ComputePacketInstanceCountSignature(packet);
        StringBuilder details = new();
        AppendIfChanged(details, "draw-count", chain.DrawCount, packet.Draws.Length);
        AppendIfChanged(details, "dispatch-count", chain.DispatchCount, packet.Dispatches.Length);
        AppendIfChanged(details, "instance-counts", chain.InstanceCountSignature, currentInstanceCountSignature);
        AppendIfChanged(details, "descriptor-set-count", chain.DescriptorSetCount, packet.DescriptorSnapshot.DescriptorSetCount);
        AppendIfChanged(details, "descriptor-set-signature", chain.DescriptorSetSignature, packet.DescriptorSnapshot.DescriptorSetSignature);
        AppendIfChanged(details, "descriptor-generation", chain.DescriptorGeneration, packet.DescriptorSnapshot.DescriptorGeneration);
        AppendIfChanged(details, "resource-plan-revision", chain.ResourcePlanRevision, packet.ResourcePlanSnapshot.Revision);
        AppendIfChanged(details, "physical-image-signature", chain.PhysicalImageSignature, packet.ResourcePlanSnapshot.PhysicalImageSignature);
        AppendIfChanged(details, "framebuffer-signature", chain.FramebufferSignature, packet.ResourcePlanSnapshot.FramebufferSignature);
        AppendIfChanged(details, "pipeline-generation", chain.PipelineGeneration, packet.ResourcePlanSnapshot.PipelineGeneration);

        string detailText = details.Length == 0 ? string.Empty : $" details=[{details}]";
        return $"key={chain.Key} reason={chain.DirtyReason} previousSig=0x{chain.StructuralSignature:X16} currentSig=0x{packet.StructuralSignature:X16} volatility={packet.Volatility}{detailText}";

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
            ComputeDispatchOp => RenderPacketVolatility.DynamicCommand,
            MemoryBarrierOp => RenderPacketVolatility.StaticStructural,
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
        bool? cameraEyeLeft = draw.Camera?.StereoEyeLeft;
        if (cameraEyeLeft.HasValue)
            return cameraEyeLeft.Value ? CommandChainLeftEyeViewIndex : CommandChainRightEyeViewIndex;

        if (draw.IsStereoPass)
            return CommandChainStereoMultiviewViewIndex;

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

    private static int ResolveCommandChainTargetIdentity(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.GetHashCode() ?? 0,
            _ => op.Target?.GetHashCode() ?? 0,
        };

    private static string ResolveCommandChainTargetName(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.Name ?? "<swapchain>",
            _ => op.Target?.Name ?? "<swapchain>",
        };

    private static ulong ResolvePipelineGeneration(FrameOp op)
        => unchecked((ulong)op.Context.PipelineIdentity);

    private static DescriptorBindingSnapshot CreateDescriptorSnapshot(FrameOp op)
    {
        ulong signature = op switch
        {
            MeshDrawOp draw => draw.Draw.ProgramBindingSnapshot is null ? 0UL : ComputeDispatchSnapshotSignature(draw.Draw.ProgramBindingSnapshot),
            ComputeDispatchOp compute => ComputeDispatchSnapshotSignature(compute.Snapshot),
            IndirectDrawOp indirect => unchecked((ulong)(indirect.BindlessMaterialTextures?.Program.GetHashCode() ?? 0)),
            MeshTaskDispatchIndirectCountOp meshTask => unchecked((ulong)(meshTask.BindlessMaterialTextures?.Program.GetHashCode() ?? 0)),
            _ => 0UL,
        };

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
            op.IndirectBuffer.GetHashCode(),
            op.ParameterBuffer?.GetHashCode() ?? 0,
            op.BindlessMaterialTextures?.Program.GetHashCode() ?? 0,
            op.DrawCount,
            false,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static DrawPacket CreateMeshTaskDrawPacket(int opIndex, MeshTaskDispatchIndirectCountOp op)
        => new(
            opIndex,
            op.IndirectBuffer.GetHashCode(),
            op.CountBuffer.GetHashCode(),
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
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.DynamicCommand),
            ComputeFrameOpFrameDataSignature(op, opIndex));

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
                hash.Add((int)draw.Draw.CullMode);
                hash.Add((int)draw.Draw.FrontFace);
                hash.Add(draw.Draw.DepthTestEnabled);
                hash.Add(draw.Draw.DepthWriteEnabled);
                hash.Add((int)draw.Draw.DepthCompareOp);
                hash.Add(draw.Draw.ViewportScissorCount);
                hash.Add(draw.Draw.ProgramBindingSnapshot is null ? 0UL : ComputeDispatchSnapshotSignature(draw.Draw.ProgramBindingSnapshot));
                hash.Add(ComputeShadowCommandChainStructuralSignature(draw.Draw.ShadowUniformState));
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
            case IndirectDrawOp indirect:
                hash.Add(indirect.IndirectBuffer.GetHashCode());
                hash.Add(indirect.ParameterBuffer?.GetHashCode() ?? 0);
                hash.Add(indirect.DrawCount);
                hash.Add(indirect.Stride);
                hash.Add(indirect.UseCount);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                hash.Add(meshTask.IndirectBuffer.GetHashCode());
                hash.Add(meshTask.CountBuffer.GetHashCode());
                hash.Add(meshTask.MaxDrawCount);
                hash.Add(meshTask.Stride);
                break;
            case ComputeDispatchOp compute:
                hash.Add(compute.Program.GetHashCode());
                hash.Add(compute.GroupsX);
                hash.Add(compute.GroupsY);
                hash.Add(compute.GroupsZ);
                hash.Add(ComputeDispatchSnapshotSignature(compute.Snapshot));
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
                hash.Add(draw.Draw.ModelMatrix.M11);
                hash.Add(draw.Draw.ModelMatrix.M22);
                hash.Add(draw.Draw.ModelMatrix.M33);
                hash.Add(draw.Draw.ModelMatrix.M41);
                hash.Add(draw.Draw.ModelMatrix.M42);
                hash.Add(draw.Draw.ModelMatrix.M43);
                hash.Add(draw.Draw.CameraPosition.X);
                hash.Add(draw.Draw.CameraPosition.Y);
                hash.Add(draw.Draw.CameraPosition.Z);
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

    private static ulong ComputeDispatchSnapshotSignature(ComputeDispatchSnapshot snapshot)
    {
        FrameOpSignatureHasher hash = new();
        HashProgramBindingSnapshot(ref hash, snapshot);
        return hash.ToHash();
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

    private void TraceCommandChainScheduleOnce(CommandChainSchedule schedule, List<RenderPacket> packets, int staticOpCount, int volatileOpCount)
    {
        if (Interlocked.Exchange(ref _commandChainTraceDumped, 1) != 0)
            return;

        StringBuilder builder = new(1024);
        builder.Append("[Vulkan.CommandChains] schedule=0x")
            .Append(schedule.StructuralSignature.ToString("X16"))
            .Append(" groups=")
            .Append(schedule.Groups.Length)
            .Append(" packets=")
            .Append(packets.Count)
            .Append(" staticOps=")
            .Append(staticOpCount)
            .Append(" volatileOps=")
            .Append(volatileOpCount);

        int packetLimit = Math.Min(packets.Count, 24);
        for (int i = 0; i < packetLimit; i++)
        {
            RenderPacket packet = packets[i];
            builder.AppendLine()
                .Append("  #")
                .Append(i)
                .Append(" pass=")
                .Append(packet.PassIndex)
                .Append(" target=")
                .Append(packet.TargetName)
                .Append(" view=")
                .Append(packet.ViewKey.Kind)
                .Append(" draws=")
                .Append(packet.Draws.Length)
                .Append(" dispatches=")
                .Append(packet.Dispatches.Length)
                .Append(" volatility=")
                .Append(packet.Volatility)
                .Append(" structural=0x")
                .Append(packet.StructuralSignature.ToString("X16"))
                .Append(" frame=0x")
                .Append(packet.FrameDataSignature.ToString("X16"));
        }

        Debug.Vulkan(builder.ToString());
    }

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
