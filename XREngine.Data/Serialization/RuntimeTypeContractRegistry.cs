namespace XREngine;

/// <summary>Maps stable runtime IDs and persisted type names to statically rooted types.</summary>
public static class RuntimeTypeContractRegistry
{
    private sealed record Entry(Guid Token, Type Type, string Id, int SchemaVersion);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> ById = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, Entry> ByType = [];
    private static readonly Dictionary<string, Entry> ByFullName = new(StringComparer.Ordinal);

    public static IDisposable Register(Type type, string id, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        if (type.ContainsGenericParameters)
            throw new ArgumentException("Runtime type contracts require a closed type.", nameof(type));
        string fullName = type.FullName ?? throw new ArgumentException("Runtime type must have a full name.", nameof(type));

        Entry entry = new(Guid.NewGuid(), type, id, schemaVersion);
        lock (Sync)
        {
            if (ById.TryGetValue(id, out Entry? existing))
                throw new InvalidOperationException($"Runtime type ID '{id}' is already registered for '{existing.Type}'.");
            if (ByType.ContainsKey(type))
                throw new InvalidOperationException($"Runtime type '{type}' is already registered.");
            ById.Add(id, entry);
            ByType.Add(type, entry);
            ByFullName.Add(fullName, entry);
        }
        return new Lease(entry);
    }

    public static bool TryResolve(string idOrTypeName, out Type? type, bool ignoreCase = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrTypeName);
        lock (Sync)
        {
            if (ById.TryGetValue(idOrTypeName, out Entry? entry) ||
                ByFullName.TryGetValue(idOrTypeName, out entry))
            {
                type = entry.Type;
                return true;
            }
            if (ignoreCase)
            {
                foreach (Entry candidate in ByFullName.Values)
                {
                    if (string.Equals(candidate.Type.FullName, idOrTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        type = candidate.Type;
                        return true;
                    }
                }
            }
        }
        type = null;
        return false;
    }

    public static bool TryGet(Type type, out string? id, out int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (Sync)
        {
            if (ByType.TryGetValue(type, out Entry? entry))
            {
                id = entry.Id;
                schemaVersion = entry.SchemaVersion;
                return true;
            }
        }
        id = null;
        schemaVersion = 0;
        return false;
    }

    private sealed class Lease(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;

        public void Dispose()
        {
            Entry? current = Interlocked.Exchange(ref _entry, null);
            if (current is null)
                return;
            lock (Sync)
            {
                if (ById.TryGetValue(current.Id, out Entry? installed) && installed.Token == current.Token)
                {
                    ById.Remove(current.Id);
                    ByType.Remove(current.Type);
                    ByFullName.Remove(current.Type.FullName!);
                }
            }
        }
    }
}
