using System.Buffers;

namespace XREngine.Core.Files
{
    /// <summary>Writes an asset's runtime payload to the caller's buffer.</summary>
    public delegate void PublishedCookedAssetSerializeDelegate(object asset, IBufferWriter<byte> writer);

    /// <summary>Reads an asset from a payload span. The span is valid only for the duration of the call.</summary>
    public delegate object? PublishedCookedAssetDeserializeDelegate(ReadOnlySpan<byte> payload, Type assetType);

    /// <summary>
    /// Explicit registry of runtime cooked asset codecs. Published builds deserialize only through
    /// these registrations; development builds use them before any reflective fallback.
    /// </summary>
    public static class PublishedCookedAssetRegistry
    {
        static PublishedCookedAssetRegistry()
            => AotRuntimeMetadataStore.PublishedAssetTypeResolver = ResolveRegisteredType;

        private static Type? ResolveRegisteredType(string fullTypeName, bool ignoreCase)
            => TryResolveByFullName(fullTypeName, ignoreCase, out Type? assetType) ? assetType : null;

        private sealed record Entry(
            Guid RegistrationId,
            string OwnerName,
            Type AssetType,
            PublishedCookedAssetSerializeDelegate Serialize,
            PublishedCookedAssetDeserializeDelegate Deserialize);

        private static readonly object Sync = new();
        private static readonly Dictionary<Type, Entry> Entries = [];
        private static readonly Dictionary<string, Type> ByFullName = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> ByFullNameIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

        public static IDisposable Register(
            Type assetType,
            PublishedCookedAssetSerializeDelegate serialize,
            PublishedCookedAssetDeserializeDelegate deserialize,
            string? ownerName = null)
        {
            ArgumentNullException.ThrowIfNull(assetType);
            ArgumentNullException.ThrowIfNull(serialize);
            ArgumentNullException.ThrowIfNull(deserialize);

            if (!typeof(XRAsset).IsAssignableFrom(assetType))
                throw new ArgumentException($"Type '{assetType}' must derive from {nameof(XRAsset)}.", nameof(assetType));

            Entry entry = new(
                Guid.NewGuid(),
                string.IsNullOrWhiteSpace(ownerName)
                    ? serialize.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown"
                    : ownerName,
                assetType,
                serialize,
                deserialize);
            lock (Sync)
            {
                if (Entries.TryGetValue(assetType, out Entry? existing))
                {
                    throw new InvalidOperationException(
                        $"Published cooked asset type '{assetType.FullName}' is already registered by " +
                        $"'{existing.OwnerName}'; '{entry.OwnerName}' cannot replace it.");
                }

                Entries.Add(assetType, entry);
                string? fullName = assetType.FullName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    ByFullName[fullName] = assetType;
                    ByFullNameIgnoreCase.TryAdd(fullName, assetType);
                }
            }

            return new RegistrationLease(entry);
        }

        /// <summary>Writes the asset's payload into <paramref name="writer"/>. False when no codec is registered for its type.</summary>
        public static bool TrySerialize(object asset, IBufferWriter<byte> writer)
        {
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(writer);

            if (!TryGetEntry(asset.GetType(), out Entry? entry) || entry is null)
                return false;

            entry.Serialize(asset, writer);
            return true;
        }

        /// <summary>Serializes into a new array. Intended for cooking and tooling, never for the runtime load path.</summary>
        public static bool TrySerialize(object asset, out byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(asset);

            ArrayBufferWriter<byte> writer = new();
            if (!TrySerialize(asset, writer))
            {
                payload = [];
                return false;
            }

            payload = writer.WrittenSpan.ToArray();
            return true;
        }

        /// <summary>Deserializes a payload span through the registered codec for <paramref name="assetType"/>.</summary>
        public static bool TryDeserialize(Type assetType, ReadOnlySpan<byte> payload, out object? asset)
        {
            ArgumentNullException.ThrowIfNull(assetType);

            if (TryGetEntry(assetType, out Entry? entry) && entry is not null)
            {
                asset = entry.Deserialize(payload, assetType);
                return asset is not null;
            }

            asset = null;
            return false;
        }

        public static bool IsRegistered(Type assetType)
        {
            ArgumentNullException.ThrowIfNull(assetType);
            return TryGetEntry(assetType, out _);
        }

        public static string[] SnapshotRegisteredTypeNames()
        {
            lock (Sync)
            {
                List<string> names = new(Entries.Count);
                foreach (Type type in Entries.Keys)
                {
                    string? name = type.AssemblyQualifiedName;
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }

                names.Sort(StringComparer.Ordinal);
                return [.. names];
            }
        }

        internal static bool TryResolveByFullName(string fullTypeName, bool ignoreCase, out Type? assetType)
        {
            lock (Sync)
            {
                Dictionary<string, Type> lookup = ignoreCase ? ByFullNameIgnoreCase : ByFullName;
                if (lookup.TryGetValue(fullTypeName, out Type? found))
                {
                    assetType = found;
                    return true;
                }
            }

            assetType = null;
            return false;
        }

        private static bool TryGetEntry(Type assetType, out Entry? entry)
        {
            lock (Sync)
            {
                if (Entries.TryGetValue(assetType, out entry))
                    return true;

                entry = null;
                return false;
            }
        }

        private sealed class RegistrationLease(Entry entry) : IDisposable
        {
            private Entry? _entry = entry;

            public void Dispose()
            {
                Entry? current = Interlocked.Exchange(ref _entry, null);
                if (current is null)
                    return;

                lock (Sync)
                {
                    if (!Entries.TryGetValue(current.AssetType, out Entry? registered)
                        || registered.RegistrationId != current.RegistrationId)
                    {
                        return;
                    }

                    Entries.Remove(current.AssetType);
                    string? fullName = current.AssetType.FullName;
                    if (string.IsNullOrWhiteSpace(fullName))
                        return;

                    if (ByFullName.TryGetValue(fullName, out Type? ordinal) && ordinal == current.AssetType)
                        ByFullName.Remove(fullName);
                    if (ByFullNameIgnoreCase.TryGetValue(fullName, out Type? insensitive) && insensitive == current.AssetType)
                        ByFullNameIgnoreCase.Remove(fullName);
                }
            }
        }
    }
}
