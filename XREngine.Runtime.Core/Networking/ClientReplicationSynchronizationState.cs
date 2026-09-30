namespace XREngine.Networking;

public enum ClientReplicationSynchronizationState : byte
{
    NotAssigned = 0,
    Synchronizing = 1,
    ApplyingBaseline = 2,
    AwaitingServerConfirmation = 3,
    Synchronized = 4,
    Failed = 5,
}
