namespace XREngine.Rendering.Commands;

/// <summary>Numeric event identities used by the bounded S13a trace.</summary>
public enum S13aPublicationTraceEventKind : byte
{
    IdentityPublished = 1,
    IdentityNotification = 2,
    OtherDirtyNotification = 3,
    ManualDirty = 4,
    QueueAdded = 5,
    QueueDuplicate = 6,
    QueueClean = 7,
    SwapBegin = 8,
    SwapCallback = 9,
    SwapAcknowledged = 10,
    SwapAuthorityYield = 11,
    SwapCleanSkip = 12,
    PublicationReused = 13,
    PublicationMissing = 14,
    PublicationExpired = 15,
    PublicationResourceMutation = 16,
    PublicationMaterialMutation = 17,
    PublicationCommandMutation = 18,
    PublicationTemporalMutation = 19,
    PublicationRegistrationRemoval = 20,
    PublicationCommitted = 21,
}
