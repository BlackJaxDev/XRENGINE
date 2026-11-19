using XREngine.Rendering;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    internal sealed class VulkanOutputReceipt : IRenderResourceLeaseOwner
    {
        private int _referenceCount;
        private int _consumerUseCount;
        private bool _initialized;
        private bool _reserved;

        public AbstractRenderer BackendOwner { get; private set; } = null!;
        public ulong AttemptId { get; private set; }
        public XRGpuFence? Fence { get; private set; }
        public bool ProducerAccepted { get; private set; }
        public bool HasConsumerUses => Volatile.Read(ref _consumerUseCount) != 0;
        internal int ReferenceCount => Volatile.Read(ref _referenceCount);
        internal int ConsumerUseCount => Volatile.Read(ref _consumerUseCount);

        public bool TryReserve()
        {
            if (_reserved)
                return false;
            if (_initialized)
            {
                if (Volatile.Read(ref _referenceCount) != 0 || !CanReleaseFence())
                    return false;
                Fence?.Dispose();
                Fence = null;
                _initialized = false;
            }

            _reserved = true;
            return true;
        }

        public void Publish(
            AbstractRenderer backendOwner,
            ulong attemptId,
            XRGpuFence? fence,
            bool producerAccepted)
        {
            if (!_reserved)
                throw new InvalidOperationException("The Vulkan deformation receipt slot was not reserved.");
            BackendOwner = backendOwner;
            AttemptId = attemptId;
            Fence = fence;
            ProducerAccepted = producerAccepted;
            Volatile.Write(ref _referenceCount, 0);
            _initialized = true;
            _reserved = false;
        }

        public void CancelReservation()
        {
            if (_reserved)
                _reserved = false;
        }

        public bool IsUsableForCurrentAttempt(ulong attemptId)
        {
            if (!_initialized || !ProducerAccepted || attemptId == 0UL || AttemptId != attemptId || Fence is null || Fence.IsDisposed)
                return false;

            return Fence.SubmissionStatus switch
            {
                EGpuFenceSubmissionStatus.AwaitingSubmission => true,
                EGpuFenceSubmissionStatus.Submitted => Fence.Poll() != EGpuFenceStatus.Failed,
                _ => false,
            };
        }

        public bool IsSubmittedAndHealthy
            => _initialized && ProducerAccepted &&
               Fence is { IsDisposed: false, SubmissionStatus: EGpuFenceSubmissionStatus.Submitted } &&
               Fence.Poll() != EGpuFenceStatus.Failed &&
               AbstractRenderer.Current is not IRuntimeRendererHost { IsDeviceLost: true };

        public bool IsHealthyForFrozenConsumer
        {
            get
            {
                if (!_initialized || !ProducerAccepted || Fence is null || Fence.IsDisposed ||
                    AbstractRenderer.Current is IRuntimeRendererHost { IsDeviceLost: true })
                {
                    return false;
                }

                return Fence.SubmissionStatus switch
                {
                    EGpuFenceSubmissionStatus.AwaitingSubmission => true,
                    EGpuFenceSubmissionStatus.Submitted => Fence.Poll() != EGpuFenceStatus.Failed,
                    _ => false,
                };
            }
        }

        public void AddReference()
            => Interlocked.Increment(ref _referenceCount);

        public void ReleaseReference()
        {
            Interlocked.Decrement(ref _referenceCount);
        }

        public void RetainAuthoringUse()
        {
            AddReference();
            Interlocked.Increment(ref _consumerUseCount);
        }

        public void ReleaseAuthoringUse()
        {
            int remaining = Interlocked.Decrement(ref _consumerUseCount);
            if (remaining < 0)
                throw new InvalidOperationException("A Vulkan deformation consumer released an unowned output use.");
            ReleaseReference();
        }

        private bool CanReleaseFence()
        {
            if (Fence is null)
                return true;
            if (Fence.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            return Fence.SubmissionStatus == EGpuFenceSubmissionStatus.Failed ||
                   Fence.Poll() != EGpuFenceStatus.Pending;
        }
    }
}
