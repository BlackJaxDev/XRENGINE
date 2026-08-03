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

