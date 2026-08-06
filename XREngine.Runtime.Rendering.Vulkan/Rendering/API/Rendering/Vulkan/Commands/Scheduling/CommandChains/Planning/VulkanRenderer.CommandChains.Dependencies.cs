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
        RecordedPacketKey currentRecordedKey = packet.RecordedPacketKey;
        if (!currentRecordedKey.IsComplete)
        {
            VulkanPreparedCommandChainAuthority? authority = chain.PreparedAuthority;
            if (authority is null)
                return CommandChainDirtyReason.ResourcePlan;

            currentRecordedKey = currentRecordedKey with
            {
                DescriptorSets = authority.PreparedKey.RecordedPacketKey.DescriptorSets,
            };
            if (!currentRecordedKey.IsComplete ||
                currentRecordedKey != authority.PreparedKey.RecordedPacketKey)
                return CommandChainDirtyReason.ResourcePlan;
        }

        if (chain.StructuralSignature == 0)
            return CommandChainDirtyReason.Structure;

        CommandChainDirtyReason reason = CommandChainDirtyReason.None;
        CommandRecordingDependencyMismatch dependencyMismatch = chain.DependencySignature.Compare(
            BuildCommandChainDependencySignature(packet, chain.Key, currentRecordedKey));
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
        => BuildCommandChainDependencySignature(packet, key, packet.RecordedPacketKey);

    private static CommandRecordingDependencySignature BuildCurrentCommandChainDependencySignature(
        RenderPacket packet,
        CommandChain chain)
    {
        RecordedPacketKey recordedKey = packet.RecordedPacketKey;
        if (!recordedKey.IsComplete && chain.PreparedAuthority is { } authority)
        {
            RecordedPacketKey prepared = recordedKey with
            {
                DescriptorSets = authority.PreparedKey.RecordedPacketKey.DescriptorSets,
            };
            if (prepared.IsComplete && prepared == authority.PreparedKey.RecordedPacketKey)
                recordedKey = prepared;
        }

        return BuildCommandChainDependencySignature(packet, chain.Key, recordedKey);
    }

    private static CommandRecordingDependencySignature BuildCommandChainDependencySignature(
        RenderPacket packet,
        in CommandChainKey key,
        in RecordedPacketKey recordedKey)
    {
        FrameOpSignatureHasher inheritanceHash = new();
        inheritanceHash.Add(key.PassIndex);
        inheritanceHash.Add(key.ViewKey.PipelineIdentity);
        inheritanceHash.Add(key.ViewKey.ViewportIdentity);
        inheritanceHash.Add(key.ViewKey.ViewIndex);
        inheritanceHash.Add((int)key.ViewKey.Kind);

        VulkanNativeAttachmentIdentity firstAttachment =
            recordedKey.RenderTarget.AttachmentCount > 0
                ? recordedKey.RenderTarget.GetAttachment(0)
                : default;
        VulkanRecordedBufferIdentity firstVertexBuffer =
            recordedKey.VertexBuffers.Count > 0
                ? recordedKey.VertexBuffers.Get(0)
                : default;
        VulkanRecordedDescriptorSetIdentity firstDescriptorSet =
            recordedKey.DescriptorSets.Count > 0
                ? recordedKey.DescriptorSets.Get(0)
                : default;
        ulong samplerGeneration = 0UL;
        for (int i = 0; i < firstDescriptorSet.Resources.Count; i++)
        {
            VulkanRecordedDescriptorResourceIdentity resource = firstDescriptorSet.Resources.Get(i);
            if (resource.Type == ObjectType.Sampler)
            {
                samplerGeneration = resource.Generation;
                break;
            }
        }
        return new CommandRecordingDependencySignature(
            OutputPassAttachment: recordedKey.RenderTarget.FramebufferHandle,
            RenderArea: recordedKey.RenderArea,
            ViewMask: recordedKey.RenderTarget.ViewMask,
            QueueFamily: recordedKey.QueueFamily,
            DynamicRenderingInheritance: inheritanceHash.ToHash(),
            PipelineGeneration: recordedKey.PipelineGeneration,
            PipelineLayoutGeneration: recordedKey.PipelineLayoutGeneration,
            MeshBindingIdentity: firstVertexBuffer.BufferHandle,
            IndexBufferBindingIdentity: recordedKey.IndexBuffer.BufferHandle,
            VertexBufferBindingIdentity: firstVertexBuffer.BufferHandle,
            BufferAllocationGeneration: recordedKey.IndexBuffer.AllocationGeneration,
            ImageAllocationGeneration: firstAttachment.ImageGeneration,
            ImageViewGeneration: firstAttachment.ImageViewGeneration,
            // Keep immutable sampler/layout identity separate for precise diagnostics.
            // Descriptor publication is nevertheless a recording dependency because
            // ordinary vkUpdateDescriptorSets calls invalidate command buffers.
            SamplerAllocationGeneration: samplerGeneration,
            DescriptorLayoutGeneration: recordedKey.PipelineLayoutGeneration,
            DescriptorSetGeneration: firstDescriptorSet.PayloadGeneration,
            ResourcePlanGeneration: packet.ResourcePlanSnapshot.Revision,
            ExternalTargetVariant: unchecked((uint)key.TargetIdentity),
            FrameSlotVariant: key.FrameSlot,
            DescriptorPublicationGeneration: firstDescriptorSet.PublicationGeneration,
            DataPublicationGeneration: packet.FrameDataSignature,
            VolatileSuffixGeneration: packet.DynamicOverlay ? packet.FrameDataSignature : 0UL,
            RenderTargetSnapshot: recordedKey.RenderTarget,
            RecordedPacketKey: recordedKey);
    }

    /// <summary>
    /// Publishes the exact post-binding packet key only after the corresponding
    /// secondary has been recorded successfully (or an existing artifact has
    /// matched it exactly). The sealed pre-binding packet remains immutable.
    /// </summary>
    private static void PublishPreparedCommandChainAuthority(
        CommandChain chain,
        VulkanPreparedCommandChainAuthority authority)
    {
        RenderPacket packet = chain.PacketSnapshot ??
            throw new InvalidOperationException("A prepared command chain has no sealed packet snapshot.");
        VulkanPreparedCommandChainKey preparedKey = authority.PreparedKey;
        RecordedPacketKey expected = packet.RecordedPacketKey with
        {
            DescriptorSets = preparedKey.RecordedPacketKey.DescriptorSets,
        };
        if (!expected.IsComplete || expected != preparedKey.RecordedPacketKey)
            throw new InvalidOperationException("Prepared command-chain authority does not match its sealed packet snapshot.");

        chain.PreparedKey = preparedKey;
        chain.PreparedAuthority = authority;
        chain.DependencySignature = BuildCommandChainDependencySignature(
            packet,
            chain.Key,
            preparedKey.RecordedPacketKey);
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
        chain.DependencySignature = BuildCurrentCommandChainDependencySignature(packet, chain);
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

