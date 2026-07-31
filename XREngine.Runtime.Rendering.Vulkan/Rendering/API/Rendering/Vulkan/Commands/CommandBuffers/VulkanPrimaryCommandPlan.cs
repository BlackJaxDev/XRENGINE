using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable ordered primary-command plan compiled before native recording.
/// </summary>
internal sealed class VulkanPrimaryCommandPlan
{
    private VulkanPrimaryPlanNode[] _nodes = new VulkanPrimaryPlanNode[64];

    internal int Count { get; private set; }
    internal int OperationCount { get; private set; }
    internal ulong Identity { get; private set; }
    internal ulong EmittedCommandSignature { get; private set; }
    internal ulong DirectRecorderCommandSignature { get; private set; }

    internal void Build(
        FrameOp[] operations,
        ulong operationSignature = 0,
        VulkanPrimaryPlanTerminalContext terminalContext = default,
        VulkanBarrierPlanner? barrierPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        int terminalNodeCount =
            1 +
            (terminalContext.RequiresPreparePresent ? 1 : 0) +
            (terminalContext.ReleaseExternalImageOwnership ? 1 : 0);
        int nodeCount = operations.Length + terminalNodeCount;
        EnsureCapacity(nodeCount);

        FrameOpSignatureHasher identity = new();
        FrameOpSignatureHasher directRecorderIdentity = new();
        identity.Add(nodeCount);
        identity.Add(operations.Length);
        identity.Add(operationSignature);
        directRecorderIdentity.Add(nodeCount);
        directRecorderIdentity.Add(operations.Length);
        directRecorderIdentity.Add(operationSignature);
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            EVulkanPrimaryPlanNodeKind kind = ResolveKind(operation);
            EVulkanPrimaryPlanAction actions =
                ResolveActions(kind, operation, barrierPlanner);
            bool isDrawLike = kind is
                EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount;
            _nodes[index] = new VulkanPrimaryPlanNode(
                kind,
                operation,
                index,
                actions,
                isDrawLike);

            identity.Add((byte)kind);
            identity.Add((byte)actions);
            identity.Add(index);
            identity.Add(operation.PassIndex);
            identity.Add(operation.Context.PipelineIdentity);
            identity.Add(operation.Context.ViewportIdentity);
            identity.Add(operation.Context.SchedulingIdentity);
            identity.Add(operation.Target is null
                ? 0
                : RuntimeHelpers.GetHashCode(operation.Target));
            AddDirectRecorderEmission(
                ref directRecorderIdentity,
                operation,
                index,
                barrierPlanner);
        }

        int nodeIndex = operations.Length;
        AddTerminalNode(
            ref nodeIndex,
            EVulkanPrimaryPlanNodeKind.EndRendering,
            EVulkanPrimaryPlanAction.EndRendering,
            ref identity);
        if (terminalContext.RequiresPreparePresent)
        {
            AddTerminalNode(
                ref nodeIndex,
                EVulkanPrimaryPlanNodeKind.PreparePresent,
                EVulkanPrimaryPlanAction.PreparePresent,
                ref identity);
        }
        if (terminalContext.ReleaseExternalImageOwnership)
        {
            AddTerminalNode(
                ref nodeIndex,
                EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership,
                EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership,
                ref identity);
        }
        AddDirectRecorderTerminalEmission(
            ref directRecorderIdentity,
            operations.Length,
            terminalContext);

        if (Count > nodeCount)
            Array.Clear(_nodes, nodeCount, Count - nodeCount);
        Count = nodeCount;
        OperationCount = operations.Length;
        Identity = identity.ToHash();
        EmittedCommandSignature = Identity;
        DirectRecorderCommandSignature = directRecorderIdentity.ToHash();
        System.Diagnostics.Debug.Assert(
            IsEquivalentToDirectOperations(operations, barrierPlanner),
            "The typed primary plan no longer matches direct FrameOp dispatch semantics.");
        System.Diagnostics.Debug.Assert(
            EmittedCommandSignature == DirectRecorderCommandSignature,
            "The typed primary plan emitted-command signature no longer matches the direct recorder.");
    }

    /// <summary>
    /// Compares the typed projection with the original direct recorder's
    /// operation classification and render-scope termination policy.
    /// </summary>
    internal bool IsEquivalentToDirectOperations(
        FrameOp[] operations,
        VulkanBarrierPlanner? barrierPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Length != OperationCount)
            return false;

        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];
            ref readonly VulkanPrimaryPlanNode node = ref _nodes[index];
            if (!ReferenceEquals(node.Operation, operation) ||
                node.SourceIndex != index ||
                node.Kind != ResolveDirectRecorderKind(operation) ||
                node.Actions !=
                    ResolveDirectRecorderActions(operation, barrierPlanner))
            {
                return false;
            }
        }

        return true;
    }

    internal bool HasTerminalAction(EVulkanPrimaryPlanAction action)
    {
        for (int index = OperationCount; index < Count; index++)
        {
            if ((_nodes[index].Actions & action) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compares the complete typed and direct-recorder emission projections,
    /// including the authoritative dependency snapshot used for cache reuse.
    /// </summary>
    internal bool HasEquivalentEmissionAndDependencies(
        in CommandRecordingDependencySignature dependencies)
    {
        VulkanCommandIdentityComponents dependencyComponents =
            dependencies.CaptureIdentityComponents();
        FrameOpSignatureHasher typed = new();
        typed.Add(EmittedCommandSignature);
        dependencyComponents.AddTo(ref typed);

        FrameOpSignatureHasher direct = new();
        direct.Add(DirectRecorderCommandSignature);
        dependencyComponents.AddTo(ref direct);
        return typed.ToHash() == direct.ToHash();
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

    private static EVulkanPrimaryPlanNodeKind ResolveKind(FrameOp operation)
        => operation switch
        {
            TextureUploadFrameOp =>
                EVulkanPrimaryPlanNodeKind.TextureUpload,
            BlitOp => EVulkanPrimaryPlanNodeKind.Blit,
            ClearOp => EVulkanPrimaryPlanNodeKind.Clear,
            TransformFeedbackOp =>
                EVulkanPrimaryPlanNodeKind.TransformFeedback,
            QueryOp => EVulkanPrimaryPlanNodeKind.Query,
            MeshDrawOp => EVulkanPrimaryPlanNodeKind.MeshDraw,
            IndirectDrawOp => EVulkanPrimaryPlanNodeKind.IndirectDraw,
            MeshTaskDispatchIndirectCountOp =>
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount,
            ComputeDispatchOp =>
                EVulkanPrimaryPlanNodeKind.ComputeDispatch,
            ComputeDispatchIndirectOp =>
                EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect,
            BufferCopyOp => EVulkanPrimaryPlanNodeKind.BufferCopy,
            SubmissionMarkerOp =>
                EVulkanPrimaryPlanNodeKind.SubmissionMarker,
            MemoryBarrierOp => EVulkanPrimaryPlanNodeKind.MemoryBarrier,
            PublishFramebufferForSamplingOp =>
                EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling,
            DlssUpscaleOp => EVulkanPrimaryPlanNodeKind.DlssUpscale,
            DlssFrameGenerationOp =>
                EVulkanPrimaryPlanNodeKind.DlssFrameGeneration,
            _ => EVulkanPrimaryPlanNodeKind.Unsupported,
        };

    private static EVulkanPrimaryPlanAction ResolveActions(
        EVulkanPrimaryPlanNodeKind kind,
        FrameOp operation,
        VulkanBarrierPlanner? barrierPlanner)
    {
        EVulkanPrimaryPlanAction actions =
            EVulkanPrimaryPlanAction.RecordOperation;
        if (kind != EVulkanPrimaryPlanNodeKind.TextureUpload)
            actions |= EVulkanPrimaryPlanAction.BarrierBatch;
        if (barrierPlanner is not null &&
            HasQueueOwnershipTransfer(
                barrierPlanner,
                operation.PassIndex))
        {
            actions |= EVulkanPrimaryPlanAction.QueueOwnershipTransfer;
        }
        if (RequiresRenderingScope(kind, operation))
            actions |= EVulkanPrimaryPlanAction.BeginRendering;
        if (SupportsSecondaryRange(kind))
            actions |= EVulkanPrimaryPlanAction.ExecuteSecondaryRange;
        if (EndsRenderScope(kind, operation))
            actions |= EVulkanPrimaryPlanAction.EndRendering;
        return actions;
    }

    private static bool RequiresRenderingScope(
        EVulkanPrimaryPlanNodeKind kind,
        FrameOp operation)
        => kind is
            EVulkanPrimaryPlanNodeKind.Clear or
            EVulkanPrimaryPlanNodeKind.TransformFeedback or
            EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount ||
            kind == EVulkanPrimaryPlanNodeKind.Query &&
            ((QueryOp)operation).Operation is
                ERenderQueryOperation.Begin or
                ERenderQueryOperation.End;

    private static bool SupportsSecondaryRange(
        EVulkanPrimaryPlanNodeKind kind)
        => kind is
            EVulkanPrimaryPlanNodeKind.MeshDraw or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.ComputeDispatch or
            EVulkanPrimaryPlanNodeKind.BufferCopy or
            EVulkanPrimaryPlanNodeKind.Query;

    private static bool EndsRenderScope(
        EVulkanPrimaryPlanNodeKind kind,
        FrameOp operation)
        => kind is
            EVulkanPrimaryPlanNodeKind.TextureUpload or
            EVulkanPrimaryPlanNodeKind.Blit or
            EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.ComputeDispatch or
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
            EVulkanPrimaryPlanNodeKind.BufferCopy or
            EVulkanPrimaryPlanNodeKind.SubmissionMarker or
            EVulkanPrimaryPlanNodeKind.MemoryBarrier or
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling or
            EVulkanPrimaryPlanNodeKind.DlssUpscale or
            EVulkanPrimaryPlanNodeKind.DlssFrameGeneration ||
            kind == EVulkanPrimaryPlanNodeKind.Query &&
            ((QueryOp)operation).Operation is
                ERenderQueryOperation.WriteProperties or
                ERenderQueryOperation.CopyResults;

    // Kept independent from ResolveKind/EndsRenderScope so tests catch drift
    // between the typed plan and the original direct recorder contract.
    private static EVulkanPrimaryPlanNodeKind ResolveDirectRecorderKind(FrameOp operation)
        => operation switch
        {
            TextureUploadFrameOp => EVulkanPrimaryPlanNodeKind.TextureUpload,
            BlitOp => EVulkanPrimaryPlanNodeKind.Blit,
            ClearOp => EVulkanPrimaryPlanNodeKind.Clear,
            TransformFeedbackOp => EVulkanPrimaryPlanNodeKind.TransformFeedback,
            QueryOp => EVulkanPrimaryPlanNodeKind.Query,
            MeshDrawOp => EVulkanPrimaryPlanNodeKind.MeshDraw,
            IndirectDrawOp => EVulkanPrimaryPlanNodeKind.IndirectDraw,
            MeshTaskDispatchIndirectCountOp => EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount,
            ComputeDispatchOp => EVulkanPrimaryPlanNodeKind.ComputeDispatch,
            ComputeDispatchIndirectOp => EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect,
            BufferCopyOp => EVulkanPrimaryPlanNodeKind.BufferCopy,
            SubmissionMarkerOp => EVulkanPrimaryPlanNodeKind.SubmissionMarker,
            MemoryBarrierOp => EVulkanPrimaryPlanNodeKind.MemoryBarrier,
            PublishFramebufferForSamplingOp => EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling,
            DlssUpscaleOp => EVulkanPrimaryPlanNodeKind.DlssUpscale,
            DlssFrameGenerationOp => EVulkanPrimaryPlanNodeKind.DlssFrameGeneration,
            _ => EVulkanPrimaryPlanNodeKind.Unsupported,
            };

    private static EVulkanPrimaryPlanAction ResolveDirectRecorderActions(
        FrameOp operation,
        VulkanBarrierPlanner? barrierPlanner)
    {
        EVulkanPrimaryPlanAction actions =
            EVulkanPrimaryPlanAction.RecordOperation;
        if (operation is not TextureUploadFrameOp)
            actions |= EVulkanPrimaryPlanAction.BarrierBatch;
        if (barrierPlanner is not null &&
            DirectRecorderHasQueueOwnershipTransfer(
                barrierPlanner,
                operation.PassIndex))
        {
            actions |= EVulkanPrimaryPlanAction.QueueOwnershipTransfer;
        }
        if (operation is
            ClearOp or
            TransformFeedbackOp or
            MeshDrawOp or
            IndirectDrawOp or
            MeshTaskDispatchIndirectCountOp ||
            operation is QueryOp
            {
                Operation:
                    ERenderQueryOperation.Begin or
                    ERenderQueryOperation.End,
            })
        {
            actions |= EVulkanPrimaryPlanAction.BeginRendering;
        }
        if (operation is
            MeshDrawOp or
            IndirectDrawOp or
            ComputeDispatchOp or
            BufferCopyOp or
            QueryOp)
        {
            actions |= EVulkanPrimaryPlanAction.ExecuteSecondaryRange;
        }
        if (DirectRecorderEndsRenderScope(operation))
            actions |= EVulkanPrimaryPlanAction.EndRendering;
        return actions;
    }

    private static bool DirectRecorderEndsRenderScope(FrameOp operation)
        => operation is
            TextureUploadFrameOp or
            BlitOp or
            IndirectDrawOp or
            ComputeDispatchOp or
            ComputeDispatchIndirectOp or
            BufferCopyOp or
            SubmissionMarkerOp or
            MemoryBarrierOp or
            PublishFramebufferForSamplingOp or
            DlssUpscaleOp or
            DlssFrameGenerationOp ||
            operation is QueryOp
            {
                Operation:
                    ERenderQueryOperation.WriteProperties or
                    ERenderQueryOperation.CopyResults,
            };

    private static void AddDirectRecorderEmission(
        ref FrameOpSignatureHasher identity,
        FrameOp operation,
        int index,
        VulkanBarrierPlanner? barrierPlanner)
    {
        identity.Add((byte)ResolveDirectRecorderKind(operation));
        identity.Add((byte)ResolveDirectRecorderActions(
            operation,
            barrierPlanner));
        identity.Add(index);
        identity.Add(operation.PassIndex);
        identity.Add(operation.Context.PipelineIdentity);
        identity.Add(operation.Context.ViewportIdentity);
        identity.Add(operation.Context.SchedulingIdentity);
        identity.Add(operation.Target is null
            ? 0
            : RuntimeHelpers.GetHashCode(operation.Target));
    }

    private static bool HasQueueOwnershipTransfer(
        VulkanBarrierPlanner barrierPlanner,
        int passIndex)
    {
        IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> imageBarriers =
            barrierPlanner.GetBarriersForPass(passIndex);
        for (int index = 0; index < imageBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedImageBarrier barrier =
                imageBarriers[index];
            if (IsQueueOwnershipTransfer(
                    barrier.SrcQueueFamilyIndex,
                    barrier.DstQueueFamilyIndex))
            {
                return true;
            }
        }

        IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> bufferBarriers =
            barrierPlanner.GetBufferBarriersForPass(passIndex);
        for (int index = 0; index < bufferBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedBufferBarrier barrier =
                bufferBarriers[index];
            if (IsQueueOwnershipTransfer(
                    barrier.SrcQueueFamilyIndex,
                    barrier.DstQueueFamilyIndex))
            {
                return true;
            }
        }

        IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier>
            swapchainBarriers =
                barrierPlanner.GetSwapchainBarriersForPass(passIndex);
        for (int index = 0; index < swapchainBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedSwapchainBarrier barrier =
                swapchainBarriers[index];
            if (IsQueueOwnershipTransfer(
                    barrier.SrcQueueFamilyIndex,
                    barrier.DstQueueFamilyIndex))
            {
                return true;
            }
        }

        return false;
    }

    // Kept structurally independent from HasQueueOwnershipTransfer so the
    // direct-recorder signature detects typed-plan classification drift.
    private static bool DirectRecorderHasQueueOwnershipTransfer(
        VulkanBarrierPlanner barrierPlanner,
        int passIndex)
    {
        foreach (VulkanBarrierPlanner.PlannedImageBarrier barrier in
                 barrierPlanner.GetBarriersForPass(passIndex))
        {
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
            {
                return true;
            }
        }

        foreach (VulkanBarrierPlanner.PlannedBufferBarrier barrier in
                 barrierPlanner.GetBufferBarriersForPass(passIndex))
        {
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
            {
                return true;
            }
        }

        foreach (VulkanBarrierPlanner.PlannedSwapchainBarrier barrier in
                 barrierPlanner.GetSwapchainBarriersForPass(passIndex))
        {
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQueueOwnershipTransfer(
        uint sourceQueueFamilyIndex,
        uint destinationQueueFamilyIndex)
        => sourceQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
           destinationQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
           sourceQueueFamilyIndex != destinationQueueFamilyIndex;

    private void AddTerminalNode(
        ref int nodeIndex,
        EVulkanPrimaryPlanNodeKind kind,
        EVulkanPrimaryPlanAction action,
        ref FrameOpSignatureHasher identity)
    {
        _nodes[nodeIndex] = new VulkanPrimaryPlanNode(
            kind,
            Operation: null,
            SourceIndex: -1,
            action,
            IsDrawLike: false);
        AddTerminalEmission(ref identity, kind, action, nodeIndex);
        nodeIndex++;
    }

    private static void AddDirectRecorderTerminalEmission(
        ref FrameOpSignatureHasher identity,
        int operationCount,
        in VulkanPrimaryPlanTerminalContext terminalContext)
    {
        int nodeIndex = operationCount;
        AddTerminalEmission(
            ref identity,
            EVulkanPrimaryPlanNodeKind.EndRendering,
            EVulkanPrimaryPlanAction.EndRendering,
            nodeIndex++);
        if (terminalContext.RequiresPreparePresent)
        {
            AddTerminalEmission(
                ref identity,
                EVulkanPrimaryPlanNodeKind.PreparePresent,
                EVulkanPrimaryPlanAction.PreparePresent,
                nodeIndex++);
        }
        if (terminalContext.ReleaseExternalImageOwnership)
        {
            AddTerminalEmission(
                ref identity,
                EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership,
                EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership,
                nodeIndex);
        }
    }

    private static void AddTerminalEmission(
        ref FrameOpSignatureHasher identity,
        EVulkanPrimaryPlanNodeKind kind,
        EVulkanPrimaryPlanAction action,
        int nodeIndex)
    {
        identity.Add((byte)kind);
        identity.Add((byte)action);
        identity.Add(nodeIndex);
        identity.Add(-1);
        identity.Add(0UL);
        identity.Add(0UL);
        identity.Add(0UL);
        identity.Add(0);
    }
}
