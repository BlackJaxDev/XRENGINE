using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        /// <summary>Keeps one complete compute output generation alive across GPU submissions.</summary>
        private sealed class OutputSlot(XRMeshRenderer.SkinnedOutputResourceState state)
        {
            public XRMeshRenderer.SkinnedOutputResourceState State { get; } = state;
            public bool HasBeenSelected { get; set; }
            public AbstractRenderer? LastBackendOwner { get; set; }

            /// <summary>
            /// The producer receipt of the output that this slot held when another slot became
            /// active. A retained frame plan (a frozen consumer) can still read this slot through
            /// that receipt, so the slot is not overwritten while the receipt has consumer uses.
            /// The slot holds one receipt reference until it is selected again.
            /// </summary>
            private VulkanOutputReceipt? _retiredReceipt;

            /// <summary>Stores the receipt of this slot's last output when the slot stops being active.</summary>
            public void RetireWithReceipt(VulkanOutputReceipt? receipt)
            {
                ReleaseRetiredReceipt();
                if (receipt is null)
                    return;

                receipt.AddReference();
                _retiredReceipt = receipt;
            }

            /// <summary>Drops the retired receipt when this slot becomes the active output again.</summary>
            public void ReleaseRetiredReceipt()
                => Interlocked.Exchange(ref _retiredReceipt, null)?.ReleaseReference();

            /// <summary>Returns true when <paramref name="receipt"/> matches the retired output receipt held by this slot.</summary>
            public bool HasRetiredReceipt(VulkanOutputReceipt receipt)
                => ReferenceEquals(_retiredReceipt, receipt);

            /// <summary>
            /// Drops retired receipt and owner tracking that belong to a retiring renderer.
            /// </summary>
            public bool ResetForRetiredRenderer(AbstractRenderer retiredRenderer)
            {
                bool changed = false;
                if (_retiredReceipt is { } retired && ReferenceEquals(retired.BackendOwner, retiredRenderer))
                {
                    ReleaseRetiredReceipt();
                    changed = true;
                }

                if (ReferenceEquals(LastBackendOwner, retiredRenderer))
                {
                    LastBackendOwner = null;
                    changed = true;
                }

                return changed;
            }

            /// <summary>
            /// Clears retired receipt and owner tracking that belong to a different renderer instance.
            /// </summary>
            public void ResetForBackendOwnerChange(AbstractRenderer currentBackendOwner)
            {
                if (_retiredReceipt is { } retired && !ReferenceEquals(retired.BackendOwner, currentBackendOwner))
                    ReleaseRetiredReceipt();
                if (LastBackendOwner is not null && !ReferenceEquals(LastBackendOwner, currentBackendOwner))
                    LastBackendOwner = null;
            }

            public bool IsReadyForOverwrite(
                AbstractRenderer backendOwner,
                IGpuBufferContentReuseCapability capability)
            {
                if (_retiredReceipt is { } retired)
                {
                    if (!ReferenceEquals(retired.BackendOwner, backendOwner))
                        ReleaseRetiredReceipt();
                    else if (retired.HasConsumerUses ||
                             retired.Fence?.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
                        return false;
                }

                if (LastBackendOwner is null || !ReferenceEquals(LastBackendOwner, backendOwner))
                    return true;

                return IsReady(capability, State.Positions) &&
                       IsReady(capability, State.Normals) &&
                       IsReady(capability, State.Tangents) &&
                       IsReady(capability, State.Interleaved);
            }

            private static bool IsReady(IGpuBufferContentReuseCapability capability, XRDataBuffer? buffer)
                => buffer is null || capability.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;

            public void Destroy()
            {
                ReleaseRetiredReceipt();
                State.Positions?.Destroy();
                State.Normals?.Destroy();
                State.Tangents?.Destroy();
                State.Interleaved?.Destroy();
            }
        }
    }
}
