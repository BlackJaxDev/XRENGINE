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
        int dynamicOverlayOpCount,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains = null)
        => ValidatePrimaryCommandChainSchedule(
            schedule,
            new FrameOperationSequence(staticOps),
            dynamicOverlayOpCount,
            chains);

    internal static void ValidatePrimaryCommandChainSchedule(
        CommandChainSchedule schedule,
        FrameOperationSequence staticOps,
        int dynamicOverlayOpCount,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains = null)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        int groupIndex = 0;
        int queryBracketDepth = 0;
        int currentPassIndex = 0;
        int currentTargetIdentity = 0;
        int currentGroupOpCount = 0;
        for (int opIndex = 0; opIndex < staticOps.Length; opIndex++)
        {
            FrameOp op = staticOps[opIndex];
            if (op is QueryOp queryOp)
            {
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
                continue;
            }

            if (queryBracketDepth != 0)
                continue;

            if (!IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false))
                continue;

            int passIndex = op.PassIndex;
            int targetIdentity = ResolveCommandChainTargetIdentity(op);
            if (currentGroupOpCount == 0)
            {
                currentPassIndex = passIndex;
                currentTargetIdentity = targetIdentity;
                currentGroupOpCount = 1;
                continue;
            }

            if (passIndex != currentPassIndex || targetIdentity != currentTargetIdentity)
            {
                ValidatePrimaryCommandChainStaticGroup(
                    groups,
                    ref groupIndex,
                    currentPassIndex,
                    currentTargetIdentity,
                    currentGroupOpCount,
                    chains);
                currentPassIndex = passIndex;
                currentTargetIdentity = targetIdentity;
                currentGroupOpCount = 1;
                continue;
            }

            currentGroupOpCount++;
        }

        if (currentGroupOpCount != 0)
        {
            ValidatePrimaryCommandChainStaticGroup(
                groups,
                ref groupIndex,
                currentPassIndex,
                currentTargetIdentity,
                currentGroupOpCount,
                chains);
        }

        if (groupIndex < groups.Length)
        {
            RenderPassChainGroup group = groups[groupIndex];
            throw new InvalidOperationException(
                $"Command-chain primary schedule contains an unmatched {(group.DynamicOverlay ? "dynamic overlay" : "static")} group at index {groupIndex}; dynamic overlay frame ops remain outside scheduled command chains ({dynamicOverlayOpCount} inline ops).");
        }
    }

    private static void ValidatePrimaryCommandChainStaticGroup(
        ReadOnlySpan<RenderPassChainGroup> groups,
        ref int groupIndex,
        int passIndex,
        int targetIdentity,
        int groupOpCount,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains)
    {
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
        int scheduledSourceOpCount = CountScheduledSourceOps(group.ChainKeys.Span, chains);
        if (scheduledSourceOpCount != groupOpCount)
        {
            throw new InvalidOperationException(
                $"Command-chain primary schedule group {groupIndex} covers {scheduledSourceOpCount} source ops with {group.ChainKeys.Length} chains for {groupOpCount} static frame ops.");
        }

        groupIndex++;
    }

    private static int CountScheduledSourceOps(
        ReadOnlySpan<CommandChainKey> keys,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? chains)
    {
        if (chains is null)
            return keys.Length;

        int count = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (!chains.TryGetValue(keys[i], out CommandChain? chain) || chain.SourceCount <= 0)
                throw new InvalidOperationException($"Command-chain primary schedule references an unmapped chain '{keys[i]}'.");

            count += chain.SourceCount;
        }

        return count;
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
        if (!packet.RecordedPacketKey.IsComplete)
        {
            AppendDetail(details, "packet-key-complete", bool.FalseString);
            AppendDetail(
                details,
                "packet-key-first-incomplete",
                packet.RecordedPacketKey.DescribeFirstIncompleteField());
            VulkanPreparedCommandChainAuthority? authority = chain.PreparedAuthority;
            AppendDetail(details, "prepared-authority", (authority is not null).ToString());
            AppendDetail(details, "prepared-key-complete", chain.PreparedKey.IsComplete.ToString());
            if (!chain.PreparedKey.RecordedPacketKey.IsComplete)
            {
                AppendDetail(
                    details,
                    "prepared-key-first-incomplete",
                    chain.PreparedKey.RecordedPacketKey.DescribeFirstIncompleteField());
            }
            if (authority is not null)
            {
                RecordedPacketKey authorityRecordedKey =
                    authority.PreparedKey.RecordedPacketKey;
                RecordedPacketKey completedPacketKey = packet.RecordedPacketKey with
                {
                    DescriptorSets = authorityRecordedKey.DescriptorSets,
                    Programs = authorityRecordedKey.Programs,
                };
                AppendDetail(details, "packet-with-authority-complete", completedPacketKey.IsComplete.ToString());
                AppendDetail(details, "authority-recorded-key-complete", authorityRecordedKey.IsComplete.ToString());
                if (completedPacketKey.IsComplete &&
                    authorityRecordedKey.IsComplete &&
                    !completedPacketKey.Matches(in authorityRecordedKey))
                {
                    AppendDetail(
                        details,
                        "authority-mismatch",
                        completedPacketKey.DescribeFirstMismatch(in authorityRecordedKey));
                }
            }
        }
        CommandRecordingDependencyMismatch dependencyMismatch = chain.DependencySignature.Compare(
            BuildCommandChainDependencySignature(packet, chain.Key));
        if (dependencyMismatch.Field != CommandRecordingDependencyField.None)
        {
            AppendDetail(details, "dependency-field", dependencyMismatch.Field.ToString());
            AppendDetail(details, "dependency-class", dependencyMismatch.InvalidationClass.ToString());
            AppendDetail(details, "affected-family", chain.Key.ViewKey.ToString());
            AppendDetail(details, "affected-range", $"{chain.SourceStartIndex}+{chain.SourceCount}");
        }
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
}

