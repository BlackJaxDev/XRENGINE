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

            if (!HasRotatablePendingOutput(in failureSnapshot) ||
                _outputReceipt is not { } receipt ||
                receipt.HasConsumerUses ||
                receipt.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission ||
                backendOwner is not IRuntimeRendererHost host ||
                !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var capability) ||
                capability is null)
            {
                return false;
            }

            for (int i = 0; i < _outputSlotCount; i++)
            {
                if (i == _activeOutputSlot || _outputSlots[i] is not { } slot ||
                    !slot.HasBeenSelected || !slot.IsReadyForOverwrite(capability))
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

        private static bool HasRotatablePendingOutput(in OutputReuseFailureSnapshot snapshot)
        {
            bool hasPendingCompletion = false;
            return AllowsRotation(snapshot.PositionsReuse, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.NormalsReuse, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.TangentsReuse, ref hasPendingCompletion) &&
                   AllowsRotation(snapshot.InterleavedReuse, ref hasPendingCompletion) &&
                   hasPendingCompletion;
        }

        private static bool AllowsRotation(
            EGpuBufferContentReuseStatus? status,
            ref bool hasPendingCompletion)
        {
            if (status == EGpuBufferContentReuseStatus.PendingCompletion)
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
