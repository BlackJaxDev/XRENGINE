using System.Runtime.CompilerServices;

namespace XREngine.Networking;

/// <summary>
/// Process-wide ownership fence for deferred replication cleanup. World contexts
/// may be recreated, so leases are keyed by the stable runtime world instance.
/// </summary>
public readonly record struct ReplicationWorldOwnershipLease(object? WorldInstance, long Token)
{
    public bool IsValid => WorldInstance is not null && Token != 0;
}

