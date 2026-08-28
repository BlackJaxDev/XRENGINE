using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable ordered primary-command plan compiled before native recording.
/// </summary>
internal sealed class VulkanPrimaryCommandPlan
{
    private int _isFrozen;

    internal bool IsFrozen => Volatile.Read(ref _isFrozen) != 0;
    /// <summary>
    /// The array of plan nodes, which may be larger than the actual count of nodes in use.
    /// A node is a typed stream projection, including its resolved kind, actions, and source index.
    /// </summary>
    private VulkanPrimaryPlanNode[] _nodes = new VulkanPrimaryPlanNode[64];

    /// <summary>
    /// The number of nodes in the plan that are currently in use.
    /// This may be less than the length of the _nodes array.
    /// </summary>
    internal int Count { get; private set; }
    /// <summary>
    /// The number of FrameOps that were used to build the plan.
    /// </summary>
    internal int OperationCount { get; private set; }
    /// <summary>
    /// The authoritative identity of the plan, computed from the FrameOps and their resolved kinds and actions.
    /// This is basically a hash of the plan's structure and content, and is used for caching and comparison purposes.
    /// </summary>
    internal ulong Identity { get; private set; }
    internal void Build(
        FrameOperationStream operations,
        ulong operationSignature = 0,
        VulkanPrimaryPlanTerminalContext terminalContext = default,
        VulkanBarrierPlanner? barrierPlanner = null,
        VulkanBarrierPlan? barrierPlan = null,
        FramePlan? framePlan = null)
    {
        Volatile.Write(ref _isFrozen, 0);
        int terminalNodeCount = 1 +
            (terminalContext.RequiresPreparePresent ? 1 : 0) +
            (terminalContext.ReleaseExternalImageOwnership ? 1 : 0);
        int nodeCount = operations.Count + terminalNodeCount;
        EnsureCapacity(nodeCount);

        FrameOpSignatureHasher identity = new();
        identity.Add(nodeCount);
        identity.Add(operations.Count);
        identity.Add(operationSignature);

        for (int opIndex = 0; opIndex < operations.Count; opIndex++)
        {
            ref readonly FrameOperationHeader header = ref operations.GetHeader(opIndex);
            EVulkanPrimaryPlanNodeKind kind = header.OpCode;
            if (kind == EVulkanPrimaryPlanNodeKind.Unsupported)
                throw new InvalidOperationException("Frame operation stream contains an unsupported opcode.");

            VulkanBarrierPlan? operationBarrierPlan = ResolveOperationBarrierPlan(
                operations.GetContext(opIndex), header.PassIndex, barrierPlan, framePlan);
            EVulkanPrimaryPlanAction actions = ResolveActions(
                operations, opIndex, barrierPlanner, operationBarrierPlan);
            bool isDrawLike = kind is EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount;
            _nodes[opIndex] = new VulkanPrimaryPlanNode(kind, opIndex, opIndex, actions, isDrawLike);
            ref readonly FrameOpContext context = ref operations.GetContext(opIndex);
            AddEmission(ref identity, kind, actions, opIndex, header.PassIndex,
                context.PipelineIdentity, context.ViewportIdentity,
                context.SchedulingIdentity, header.TargetIdentity);
        }

        int nodeIndex = operations.Count;
        AddTerminalNode(ref nodeIndex, EVulkanPrimaryPlanNodeKind.EndRendering,
            EVulkanPrimaryPlanAction.EndRendering, ref identity);
        if (terminalContext.RequiresPreparePresent)
            AddTerminalNode(ref nodeIndex, EVulkanPrimaryPlanNodeKind.PreparePresent,
                EVulkanPrimaryPlanAction.PreparePresent, ref identity);
        if (terminalContext.ReleaseExternalImageOwnership)
            AddTerminalNode(ref nodeIndex, EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership,
                EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership, ref identity);

        if (Count > nodeCount) Array.Clear(_nodes, nodeCount, Count - nodeCount);
        Count = nodeCount;
        OperationCount = operations.Count;
        Identity = identity.ToHash();
        Volatile.Write(ref _isFrozen, 1);
    }

    /// <summary>
    /// Adds an emission to the identity hasher, including the kind, actions, index, and stream metadata for an operation or terminal node.
    /// </summary>
    /// <param name="identity">The frame operation signature hasher to add the emission to.</param>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="actions">The actions associated with the primary plan node.</param>
    /// <param name="index">The index of the primary plan node.</param>
    /// <param name="passIndex">The pass index from the operation header.</param>
    /// <param name="pipelineIdentity">The pipeline identity from the operation context.</param>
    /// <param name="viewportIdentity">The viewport identity from the operation context.</param>
    /// <param name="schedulingIdentity">The scheduling identity from the operation context.</param>
    /// <param name="targetHashCode">The hash code of the target object, if any.</param>
    private static void AddEmission(
        ref FrameOpSignatureHasher identity,
        EVulkanPrimaryPlanNodeKind kind,
        EVulkanPrimaryPlanAction actions,
        int index,
        int passIndex,
        int pipelineIdentity,
        int viewportIdentity,
        int schedulingIdentity,
        int targetHashCode)
    {
        identity.Add((byte)kind);
        identity.Add((byte)actions);
        identity.Add(index);

        identity.Add(passIndex);
        identity.Add(pipelineIdentity);
        identity.Add(viewportIdentity);
        identity.Add(schedulingIdentity);
        identity.Add(targetHashCode);
    }

    /// <summary>
    /// Adds a terminal node's emission to the identity hasher, including its kind, action, and index.
    /// A terminal node is a special node that represents the end of rendering or other finalization actions in the command plan.
    /// </summary>
    /// <param name="identity">The frame operation signature hasher to add the terminal emission to.</param>
    /// <param name="kind">The kind of the terminal node.</param>
    /// <param name="action">The action of the terminal node.</param>
    /// <param name="nodeIndex">The index of the terminal node.</param>
    private static void AddTerminalEmission(
        ref FrameOpSignatureHasher identity,
        EVulkanPrimaryPlanNodeKind kind,
        EVulkanPrimaryPlanAction action,
        int nodeIndex)
        => AddEmission(
            ref identity,
            kind,
            action,
            nodeIndex,
            passIndex: -1,
            pipelineIdentity: 0,
            viewportIdentity: 0,
            schedulingIdentity: 0,
            targetHashCode: 0);

    private static VulkanBarrierPlan? ResolveOperationBarrierPlan(
        in FrameOpContext context,
        int passIndex,
        VulkanBarrierPlan? fallbackPlan,
        FramePlan? framePlan)
    {
        if (framePlan is null)
            return fallbackPlan;
        if (framePlan.TryResolveRenderGraphPlan(
                in context,
                out VulkanRenderGraphPlan renderGraphPlan))
        {
            return renderGraphPlan.Barriers;
        }
        if (context.ResourceRegistry is null &&
            context.PassMetadata is not { Count: > 0 })
        {
            return fallbackPlan;
        }

        throw new VulkanPlanPreconditionException(
            $"Primary command plan has no frozen render-graph publication for " +
            $"kind={context.ContextKind} pipe={context.PipelineIdentity} " +
            $"viewport={context.ViewportIdentity} pass={passIndex}.");
    }

    /// <summary>
    /// Determines whether any of the plan nodes from OperationCount to Count have the specified terminal action.
    /// </summary>
    /// <param name="action">The terminal action to check for.</param>
    /// <returns>True if any of the plan nodes from OperationCount to Count have the specified terminal action; otherwise, false.</returns>
    internal bool HasTerminalAction(EVulkanPrimaryPlanAction action)
    {
        for (int index = OperationCount; index < Count; index++)
            if ((_nodes[index].Actions & action) != 0)
                return true;

        return false;
    }

    internal ref readonly VulkanPrimaryPlanNode GetNode(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return ref _nodes[index];
    }

    private void EnsureCapacity(int required)
    {
        if (_nodes.Length >= required)
            return;

        int capacity = Math.Max(required, _nodes.Length * 2);
        Array.Resize(ref _nodes, capacity);
    }

    /// <summary>
    /// Resolves the actions for a stream operation based on its header and barrier planning.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="operationIndex">The stream operation index for which to resolve actions.</param>
    /// <param name="barrierPlanner">Optional barrier planner for queue ownership transfers.</param>
    /// <returns>The resolved actions for the given stream operation.</returns>
    private static EVulkanPrimaryPlanAction ResolveActions(
        FrameOperationStream operations,
        int operationIndex,
        VulkanBarrierPlanner? barrierPlanner,
        VulkanBarrierPlan? barrierPlan)
    {
        ref readonly FrameOperationHeader header = ref operations.GetHeader(operationIndex);
        EVulkanPrimaryPlanNodeKind kind = header.OpCode;
        EVulkanPrimaryPlanAction actions = EVulkanPrimaryPlanAction.RecordOperation;
        if (kind != EVulkanPrimaryPlanNodeKind.TextureUpload)
            actions |= EVulkanPrimaryPlanAction.BarrierBatch;
        if ((barrierPlanner is not null && HasQueueOwnershipTransfer(barrierPlanner, header.PassIndex)) ||
            (barrierPlan is not null && HasQueueOwnershipTransfer(barrierPlan, header.PassIndex)))
            actions |= EVulkanPrimaryPlanAction.QueueOwnershipTransfer;
        if (RequiresRenderingScope(operations, operationIndex))
            actions |= EVulkanPrimaryPlanAction.BeginRendering;
        if (SupportsSecondaryRange(kind))
            actions |= EVulkanPrimaryPlanAction.ExecuteSecondaryRange;
        if (EndsRenderScope(operations, operationIndex))
            actions |= EVulkanPrimaryPlanAction.EndRendering;
        return actions;
    }

    private static bool RequiresRenderingScope(FrameOperationStream operations, int operationIndex)
    {
        EVulkanPrimaryPlanNodeKind kind = operations.GetHeader(operationIndex).OpCode;
        return kind is EVulkanPrimaryPlanNodeKind.Clear or
            EVulkanPrimaryPlanNodeKind.TransformFeedback or
            EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount ||
            kind == EVulkanPrimaryPlanNodeKind.AdvancedVisibility &&
            operations.GetAdvancedVisibility(operationIndex).Request.Stage ==
                EAdvancedRenderStage.VisibilityRaster ||
            kind == EVulkanPrimaryPlanNodeKind.Query &&
            operations.GetQuery(operationIndex).Operation is ERenderQueryOperation.Begin or ERenderQueryOperation.End;
    }

    private static bool EndsRenderScope(FrameOperationStream operations, int operationIndex)
    {
        EVulkanPrimaryPlanNodeKind kind = operations.GetHeader(operationIndex).OpCode;
        return kind is EVulkanPrimaryPlanNodeKind.TextureUpload or
            EVulkanPrimaryPlanNodeKind.Blit or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.ComputeDispatch or
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
            EVulkanPrimaryPlanNodeKind.BufferCopy or
            EVulkanPrimaryPlanNodeKind.SubmissionMarker or
            EVulkanPrimaryPlanNodeKind.MemoryBarrier or
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling or
            EVulkanPrimaryPlanNodeKind.DlssUpscale or
            EVulkanPrimaryPlanNodeKind.DlssFrameGeneration or
            EVulkanPrimaryPlanNodeKind.AdvancedVisibility ||
            kind == EVulkanPrimaryPlanNodeKind.Query &&
            operations.GetQuery(operationIndex).Operation is ERenderQueryOperation.WriteProperties or ERenderQueryOperation.CopyResults;
    }

    /// <summary>
    /// Determines whether an operation kind supports execution in a secondary command buffer range.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <returns>True if the operation supports execution in a secondary command buffer range; otherwise, false.</returns>
    private static bool SupportsSecondaryRange(
        EVulkanPrimaryPlanNodeKind kind)
        => kind is
            EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.ComputeDispatch or
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
            EVulkanPrimaryPlanNodeKind.BufferCopy or
            EVulkanPrimaryPlanNodeKind.MemoryBarrier or
            EVulkanPrimaryPlanNodeKind.Query;

    /// <summary>
    /// Determines whether any queue ownership transfers are required for the given pass index,
    /// based on the planned image, buffer, and swapchain barriers.
    /// </summary>
    /// <param name="barrierPlanner">The Vulkan barrier planner containing the planned barriers.</param>
    /// <param name="passIndex">The index of the pass for which to check queue ownership transfers.</param>
    /// <returns>True if any queue ownership transfers are required; otherwise, false.</returns>
    private static bool HasQueueOwnershipTransfer(
        VulkanBarrierPlanner barrierPlanner,
        int passIndex)
    {
        var imageBarriers = barrierPlanner.GetBarriersForPass(passIndex);
        for (int index = 0; index < imageBarriers.Count; index++)
        {
            var barrier = imageBarriers[index];
            if (IsQueueOwnershipTransfer(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex))
                return true;
        }

        var bufferBarriers = barrierPlanner.GetBufferBarriersForPass(passIndex);
        for (int index = 0; index < bufferBarriers.Count; index++)
        {
            var barrier = bufferBarriers[index];
            if (IsQueueOwnershipTransfer(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex))
                return true;
        }

        var swapchainBarriers = barrierPlanner.GetSwapchainBarriersForPass(passIndex);
        for (int index = 0; index < swapchainBarriers.Count; index++)
        {
            var barrier = swapchainBarriers[index];
            if (IsQueueOwnershipTransfer(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex))
                return true;
        }

        return false;
    }

    private static bool HasQueueOwnershipTransfer(
        VulkanBarrierPlan barrierPlan,
        int passIndex)
    {
        ReadOnlySpan<VulkanFrozenImageBarrier> imageBarriers =
            barrierPlan.GetImageBarriersForPass(passIndex);
        for (int index = 0; index < imageBarriers.Length; index++)
            if (IsQueueOwnershipTransfer(
                    imageBarriers[index].SrcQueueFamilyIndex,
                    imageBarriers[index].DstQueueFamilyIndex))
                return true;

        ReadOnlySpan<VulkanFrozenBufferBarrier> bufferBarriers =
            barrierPlan.GetBufferBarriersForPass(passIndex);
        for (int index = 0; index < bufferBarriers.Length; index++)
            if (IsQueueOwnershipTransfer(
                    bufferBarriers[index].SrcQueueFamilyIndex,
                    bufferBarriers[index].DstQueueFamilyIndex))
                return true;

        ReadOnlySpan<VulkanFrozenSwapchainBarrier> swapchainBarriers =
            barrierPlan.GetSwapchainBarriersForPass(passIndex);
        for (int index = 0; index < swapchainBarriers.Length; index++)
            if (IsQueueOwnershipTransfer(
                    swapchainBarriers[index].SrcQueueFamilyIndex,
                    swapchainBarriers[index].DstQueueFamilyIndex))
                return true;

        return false;
    }

    private static bool IsQueueOwnershipTransfer(
        uint sourceQueueFamilyIndex,
        uint destinationQueueFamilyIndex)
        => sourceQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
           destinationQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
           sourceQueueFamilyIndex != destinationQueueFamilyIndex;

    /// <summary>
    /// Adds a terminal node to the plan, which represents an end-of-rendering or finalization action.
    /// </summary>
    /// <param name="nodeIndex">The index of the terminal node in the plan.</param>
    /// <param name="kind">The kind of the terminal node.</param>
    /// <param name="action">The action of the terminal node.</param>
    /// <param name="identity">The frame operation signature hasher to add the terminal emissions to.</param>
    private void AddTerminalNode(
        ref int nodeIndex,
        EVulkanPrimaryPlanNodeKind kind,
        EVulkanPrimaryPlanAction action,
        ref FrameOpSignatureHasher identity)
    {
        // Add a terminal node to the plan, which represents an end-of-rendering or finalization action.
        _nodes[nodeIndex] = new VulkanPrimaryPlanNode(
            kind,
            OperationIndex: -1,
            SourceIndex: -1,
            action,
            IsDrawLike: false);

        // Add the terminal emission to the identity hasher, which includes the kind, action, and index of the terminal node.
        AddTerminalEmission(ref identity, kind, action, nodeIndex++);
    }

}
