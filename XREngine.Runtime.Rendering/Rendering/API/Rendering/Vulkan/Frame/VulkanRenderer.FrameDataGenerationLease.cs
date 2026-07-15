namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Exact ownership state for the frame-data generation captured by one command-buffer
    /// variant. Recording, cache, and queue ownership are deliberately independent: evicting
    /// a cached variant cannot release a generation that is still executing on a queue.
    /// </summary>
    internal struct VulkanFrameDataGenerationLease
    {
        public ulong Generation { get; private set; }
        public bool HasRecordingOwner { get; private set; }
        public bool HasCachedVariantOwner { get; private set; }
        public ulong GraphicsSequence { get; private set; }
        public ulong TransferSequence { get; private set; }
        public ulong OtherSequence { get; private set; }

        public bool HasSubmittedOwner =>
            GraphicsSequence != 0 || TransferSequence != 0 || OtherSequence != 0;

        public bool HasAnyOwner =>
            HasRecordingOwner || HasCachedVariantOwner || HasSubmittedOwner;

        public bool TryAcquireRecording(ulong generation, bool commandBufferQueued)
        {
            if (generation == 0 || commandBufferQueued)
                return false;
            if (Generation != 0 && Generation != generation && HasAnyOwner)
                return false;

            Generation = generation;
            HasRecordingOwner = true;
            return true;
        }

        public void CompleteRecording(bool cacheVariant)
        {
            HasRecordingOwner = false;
            HasCachedVariantOwner |= cacheVariant;
            ClearGenerationWhenUnowned();
        }

        public bool TryTransferToSubmission(EVulkanLifetimeQueueDomain domain, ulong queueSequence)
        {
            if (Generation == 0 || queueSequence == 0)
                return false;

            HasRecordingOwner = false;
            HasCachedVariantOwner = true;
            switch (domain)
            {
                case EVulkanLifetimeQueueDomain.Graphics:
                    GraphicsSequence = Math.Max(GraphicsSequence, queueSequence);
                    break;
                case EVulkanLifetimeQueueDomain.Transfer:
                    TransferSequence = Math.Max(TransferSequence, queueSequence);
                    break;
                default:
                    OtherSequence = Math.Max(OtherSequence, queueSequence);
                    break;
            }
            return true;
        }

        public void AbandonRecording()
        {
            HasRecordingOwner = false;
            ClearGenerationWhenUnowned();
        }

        public void EvictCachedVariant()
        {
            HasRecordingOwner = false;
            HasCachedVariantOwner = false;
            ClearGenerationWhenUnowned();
        }

        public void ObserveQueueCompletion(
            ulong completedGraphicsSequence,
            ulong completedTransferSequence,
            ulong completedOtherSequence)
        {
            if (GraphicsSequence != 0 && GraphicsSequence <= completedGraphicsSequence)
                GraphicsSequence = 0;
            if (TransferSequence != 0 && TransferSequence <= completedTransferSequence)
                TransferSequence = 0;
            if (OtherSequence != 0 && OtherSequence <= completedOtherSequence)
                OtherSequence = 0;
            ClearGenerationWhenUnowned();
        }

        public void Reset()
            => this = default;

        private void ClearGenerationWhenUnowned()
        {
            if (!HasAnyOwner)
                Generation = 0;
        }
    }
}
