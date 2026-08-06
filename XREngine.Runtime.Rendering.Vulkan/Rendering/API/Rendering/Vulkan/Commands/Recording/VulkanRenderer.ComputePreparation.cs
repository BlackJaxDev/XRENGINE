using System;
using System.Collections.Generic;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Prepares compute pipelines, persistent uniform buffers, and reusable
    /// descriptor sets before the command recorder enters its guarded scope.
    /// </summary>
    private VulkanComputePreparationResult PrepareComputeFrameOpsForRecording(
        uint imageIndex,
        FrameOp[] operations)
    {
        for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
        {
            VkRenderProgram? program;
            ComputeDispatchSnapshot? snapshot;
            ulong reusableDescriptorKey;
            int passIndex;
            IReadOnlyCollection<RenderPassMetadata>? passMetadata;

            switch (operations[operationIndex])
            {
                case ComputeDispatchOp dispatch:
                    program = dispatch.Program;
                    snapshot = dispatch.Snapshot;
                    int descriptorBindingOrdinal =
                        ResolveCommandChainInlineOperationIndex(
                            operations,
                            operationIndex);
                    reusableDescriptorKey =
                        ComputeReusableComputeDescriptorBindingKey(
                            dispatch,
                            descriptorBindingOrdinal);
                    passIndex = dispatch.PassIndex;
                    passMetadata = dispatch.Context.PassMetadata;
                    break;
                case ComputeDispatchIndirectOp indirect:
                    program = indirect.Program;
                    snapshot = indirect.Snapshot;
                    reusableDescriptorKey = 0UL;
                    passIndex = indirect.PassIndex;
                    passMetadata = indirect.Context.PassMetadata;
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

            if (program.TryPrepareComputeDispatchResources(
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
