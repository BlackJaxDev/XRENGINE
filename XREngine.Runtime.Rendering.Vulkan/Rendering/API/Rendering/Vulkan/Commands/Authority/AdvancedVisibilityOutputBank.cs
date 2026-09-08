namespace XREngine.Rendering.Vulkan;

/// <summary>Mutable bounded ownership state for one Advanced output resource bank.</summary>
internal sealed class AdvancedVisibilityOutputBank
{
    internal ulong OutputId;
    internal long BackendGeneration;
    internal long BankIncarnation;
    internal EAdvancedOutputReservationBankState State;
    internal int PlanLeaseCount;
    internal int RecordedCommandBufferLeaseCount;
    // This counts active queue-domain watermarks, not submissions.
    internal int PendingQueueDomainCount;
    internal long GraphicsCompletionWatermark;
    internal long TransferCompletionWatermark;
    internal long OtherCompletionWatermark;
    internal int ActivationFailureCount;
    internal long ManagedActivationBytes;
    internal bool HasMeasuredColdActivation;
    internal string? LastActivationFailure;
}
