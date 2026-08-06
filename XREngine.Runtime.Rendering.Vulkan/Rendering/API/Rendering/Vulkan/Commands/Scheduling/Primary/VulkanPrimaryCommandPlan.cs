using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable ordered primary-command plan compiled before native recording.
/// </summary>
internal sealed class VulkanPrimaryCommandPlan
{
    /// <summary>
    /// The array of plan nodes, which may be larger than the actual count of nodes in use.
    /// A node is a typed projection of a FrameOp, including its resolved kind, actions, and source index.
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
    /// <summary>
    /// The identity of the plan as emitted by the direct recorder,
    /// which may differ from the typed plan's identity if there are differences in classification or actions.
    /// </summary>
    internal ulong EmittedCommandSignature { get; private set; }
    /// <summary>
    /// The identity of the plan as emitted by the direct recorder,
    /// which may differ from the typed plan's identity if there are differences in classification or actions.
    /// </summary>
    internal ulong DirectRecorderCommandSignature { get; private set; }

    /// <summary>
    /// Builds a typed primary command plan from the given FrameOps,
    /// computing the authoritative operation signature and terminal context.
    /// The plan is reusable for multiple command buffer recordings.
    /// </summary>
    /// <param name="operations">The array of FrameOps to build the plan from.</param>
    /// <param name="operationSignature">The precomputed operation signature for the FrameOps.</param>
    /// <param name="terminalContext">The terminal context specifying end-of-rendering behavior.</param>
    /// <param name="barrierPlanner">Optional barrier planner for synchronization.</param>
    internal void Build(
        FrameOp[] operations,
        ulong operationSignature = 0,
        VulkanPrimaryPlanTerminalContext terminalContext = default,
        VulkanBarrierPlanner? barrierPlanner = null)
        => Build(
            new FrameOperationSequence(operations),
            operationSignature,
            terminalContext,
            barrierPlanner);

    internal void Build(
        FrameOperationStream operations,
        ulong operationSignature = 0,
        VulkanPrimaryPlanTerminalContext terminalContext = default,
        VulkanBarrierPlanner? barrierPlanner = null)
        => Build(
            new FrameOperationSequence(operations),
            operationSignature,
            terminalContext,
            barrierPlanner);

    internal void Build(
        FrameOperationSequence operations,
        ulong operationSignature,
        VulkanPrimaryPlanTerminalContext terminalContext,
        VulkanBarrierPlanner? barrierPlanner)
    {

        // Compute the total number of nodes needed, including terminal nodes for end-of-rendering actions.
        int terminalNodeCount =
            1 + //1 is for the EndRendering node
            (terminalContext.RequiresPreparePresent ? 1 : 0) + //1 is for the PreparePresent node
            (terminalContext.ReleaseExternalImageOwnership ? 1 : 0); //1 is for the ReleaseExternalImageOwnership node

        // Ensure the internal node array has enough capacity to hold all nodes.
        int nodeCount = operations.Length + terminalNodeCount;
        EnsureCapacity(nodeCount);

        // Compute the identity of the plan based on the FrameOps, their kinds, actions, and terminal nodes.
        // identity is used for caching and comparison purposes,
        // while directRecorderIdentity is used to compare against the direct recorder's emission.

        FrameOpSignatureHasher identity = new();
        identity.Add(nodeCount);
        identity.Add(operations.Length);
        identity.Add(operationSignature);

        FrameOpSignatureHasher directRecorderIdentity = new();
        directRecorderIdentity.Add(nodeCount);
        directRecorderIdentity.Add(operations.Length);
        directRecorderIdentity.Add(operationSignature);

        // Build the plan nodes for each FrameOp, resolving their kinds and actions, and adding them to the identity hashers.
        for (int opIndex = 0; opIndex < operations.Length; opIndex++)
        {
            // Build a typed plan node for the FrameOp at the current index,
            // resolving its kind and actions,
            // and adding it to the identity hashers.
            FrameOp operation = operations[opIndex];

            // Determine the kind of the primary plan node based on the FrameOp type.
            EVulkanPrimaryPlanNodeKind kind = operation.Kind;
            if (kind == EVulkanPrimaryPlanNodeKind.Unsupported)
                throw new InvalidOperationException($"Unsupported FrameOp type: {operation.GetType().Name}");

            // Resolve the actions for the FrameOp based on its kind, the operation itself, and any barrier planning that may be required.
            EVulkanPrimaryPlanAction actions = ResolveActions(kind, operation, barrierPlanner);

            // Determine if the operation is a draw-like operation, which affects how it is handled in the plan.
            bool isDrawLike = kind is
                EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount;

            // Create a new VulkanPrimaryPlanNode for the FrameOp, including its kind, actions, source index, and draw-like status.
            _nodes[opIndex] = new VulkanPrimaryPlanNode(
                kind,
                operation,
                opIndex,
                actions,
                isDrawLike);

            var opContext = operation.Context;

            int passIndex = operation.PassIndex;
            int pipelineIdentity = opContext.PipelineIdentity;
            int viewportIdentity = opContext.ViewportIdentity;
            int schedulingIdentity = opContext.SchedulingIdentity;
            int targetHashCode = operation.Target is null ? 0 : RuntimeHelpers.GetHashCode(operation.Target);

            // Add the emissions for the typed plan's identity hasher, including the kind, actions, and other relevant information for the FrameOp.
            AddEmission(
                ref identity,
                kind,
                actions,
                opIndex,
                passIndex,
                pipelineIdentity,
                viewportIdentity,
                schedulingIdentity,
                targetHashCode);

            // Add the emissions for the direct recorder's identity hasher,
            // which may differ from the typed plan's emissions if there are differences in classification or actions.
            AddEmission(
                ref directRecorderIdentity,
                kind,
                ResolveDirectRecorderActions(operation, barrierPlanner),
                opIndex,
                passIndex,
                pipelineIdentity,
                viewportIdentity,
                schedulingIdentity,
                targetHashCode);
        }

        // Add the terminal nodes for end-of-rendering actions,
        // including EndRendering, PreparePresent, and ReleaseExternalImageOwnership, if required by the terminal context.
        int nodeIndex = operations.Length;
        AddTerminalNode(
            ref nodeIndex,
            EVulkanPrimaryPlanNodeKind.EndRendering,
            EVulkanPrimaryPlanAction.EndRendering,
            ref identity);

        // Add the PreparePresent terminal node if required by the terminal context.
        if (terminalContext.RequiresPreparePresent)
        {
            AddTerminalNode(
                ref nodeIndex,
                EVulkanPrimaryPlanNodeKind.PreparePresent,
                EVulkanPrimaryPlanAction.PreparePresent,
                ref identity);
        }

        // Add the ReleaseExternalImageOwnership terminal node if required by the terminal context.
        if (terminalContext.ReleaseExternalImageOwnership)
        {
            AddTerminalNode(
                ref nodeIndex,
                EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership,
                EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership,
                ref identity);
        }

        // Add the terminal emissions for the direct recorder's command signature,
        // including end-of-rendering, prepare-present, and release-external-image-ownership actions.
        AddDirectRecorderTerminalEmission(
            ref directRecorderIdentity,
            operations.Length,
            terminalContext);

        // Clear any unused nodes in the internal array to avoid holding references to old FrameOps.
        if (Count > nodeCount)
            Array.Clear(_nodes, nodeCount, Count - nodeCount);

        // Set the final counts and identities for the plan, including the total node count, operation count, and computed identities.
        Count = nodeCount;
        OperationCount = operations.Length;
        Identity = identity.ToHash();
        EmittedCommandSignature = Identity;
        DirectRecorderCommandSignature = directRecorderIdentity.ToHash();

        // Validate that the typed primary plan matches the direct FrameOp dispatch semantics
        System.Diagnostics.Debug.Assert(
            IsEquivalentToDirectOperations(operations, barrierPlanner),
            "The typed primary plan no longer matches direct FrameOp dispatch semantics.");

        // Validate that the typed primary plan's emitted-command signature matches the direct recorder's signature
        System.Diagnostics.Debug.Assert(
            EmittedCommandSignature == DirectRecorderCommandSignature,
            "The typed primary plan emitted-command signature no longer matches the direct recorder.");
    }

    /// <summary>
    /// Adds an emission to the identity hasher, including the kind, actions, index, and other relevant information for a FrameOp or terminal node.
    /// </summary>
    /// <param name="identity">The frame operation signature hasher to add the emission to.</param>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="actions">The actions associated with the primary plan node.</param>
    /// <param name="index">The index of the primary plan node.</param>
    /// <param name="passIndex">The pass index of the FrameOp.</param>
    /// <param name="pipelineIdentity">The pipeline identity of the FrameOp.</param>
    /// <param name="viewportIdentity">The viewport identity of the FrameOp.</param>
    /// <param name="schedulingIdentity">The scheduling identity of the FrameOp.</param>
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

    /// <summary>
    /// Compares the typed projection with the original direct recorder's
    /// operation classification and render-scope termination policy.
    /// </summary>
    internal bool IsEquivalentToDirectOperations(
        FrameOp[] operations,
        VulkanBarrierPlanner? barrierPlanner = null)
        => IsEquivalentToDirectOperations(
            new FrameOperationSequence(operations),
            barrierPlanner);

    internal bool IsEquivalentToDirectOperations(
        FrameOperationSequence operations,
        VulkanBarrierPlanner? barrierPlanner = null)
    {
        if (operations.Length != OperationCount)
            return false;

        // Compare each FrameOp with the corresponding typed plan node,
        // checking for equivalence in operation reference, source index, kind, and resolved actions.
        for (int index = 0; index < operations.Length; index++)
        {
            FrameOp operation = operations[index];

            ref readonly VulkanPrimaryPlanNode node = ref _nodes[index];

            // Compare the operation reference, source index, kind, and resolved actions for equivalence.
            if (!ReferenceEquals(node.Operation, operation) ||
                node.SourceIndex != index ||
                node.Kind != operation.Kind ||
                node.Actions != ResolveDirectRecorderActions(operation, barrierPlanner))
                return false;
        }

        return true;
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

    /// <summary>
    /// Compares the complete typed and direct-recorder emission projections,
    /// including the authoritative dependency snapshot used for cache reuse.
    /// </summary>
    internal bool HasEquivalentEmissionAndDependencies(
        in CommandRecordingDependencySignature dependencies)
    {
        // Capture the identity components from the dependency signature,
        // which includes the relevant information for comparing the typed and direct-recorder emissions.
        VulkanCommandIdentityComponents dependencyComponents = dependencies.CaptureIdentityComponents();
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

    /// <summary>
    /// Resolves the actions for a given FrameOp based on its kind, the operation itself, and any barrier planning that may be required.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="operation">The FrameOp for which to resolve actions.</param>
    /// <param name="barrierPlanner">Optional barrier planner for queue ownership transfers.</param>
    /// <returns>The resolved actions for the given FrameOp.</returns>
    private static EVulkanPrimaryPlanAction ResolveActions(
        EVulkanPrimaryPlanNodeKind kind,
        FrameOp operation,
        VulkanBarrierPlanner? barrierPlanner)
    {
        // Determine the actions for the FrameOp based on its kind and any required synchronization or rendering scope.
        // Start with the default action of recording the operation, and add additional actions as needed.
        EVulkanPrimaryPlanAction actions = EVulkanPrimaryPlanAction.RecordOperation;

        // Add a barrier batch action for all operations except texture uploads, which are handled separately.
        if (kind != EVulkanPrimaryPlanNodeKind.TextureUpload)
            actions |= EVulkanPrimaryPlanAction.BarrierBatch;

        // Check if a queue ownership transfer is required for this operation based on the barrier planner and the operation's pass index.
        if (barrierPlanner is not null && HasQueueOwnershipTransfer(barrierPlanner, operation.PassIndex))
            actions |= EVulkanPrimaryPlanAction.QueueOwnershipTransfer;

        // Determine if the operation requires a rendering scope to be begun, based on its kind and specific operation type.
        if (RequiresRenderingScope(kind, operation))
            actions |= EVulkanPrimaryPlanAction.BeginRendering;

        // Determine if the operation supports execution in a secondary command buffer range, based on its kind.
        if (SupportsSecondaryRange(kind))
            actions |= EVulkanPrimaryPlanAction.ExecuteSecondaryRange;

        // Determine if the operation ends a rendering scope, based on its kind and specific operation type.
        if (EndsRenderScope(kind, operation))
            actions |= EVulkanPrimaryPlanAction.EndRendering;

        return actions;
    }

    /// <summary>
    /// Determines whether a given FrameOp requires a rendering scope to be begun, based on its kind and specific operation type.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="operation">The FrameOp for which to check if a rendering scope is required.</param>
    /// <returns>True if the FrameOp requires a rendering scope to be begun; otherwise, false.</returns>
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

    /// <summary>
    /// Determines whether a given FrameOp supports execution in a secondary command buffer range, based on its kind.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <returns>True if the FrameOp supports execution in a secondary command buffer range; otherwise, false.</returns>
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
    /// Determines whether a given FrameOp ends a rendering scope, based on its kind and specific operation type.
    /// </summary>
    /// <param name="kind">The kind of the primary plan node.</param>
    /// <param name="operation">The FrameOp for which to check if it ends a rendering scope.</param>
    /// <returns>True if the FrameOp ends a rendering scope; otherwise, false.</returns>
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

    private static EVulkanPrimaryPlanAction ResolveDirectRecorderActions(
        FrameOp operation,
        VulkanBarrierPlanner? barrierPlanner)
    {
        EVulkanPrimaryPlanAction actions = EVulkanPrimaryPlanAction.RecordOperation;
        if (operation is not TextureUploadFrameOp)
            actions |= EVulkanPrimaryPlanAction.BarrierBatch;
        if (barrierPlanner is not null &&
            DirectRecorderHasQueueOwnershipTransfer(
                barrierPlanner,
                operation.PassIndex))
            actions |= EVulkanPrimaryPlanAction.QueueOwnershipTransfer;

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
            ComputeDispatchIndirectOp or
            BufferCopyOp or
            MemoryBarrierOp or
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

    // Kept structurally independent from HasQueueOwnershipTransfer so the
    // direct-recorder signature detects typed-plan classification drift.
    private static bool DirectRecorderHasQueueOwnershipTransfer(VulkanBarrierPlanner barrierPlanner, int passIndex)
    {
        IReadOnlyList<VulkanBarrierPlanner.PlannedImageBarrier> imageBarriers =
            barrierPlanner.GetBarriersForPass(passIndex);
        for (int index = 0; index < imageBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedImageBarrier barrier =
                imageBarriers[index];
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
                return true;
        }

        IReadOnlyList<VulkanBarrierPlanner.PlannedBufferBarrier> bufferBarriers =
            barrierPlanner.GetBufferBarriersForPass(passIndex);
        for (int index = 0; index < bufferBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedBufferBarrier barrier =
                bufferBarriers[index];
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
                return true;
        }

        IReadOnlyList<VulkanBarrierPlanner.PlannedSwapchainBarrier>
            swapchainBarriers =
                barrierPlanner.GetSwapchainBarriersForPass(passIndex);
        for (int index = 0; index < swapchainBarriers.Count; index++)
        {
            VulkanBarrierPlanner.PlannedSwapchainBarrier barrier =
                swapchainBarriers[index];
            if (barrier.SrcQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.DstQueueFamilyIndex != Silk.NET.Vulkan.Vk.QueueFamilyIgnored &&
                barrier.SrcQueueFamilyIndex != barrier.DstQueueFamilyIndex)
                return true;
        }

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
            Operation: null,
            SourceIndex: -1,
            action,
            IsDrawLike: false);

        // Add the terminal emission to the identity hasher, which includes the kind, action, and index of the terminal node.
        AddTerminalEmission(ref identity, kind, action, nodeIndex++);
    }

    /// <summary>
    /// Adds the terminal emissions for the direct recorder's command signature,
    /// including end-of-rendering, prepare-present, and release-external-image-ownership actions.
    /// </summary>
    /// <param name="identity">The frame operation signature hasher to add the terminal emissions to.</param>
    /// <param name="operationCount">The number of operations before adding the terminal emissions.</param>
    /// <param name="terminalContext">The context containing terminal node requirements.</param>
    private static void AddDirectRecorderTerminalEmission(
        ref FrameOpSignatureHasher identity,
        int operationCount,
        in VulkanPrimaryPlanTerminalContext terminalContext)
    {
        // The index of the first terminal node is equal to the number of operations,
        // since terminal nodes are added after all operations have been processed.
        int nodeIndex = operationCount;

        // Add the EndRendering terminal emission, which is always required.
        AddTerminalEmission(
            ref identity,
            EVulkanPrimaryPlanNodeKind.EndRendering,
            EVulkanPrimaryPlanAction.EndRendering,
            nodeIndex++);

        // If the terminal context requires a PreparePresent node, add its emission.
        if (terminalContext.RequiresPreparePresent)
            AddTerminalEmission(
                ref identity,
                EVulkanPrimaryPlanNodeKind.PreparePresent,
                EVulkanPrimaryPlanAction.PreparePresent,
                nodeIndex++);

        // If the terminal context requires a ReleaseExternalImageOwnership node, add its emission.
        if (terminalContext.ReleaseExternalImageOwnership)
            AddTerminalEmission(
                ref identity,
                EVulkanPrimaryPlanNodeKind.ReleaseExternalImageOwnership,
                EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership,
                nodeIndex);
    }
}
