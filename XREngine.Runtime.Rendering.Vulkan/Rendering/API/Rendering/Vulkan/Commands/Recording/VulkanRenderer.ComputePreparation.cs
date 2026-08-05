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
    private bool TryPrepareComputeFrameOpsForRecording(
        uint imageIndex,
        FrameOp[] operations,
        out string failureReason)
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
                    dispatch.SetReusableDescriptorBindingOrdinal(
                        descriptorBindingOrdinal);
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
            {
                failureReason =
                    $"Compute program '{program.Data.Name ?? "UnnamedProgram"}' is not linkable before recording.";
                return false;
            }

            try
            {
                if (program.GetOrCreateComputePipeline(passIndex, passMetadata).Handle == 0)
                {
                    failureReason =
                        $"Compute pipeline '{program.Data.Name ?? "UnnamedProgram"}' is unavailable before recording.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                failureReason =
                    $"Compute pipeline '{program.Data.Name ?? "UnnamedProgram"}' preparation failed: " +
                    $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }

            if (program.TryPrepareComputeDispatchResources(
                imageIndex,
                snapshot,
                reusableDescriptorKey))
            {
                continue;
            }

            failureReason =
                $"Compute descriptor resources for '{program.Data.Name ?? "UnnamedProgram"}' " +
                $"could not be prepared before recording (op {operationIndex}/{operations.Length}).";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
