using System;
using System.Collections.Generic;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Links compute programs and creates their pipelines before the frame plan
    /// is sealed. Descriptor publication must wait for the plan's final DAG
    /// order because that order owns the recording-time binding ordinal.
    /// </summary>
    internal VulkanComputePreparationResult PrepareComputeProgramsForFramePlan(
        FrameOp[] operations)
    {
        for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            VkRenderProgram program;
            int passIndex;
            IReadOnlyCollection<RenderPassMetadata>? passMetadata;
            switch (operations[operationIndex])
            {
                case ComputeDispatchOp dispatch:
                    program = dispatch.Program;
                    passIndex = dispatch.PassIndex;
                    passMetadata = dispatch.Context.PassMetadata;
                    break;
                case ComputeDispatchIndirectOp indirect:
                    program = indirect.Program;
                    passIndex = indirect.PassIndex;
                    passMetadata = indirect.Context.PassMetadata;
                    break;
                default:
                    continue;
            }

            VulkanComputePreparationResult preparation =
                TryPrepareComputeProgram(
                    program,
                    passIndex,
                    passMetadata,
                    operationIndex,
                    operations.Length);
            if (!preparation.Succeeded)
                return preparation;
        }

        return VulkanComputePreparationResult.Success;
    }

    /// <summary>
    /// Publishes persistent uniform buffers and reusable descriptor sets for
    /// the exact sealed operation sequence consumed by command recording.
    /// </summary>
    internal VulkanComputePreparationResult PrepareComputeFrameOpsForRecording(
        uint imageIndex,
        FrameOperationSequence operations)
        => PrepareComputeFrameOps(
            imageIndex,
            operations,
            prepareDescriptors: true);

    private VulkanComputePreparationResult PrepareComputeFrameOps(
        uint imageIndex,
        FrameOperationSequence operations,
        bool prepareDescriptors)
    {
        for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            VkRenderProgram program;
            ComputeDispatchSnapshot? snapshot;
            ulong reusableDescriptorKey;
            bool requiresComputePipeline;
            bool excludeGlobalTextureArray;
            int passIndex;
            IReadOnlyCollection<RenderPassMetadata>? passMetadata;
            FrameOpContext frameContext;

            ref readonly FrameOperationHeader header =
                ref operations.GetHeader(operationIndex);
            ref readonly FrameOpContext operationContext =
                ref operations.GetContext(operationIndex);
            switch (header.OpCode)
            {
                case EVulkanPrimaryPlanNodeKind.ComputeDispatch:
                    ref readonly ComputeDispatchPayload dispatch =
                        ref operations.GetComputeDispatch(operationIndex);
                    program = dispatch.Program;
                    snapshot = dispatch.Snapshot;
                    reusableDescriptorKey = 0UL;
                    requiresComputePipeline = true;
                    excludeGlobalTextureArray = false;
                    passIndex = header.PassIndex;
                    passMetadata = operationContext.PassMetadata;
                    frameContext = operationContext;
                    break;
                case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect:
                    ref readonly ComputeDispatchIndirectPayload indirect =
                        ref operations.GetComputeDispatchIndirect(operationIndex);
                    program = indirect.Program;
                    snapshot = indirect.Snapshot;
                    reusableDescriptorKey = 0UL;
                    requiresComputePipeline = true;
                    excludeGlobalTextureArray = false;
                    passIndex = header.PassIndex;
                    passMetadata = operationContext.PassMetadata;
                    frameContext = operationContext;
                    break;
                case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount:
                    ref readonly MeshTaskDispatchIndirectCountPayload meshTask =
                        ref operations.GetMeshTask(operationIndex);
                    program = meshTask.Program;
                    snapshot = meshTask.ProgramBindingSnapshot;
                    reusableDescriptorKey = snapshot.ComputeReusableDescriptorBindingKey();
                    requiresComputePipeline = false;
                    excludeGlobalTextureArray = meshTask.BindlessMaterialTextures is not null;
                    passIndex = header.PassIndex;
                    passMetadata = operationContext.PassMetadata;
                    frameContext = operationContext;
                    break;
                default:
                    continue;
            }

            VulkanComputePreparationResult preparation = requiresComputePipeline
                ? TryPrepareComputeProgram(
                    program,
                    passIndex,
                    passMetadata,
                    operationIndex,
                    operations.Length)
                : TryPrepareMeshTaskDescriptorProgram(
                    program,
                    operationIndex,
                    operations.Length);
            if (!preparation.Succeeded)
                return preparation;

            if (!prepareDescriptors)
                continue;

            // Linking publishes the program binding and link generation used by
            // the reusable descriptor key. The sealed FramePlan sequence also
            // publishes the final resource-DAG order used by secondary recording;
            // preparing from the authoring array would publish a different key.
            if (header.OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatch)
            {
                int descriptorBindingOrdinal =
                    ResolveCommandChainInlineOperationIndex(
                        operations.Stream,
                        operationIndex);
                ref readonly ComputeDispatchPayload preparedDispatch =
                    ref operations.GetComputeDispatch(operationIndex);
                reusableDescriptorKey =
                    ComputeReusableComputeDescriptorBindingKey(
                        in preparedDispatch,
                        in header,
                        in operationContext,
                        descriptorBindingOrdinal);
            }

            if (program.TryPrepareComputeDispatchResources(
                VulkanProgramPlannerRequest.From(frameContext),
                imageIndex,
                snapshot,
                reusableDescriptorKey,
                excludeGlobalTextureArray))
            {
                continue;
            }

            return new(
                EVulkanComputePreparationOutcome.DescriptorPreparationFailed,
                operationIndex,
                operations.Length,
                program.Data.Name);
        }

        return VulkanComputePreparationResult.Success;
    }

    private static VulkanComputePreparationResult TryPrepareMeshTaskDescriptorProgram(
        VkRenderProgram program,
        int operationIndex,
        int operationCount)
    {
        if (program.Link() && program.PipelineLayout.Handle != 0)
            return VulkanComputePreparationResult.Success;

        return new(
            EVulkanComputePreparationOutcome.ProgramLinkFailed,
            operationIndex,
            operationCount,
            program.Data.Name);
    }

    private static VulkanComputePreparationResult TryPrepareComputeProgram(
        VkRenderProgram program,
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        int operationIndex,
        int operationCount)
    {
        if (!program.Link())
        {
            return new(
                EVulkanComputePreparationOutcome.ProgramLinkFailed,
                operationIndex,
                operationCount,
                program.Data.Name);
        }

        try
        {
            if (program.GetOrCreateComputePipeline(passIndex, passMetadata).Handle != 0)
                return VulkanComputePreparationResult.Success;

            return new(
                EVulkanComputePreparationOutcome.PipelineUnavailable,
                operationIndex,
                operationCount,
                program.Data.Name);
        }
        catch (Exception exception)
        {
            return new(
                EVulkanComputePreparationOutcome.PipelineCreationFailed,
                operationIndex,
                operationCount,
                program.Data.Name,
                exception);
        }
    }
}
