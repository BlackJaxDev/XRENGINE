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
    internal static CommandChainDirtyReason EvaluateCommandChainDirtyReason(CommandChain chain, RenderPacket packet)
    {
        if (chain.StructuralSignature == 0)
            return CommandChainDirtyReason.Structure;

        CommandChainDirtyReason reason = CommandChainDirtyReason.None;
        CommandRecordingDependencyMismatch dependencyMismatch = chain.DependencySignature.Compare(
            BuildCommandChainDependencySignature(packet, chain.Key));
        if (dependencyMismatch.InvalidationClass == CommandRecordingInvalidationClass.Structural)
            reason |= CommandChainDirtyReason.Structure;
        else if (dependencyMismatch.InvalidationClass == CommandRecordingInvalidationClass.BindingIdentity)
            reason |= CommandChainDirtyReason.ResourcePlan;
        if (chain.State == CommandChainState.NotReady)
            reason |= CommandChainDirtyReason.PipelineGeneration;
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

    internal static CommandRecordingDependencySignature BuildCommandChainDependencySignature(
        RenderPacket packet,
        in CommandChainKey key)
    {
        FrameOpSignatureHasher inheritanceHash = new();
        inheritanceHash.Add(key.PassIndex);
        inheritanceHash.Add(key.ViewKey.PipelineIdentity);
        inheritanceHash.Add(key.ViewKey.ViewportIdentity);
        inheritanceHash.Add(key.ViewKey.ViewIndex);
        inheritanceHash.Add((int)key.ViewKey.Kind);

        ulong meshBinding = MixSignature(packet.StructuralSignature, 0x4D455348UL);
        ulong indexBinding = MixSignature(packet.StructuralSignature, 0x494E4458UL);
        ulong vertexBinding = MixSignature(packet.StructuralSignature, 0x56455254UL);
        return new CommandRecordingDependencySignature(
            OutputPassAttachment: packet.ResourcePlanSnapshot.FramebufferSignature,
            RenderArea: 0UL,
            ViewMask: key.ViewKey.Kind == RenderViewKind.VREye ? 0x3u : 0x1u,
            QueueFamily: 0u,
            DynamicRenderingInheritance: inheritanceHash.ToHash(),
            PipelineGeneration: packet.ResourcePlanSnapshot.PipelineGeneration,
            PipelineLayoutGeneration: MixSignature(packet.StructuralSignature, 0x504C4159UL),
            MeshBindingIdentity: meshBinding,
            IndexBufferBindingIdentity: indexBinding,
            VertexBufferBindingIdentity: vertexBinding,
            BufferAllocationGeneration: packet.ResourcePlanSnapshot.Revision,
            ImageAllocationGeneration: packet.ResourcePlanSnapshot.PhysicalImageSignature,
            ImageViewGeneration: packet.ResourcePlanSnapshot.FramebufferSignature,
            // Keep immutable sampler/layout identity separate for precise diagnostics.
            // Descriptor publication is nevertheless a recording dependency because
            // ordinary vkUpdateDescriptorSets calls invalidate command buffers.
            SamplerAllocationGeneration: packet.DescriptorSnapshot.DescriptorSetSignature,
            DescriptorLayoutGeneration: MixSignature(packet.StructuralSignature, 0x444C4159UL),
            DescriptorSetGeneration: packet.DescriptorSnapshot.DescriptorSetSignature,
            ResourcePlanGeneration: packet.ResourcePlanSnapshot.Revision,
            ExternalTargetVariant: unchecked((uint)key.TargetIdentity),
            FrameSlotVariant: key.FrameSlot,
            DescriptorPublicationGeneration: packet.DescriptorSnapshot.DescriptorGeneration,
            DataPublicationGeneration: packet.FrameDataSignature,
            VolatileSuffixGeneration: packet.DynamicOverlay ? packet.FrameDataSignature : 0UL);
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
        // Frame-buffered uniform/storage bytes can change without invalidating a
        // command buffer as long as descriptor publication and binding identity match.
        chain.DependencySignature = BuildCommandChainDependencySignature(packet, chain.Key);
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
        if (recordedProfilerActive != currentProfilerActive ||
            (currentProfilerActive && recordedProfilerFrameSlot != currentProfilerFrameSlot))
        {
            reason |= PrimaryCommandBufferDirtyReason.ProfilerMode;
        }

        return reason;
    }
}

