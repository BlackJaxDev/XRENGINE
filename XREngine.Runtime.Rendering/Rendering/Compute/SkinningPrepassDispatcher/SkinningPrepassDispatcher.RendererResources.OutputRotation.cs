using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        /// <summary>
        /// Selects a writable output set before residency or bounds data changes.
        /// Retired sets stay alive while GPU consumers hold their native allocations.
        /// </summary>
        public bool TrySelectWritableOutput(
            AbstractRenderer backendOwner,
            XRMesh mesh,
            bool isInterleaved,
            out string? failureReason,
            out OutputReuseFailureSnapshot failureSnapshot)
        {
            if (CanOverwriteOutput(backendOwner, out failureReason, out failureSnapshot))
                return true;

            // A frozen consumer (a retained frame plan, for example the held desktop frame
            // while the eyes render) can keep its use on the current output for many frames.
            // Another producer cohort can also record compute skinning while an earlier cohort
            // (for example desktop shadow pre-render) has not submitted its command buffer yet.
            // Rotate to another output slot and retire the previous slot with its receipt so
            // it cannot be overwritten until the earlier cohort submits and completes.
            bool awaitingCrossCohortSubmission =
                _outputReceipt is { } currentReceipt &&
                currentReceipt.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission &&
                SkinningPrepassDispatcher.ActiveProducerAttemptId != 0 &&
                currentReceipt.AttemptId != SkinningPrepassDispatcher.ActiveProducerAttemptId;

            if (_outputReceipt is not { } receipt ||
                !HasRotatableOutput(in failureSnapshot, receipt.HasConsumerUses, awaitingCrossCohortSubmission) ||
                (receipt.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission &&
                 !awaitingCrossCohortSubmission) ||
                backendOwner is not IRuntimeRendererHost host ||
                !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var capability) ||
                capability is null)
            {
                return false;
            }

            for (int i = 0; i < _outputSlotCount; i++)
            {
                if (i == _activeOutputSlot || _outputSlots[i] is not { } slot ||
                    !slot.HasBeenSelected || !slot.IsReadyForOverwrite(backendOwner, capability))
                {
                    continue;
                }

                PublishOutputSlot(slot, i);
                failureReason = null;
                failureSnapshot = default;
                return true;
            }

            if (_outputSlotCount == OutputSlotCapacity)
            {
                failureReason = "Vulkan deformation output slot pool is full and its other allocations are still in use.";
                failureSnapshot = failureSnapshot with { FailureReason = failureReason };
                return false;
            }

            using (RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication())
            {
                XRMeshRenderer.SkinnedOutputResourceState state = CreateOutputState(mesh, isInterleaved);
                publication.Complete();
                OutputSlot newSlot = new(state);
                int slotIndex = _outputSlotCount;
                _outputSlots[slotIndex] = newSlot;
                _outputSlotCount = slotIndex + 1;
                PublishOutputSlot(newSlot, slotIndex);
            }

            failureReason = null;
            failureSnapshot = default;
            return true;
        }

        /// <summary>
        /// Returns true when the current output may move to another slot: every output buffer is
        /// ready, pending GPU completion, or awaiting a different cohort's submission, and the
        /// output is still in use by pending GPU work, a frozen consumer, or another cohort.
        /// </summary>
        private static bool HasRotatableOutput(
            in OutputReuseFailureSnapshot snapshot,
            bool heldByFrozenConsumer,
            bool awaitingCrossCohortSubmission)
        {
            bool hasPendingCompletion = false;
            return AllowsRotation(snapshot.PositionsReuse, awaitingCrossCohortSubmission, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.NormalsReuse, awaitingCrossCohortSubmission, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.TangentsReuse, awaitingCrossCohortSubmission, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.InterleavedReuse, awaitingCrossCohortSubmission, ref hasPendingCompletion) &&
                   (hasPendingCompletion || heldByFrozenConsumer || awaitingCrossCohortSubmission);
        }

        private static bool AllowsRotation(
            EGpuBufferContentReuseStatus? status,
            bool awaitingCrossCohortSubmission,
            ref bool hasPendingCompletion)
        {
            if (status == EGpuBufferContentReuseStatus.PendingCompletion)
            {
                hasPendingCompletion = true;
                return true;
            }

            if (awaitingCrossCohortSubmission &&
                status == EGpuBufferContentReuseStatus.AwaitingSubmission)
            {
                hasPendingCompletion = true;
                return true;
            }

            return status is null or EGpuBufferContentReuseStatus.Ready;
        }

        private void PublishOutputSlot(OutputSlot slot, int slotIndex)
        {
            OutputSlot? prior = _outputSlots[_activeOutputSlot];
            _renderer.EnterResourcePublicationLease();
            try
            {
                _renderer.InstallSkinnedOutputResources(slot.State);
                _renderer.ValidateResourcePublicationLease();
                _activeOutputSlot = slotIndex;
                slot.HasBeenSelected = true;
                slot.ReleaseRetiredReceipt();
                // The prior slot keeps the receipt of its last output, so a frozen consumer that
                // still reads it blocks reuse of that slot.
                if (!ReferenceEquals(prior, slot))
                    prior?.RetireWithReceipt(_outputReceipt);
                ClearOutputReceipt();
                _outputAttemptUnfenced = false;
                _hasValidOutput = false;
                _hasValidSkinnedBounds = false;
            }
            catch
            {
                if (prior is not null)
                    _renderer.InstallSkinnedOutputResources(prior.State);
                throw;
            }
            finally
            {
                _renderer.ExitResourcePublicationLease();
            }
        }
    }
}
