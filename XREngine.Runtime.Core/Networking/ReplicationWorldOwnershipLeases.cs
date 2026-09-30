using System.Runtime.CompilerServices;

namespace XREngine.Networking;

/// <summary>Serializes process-wide acquisition and retirement of world replication ownership.</summary>
public static class ReplicationWorldOwnershipLeases
{
    private sealed class Entry
    {
        public WeakReference<object>? Owner;
        public long Token;
    }

    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<object, Entry> Entries = new();
    private static long _nextToken;

    public static ReplicationWorldOwnershipLease Acquire(object worldInstance, object owner)
    {
        ArgumentNullException.ThrowIfNull(worldInstance);
        ArgumentNullException.ThrowIfNull(owner);
        lock (Sync)
        {
            Entry entry = Entries.GetValue(worldInstance, static _ => new Entry());
            long token = checked(++_nextToken);
            if (token == 0)
                token = checked(++_nextToken);
            entry.Token = token;
            entry.Owner = new WeakReference<object>(owner);
            return new ReplicationWorldOwnershipLease(worldInstance, token);
        }
    }

    public static bool IsCurrent(ReplicationWorldOwnershipLease lease, object owner)
    {
        if (!lease.IsValid)
            return false;
        lock (Sync)
            return Entries.TryGetValue(lease.WorldInstance!, out Entry? entry)
                && entry.Token == lease.Token
                && entry.Owner?.TryGetTarget(out object? currentOwner) == true
                && ReferenceEquals(currentOwner, owner);
    }

    public static void Release(ReplicationWorldOwnershipLease lease, object owner)
    {
        if (!lease.IsValid)
            return;
        lock (Sync)
            if (Entries.TryGetValue(lease.WorldInstance!, out Entry? entry)
                && entry.Token == lease.Token
                && entry.Owner?.TryGetTarget(out object? currentOwner) == true
                && ReferenceEquals(currentOwner, owner))
            {
                entry.Owner = null;
            }
    }
}
