using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow resource and snapshot services used while lowering a sealed frame plan
/// into command-chain packets.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    private static bool HasMutableCommandChainFrameOps(FrameOperationStream operations)
        // Submission markers publish no Vulkan command. Their current fence is
        // rebound to a cached primary immediately before submission, so they do
        // not require native command-buffer re-recording.
        => false;

    private bool HasCompleteCommandChainImageEntrySnapshot(
        CommandBuffer commandBuffer,
        out VulkanImageEntryStateMismatch failure)
    {
        failure = default;
        if (commandBuffer.Handle == 0)
        {
            failure = CreateMissingCommandChainImageEntryStateFailure(
                EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState);
            return false;
        }

        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)commandBuffer.Handle),
                    out VulkanRecordedImageLayoutState? recorded))
            {
                failure = CreateMissingCommandChainImageEntryStateFailure(
                    EVulkanPrimaryEntryStateMismatch.MissingCommandBufferState);
                return false;
            }

            if (!recorded.EntryStateIncomplete)
                return true;

            failure = recorded.EntryStateFailure.RequiresRecording
                ? recorded.EntryStateFailure
                : CreateMissingCommandChainImageEntryStateFailure(
                    EVulkanPrimaryEntryStateMismatch.IncompleteSnapshot);
            return false;
        }
    }

    /// <summary>
    /// Publishes a reusable secondary only when its native lifetime exists and
    /// its bind-state generation matches a complete image journal for the exact
    /// recording that produced the artifact. The lifetime authority uses an
    /// independent per-buffer reset generation, which is captured rather than
    /// compared numerically with the bind-state generation.
    /// Lifetime tracking is always acquired before the image-layout journal;
    /// no caller may acquire these locks in the inverse order.
    /// </summary>
    private bool TryPublishCommandChainSecondaryArtifact(
        CommandChain chain,
        VulkanResourceRuntime resources)
    {
        VulkanRecordedCommandArtifact artifact = chain.RecordedArtifact;
        ulong handle = unchecked((ulong)artifact.NativeBuffer.Handle);
        // Artifact/journal generations are issued by the command bind-state
        // authority. Command-buffer lifetime generations are a separate,
        // per-buffer reset domain; comparing their numeric values caused every
        // valid worker secondary to be rejected after publication hardening.
        ulong journalRecordingGeneration = artifact.RecordingGeneration;
        if (handle == 0 || journalRecordingGeneration == 0)
            return FailCommandChainSecondaryArtifactPublication(chain);

        // Keep lifetime and journal observations atomic with publication. In
        // particular, do not retain a dependency snapshot after its journal
        // was removed or reset for another recording generation.
        lock (resources.Lifetime.Tracker.SyncRoot)
        {
            if (!resources.Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                    handle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) ||
                lifetime.RecordingGeneration == 0)
            {
                return FailCommandChainSecondaryArtifactPublication(chain);
            }

            lock (Synchronization._vulkanImageLayoutLock)
            {
                if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                        handle,
                        out VulkanRecordedImageLayoutState? recorded) ||
                    recorded.RecordingGeneration != journalRecordingGeneration ||
                    recorded.EntryStateIncomplete)
                {
                    return FailCommandChainSecondaryArtifactPublication(chain);
                }

                int recordedPrimaryReferenceCount = 0;
                if (resources.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        new VulkanResourceLifetimeKey(ObjectType.CommandBuffer, handle),
                        out VulkanResourceLifetimeRecord? resource))
                {
                    recordedPrimaryReferenceCount = resource.Pins.RecordedReferenceCount;
                }

                ref readonly CommandRecordingDependencySignature dependencySignature =
                    ref chain.DependencySignatureReference;
                artifact.PublishExecutable(
                    in dependencySignature,
                    lifetime.TouchedDependencies,
                    lifetime.RecordingGeneration,
                    lifetime.QueuedSubmissionCount,
                    recordedPrimaryReferenceCount);
                return true;
            }
        }
    }

    private static bool FailCommandChainSecondaryArtifactPublication(CommandChain chain)
    {
        chain.RecordedArtifact.MarkFailed();
        chain.State = CommandChainState.NotReady;
        chain.DirtyReason |= CommandChainDirtyReason.SecondaryCommandBufferInvalid;
        return false;
    }

    private static VulkanImageEntryStateMismatch
        CreateMissingCommandChainImageEntryStateFailure(
            EVulkanPrimaryEntryStateMismatch reason)
        => new(
            reason,
            0UL,
            0u,
            0u,
            ImageAspectFlags.None,
            VulkanImageAccessState.Undefined,
            VulkanImageAccessState.Undefined);

    private static int GetCommandChainFrameOpKindId(FrameOp operation)
        => operation switch
        {
            ClearOp => 1,
            MeshDrawOp => 2,
            BlitOp => 3,
            IndirectDrawOp => 4,
            MeshTaskDispatchIndirectCountOp => 5,
            MemoryBarrierOp => 6,
            DlssUpscaleOp => 7,
            DlssFrameGenerationOp => 8,
            TransformFeedbackOp => 9,
            ComputeDispatchOp => 10,
            TextureUploadFrameOp => 11,
            QueryOp => 12,
            PublishFramebufferForSamplingOp => 13,
            ComputeDispatchIndirectOp => 14,
            BufferCopyOp => 15,
            SubmissionMarkerOp => 16,
            _ => 0,
        };

    private static void HashCommandChainProgramBindingSnapshot(
        ref FrameOpSignatureHasher hash,
        ComputeDispatchSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            hash.Add(0);
            return;
        }

        hash.Add(1);
        hash.Add(VulkanFrameOpSnapshotSignatures.HashSamplerUnitBindings(
            snapshot.Samplers,
            snapshot.SamplerNamesByUnit,
            snapshot.DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashSamplerNameBindings(
            snapshot.SamplersByName,
            snapshot.DescriptorSignatures,
            includeMutableFrameSourceDescriptors: true));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashImageBindings(
            snapshot.Images,
            snapshot.DescriptorSignatures));
        hash.Add(VulkanFrameOpSnapshotSignatures.HashBufferBindings(
            snapshot.Buffers));
    }
}
