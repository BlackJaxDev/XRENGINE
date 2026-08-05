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
    private CommandChainSchedule RentCommandChainSchedule(uint imageIndex)
    {
        if (!CommandChainValidationEnabled &&
            !CommandChainTraceEnabled &&
            TryGetIndexedCommandChainCacheSlot(imageIndex, out int slot))
        {
            CommandChainSchedule? schedule = _commandScheduler.GetReusableSchedule(
                slot,
                _commandBuffers?.Length ?? 0);
            if (schedule is not null)
                return schedule;
        }

        return new CommandChainSchedule();
    }

    private bool ShouldBypassCommandChainScheduleForStabilityGuard(
        uint imageIndex,
        ulong resourcePlanRevision,
        out CommandChainStabilityBypassReason reason)
    {
        reason = CommandChainStabilityBypassReason.None;
        if (!CommandChainStabilityGuardEnabled)
            return false;

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
            // A new resource plan invalidates the old stability history, but it does
            // not make the new schedule unstable. Build the replacement command-chain
            // schedule immediately. The old one-frame bypass recorded a complete
            // inline primary; camera motion then made that primary stale before its
            // next use and produced a recurring full-frame re-record spike.
            state.ResourcePlanRevision = resourcePlanRevision;
            state.StableObservations = 1;
            state.ScheduledAttemptsForRevision = 0;
            state.ConsecutiveRecordedWithoutReuse = 0;
            state.ConsecutiveBypasses = 0;
            _commandChainStabilityGuardStates[imageIndex] = state;
        }

        state.StableObservations++;
        // Zero reuse means this schedule changed; it is not a correctness failure.
        // The scheduler can still record changed chains while reusing any compatible
        // ones. Falling back to a complete inline primary here turned one camera-
        // motion miss into as many as 119 consecutive 130-220 ms frames.
        state.ConsecutiveBypasses = 0;

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
        if (stats.ChainsRecorded > stats.ChainsReused + stats.ChainsFrameDataRefreshed &&
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

    private void LogCommandChainStabilityGuardBypass(
        uint imageIndex,
        ulong resourcePlanRevision,
        int opCount,
        CommandChainStabilityBypassReason reason)
    {
        if (!_commandChainStabilityGuardStates.TryGetValue(imageIndex, out CommandChainStabilityGuardState state))
            state = default;

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
        if (!CanReuseCachedCommandChainSchedule(
                CommandChainBenchmarkForceRerecord,
                CommandChainValidationEnabled,
                CommandChainTraceEnabled,
                IsRenderingExternalSwapchainTarget))
            return false;

        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int slot))
            return false;

        if (!_commandScheduler.TryGetCachedSchedule(slot, fastScheduleSignature, out schedule))
            return false;

        CommandChainSchedule cachedSchedule = schedule!;
        int chainCount = CountCommandChains(cachedSchedule);
        stats = new CommandChainLoweringStats(
            VisibilityPackets: cachedSchedule.Groups.Length,
            RenderPackets: chainCount,
            ChainsScheduled: chainCount,
            ChainsRecorded: 0,
            ChainsReused: chainCount,
            ChainsFrameDataRefreshed: 0,
            VolatileChainsRecorded: 0,
            SecondaryCommandBuffers: chainCount,
            FirstStructuralDirtyReason: null,
            FirstDescriptorGenerationMismatch: null,
            FirstResourcePlanRevisionMismatch: null);
        return true;
    }

    private void CacheCommandChainSchedule(
        uint imageIndex,
        ulong fastScheduleSignature,
        CommandChainSchedule schedule)
    {
        if (CommandChainValidationEnabled || CommandChainTraceEnabled)
            return;

        if (!TryGetIndexedCommandChainCacheSlot(imageIndex, out int slot))
            return;

        _commandScheduler.CacheSchedule(
            slot,
            _commandBuffers?.Length ?? 0,
            fastScheduleSignature,
            schedule);
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
        AddCommandChainFastScheduleSignatureParts(
            ref hash,
            staticOps,
            dynamicOverlay: false);
        AddCommandChainFastScheduleSignatureParts(
            ref hash,
            volatileOps,
            dynamicOverlay: true);
        return hash.ToHash();
    }

    private static void AddCommandChainFastScheduleSignatureParts(
        ref FrameOpSignatureHasher hash,
        FrameOp[] ops,
        bool dynamicOverlay)
    {
        hash.Add(ops.Length);
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            RenderViewKey viewKey = BuildRenderViewKey(op, dynamicOverlay);
            RenderPacketVolatility volatility =
                ClassifyRenderPacketVolatility(op, dynamicOverlay);
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
            DescriptorBindingSnapshot descriptorSnapshot =
                CreateDescriptorSnapshot(op);
            hash.Add(descriptorSnapshot.DescriptorGeneration);
            hash.Add(descriptorSnapshot.DescriptorSetSignature);
            hash.Add(descriptorSnapshot.DescriptorSetCount);
        }
    }
}
