using System;
using System.Collections.Generic;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>
    /// Links compute programs and creates their pipelines before the frame plan
    /// is sealed. Descriptor publication must wait for the plan's final DAG
    /// order because that order owns the recording-time binding ordinal.
    /// </summary>
    internal VulkanComputePreparationResult PrepareComputeProgramsForFramePlan(
        FrameOp[] operations)
        => PrepareComputeFrameOps(
            imageIndex: 0,
            operations,
            prepareDescriptors: false);

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
            VkRenderProgram? program;
            ComputeDispatchSnapshot? snapshot;
            ulong reusableDescriptorKey;
            int passIndex;
            IReadOnlyCollection<RenderPassMetadata>? passMetadata;
            FrameOpContext frameContext;

            switch (operations[operationIndex])
            {
                case ComputeDispatchOp dispatch:
                    program = dispatch.Program;
                    snapshot = dispatch.Snapshot;
                    reusableDescriptorKey = 0UL;
                    passIndex = dispatch.PassIndex;
                    passMetadata = dispatch.Context.PassMetadata;
                    frameContext = dispatch.Context;
                    break;
                case ComputeDispatchIndirectOp indirect:
                    program = indirect.Program;
                    snapshot = indirect.Snapshot;
                    reusableDescriptorKey = 0UL;
                    passIndex = indirect.PassIndex;
                    passMetadata = indirect.Context.PassMetadata;
                    frameContext = indirect.Context;
                    break;
                default:
                    continue;
            }

            if (!program.Link())
                return new(
                    EVulkanComputePreparationOutcome.ProgramLinkFailed,
                    operationIndex,
                    operations.Length,
                    program.Data.Name);

            try
            {
                if (program.GetOrCreateComputePipeline(passIndex, passMetadata).Handle == 0)
                    return new(
                        EVulkanComputePreparationOutcome.PipelineUnavailable,
                        operationIndex,
                        operations.Length,
                        program.Data.Name);
            }
            catch (Exception exception)
            {
                return new(
                    EVulkanComputePreparationOutcome.PipelineCreationFailed,
                    operationIndex,
                    operations.Length,
                    program.Data.Name,
                    exception);
            }

            if (!prepareDescriptors)
                continue;

            // Linking publishes the program binding and link generation used by
            // the reusable descriptor key. The sealed FramePlan sequence also
            // publishes the final resource-DAG order used by secondary recording;
            // preparing from the authoring array would publish a different key.
            if (operations[operationIndex] is ComputeDispatchOp preparedDispatch)
            {
                int descriptorBindingOrdinal =
                    ResolveCommandChainInlineOperationIndex(
                        operations,
                        operationIndex);
                reusableDescriptorKey =
                    ComputeReusableComputeDescriptorBindingKey(
                        preparedDispatch,
                        descriptorBindingOrdinal);
            }

            if (program.TryPrepareComputeDispatchResources(
                VulkanProgramPlannerRequest.From(frameContext),
                imageIndex,
                snapshot,
                reusableDescriptorKey))
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
}
