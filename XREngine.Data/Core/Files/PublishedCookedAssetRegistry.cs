namespace XREngine.Core.Files
{
    public delegate byte[] PublishedCookedAssetSerializeDelegate(object asset);

    public delegate object? PublishedCookedAssetDeserializeDelegate(byte[] payload, Type assetType);

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
            PublishedCookedAssetDeserializeDelegate Deserialize,
            PublishedCookedAssetDependenciesDelegate? Dependencies);

        private static readonly object Sync = new();
        private static readonly Dictionary<Type, Entry> Entries = [];

        public static IDisposable Register(
            Type assetType,
            PublishedCookedAssetSerializeDelegate serialize,
            PublishedCookedAssetDeserializeDelegate deserialize,
            string? ownerName = null)
            => Register(assetType, serialize, deserialize, ownerName, dependencies: null);

        public static IDisposable Register(
            Type assetType,
            PublishedCookedAssetSerializeDelegate serialize,
            PublishedCookedAssetDeserializeDelegate deserialize,
            string? ownerName,
            PublishedCookedAssetDependenciesDelegate? dependencies)
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
                deserialize,
                dependencies);
            lock (Sync)
            {
                if (Entries.TryGetValue(assetType, out Entry? existing))
                {
                    throw new InvalidOperationException(
                        $"Published cooked asset type '{assetType.FullName}' is already registered by " +
                        $"'{existing.OwnerName}'; '{entry.OwnerName}' cannot replace it.");
                }

                Entries.Add(assetType, entry);
            }

            return new RegistrationLease(entry);
        }

        public static bool TrySerialize(object asset, out byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(asset);

            if (TryGetEntry(asset.GetType(), out Entry? entry) && entry is not null)
            {
                payload = entry.Serialize(asset);
                return true;
            }

            payload = Array.Empty<byte>();
            return false;
        }

        public static bool TryDeserialize(Type assetType, byte[] payload, out object? asset)
        {
            ArgumentNullException.ThrowIfNull(assetType);
            ArgumentNullException.ThrowIfNull(payload);

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

        /// <summary>
        /// Returns false when the serializer owner has not declared its external-reference contract.
        /// An explicitly empty declaration is distinct from an unknown dependency set.
        /// </summary>
        public static bool TryGetDependencies(object asset, out IReadOnlyList<PublishedCookedAssetDependency>? dependencies)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (TryGetEntry(asset.GetType(), out Entry? entry) && entry?.Dependencies is { } describe)
            {
                dependencies = describe(asset) ?? throw new InvalidOperationException(
                    $"Published cooked serializer for '{asset.GetType().FullName}' returned null dependencies.");
                return true;
            }

            dependencies = null;
            return false;
        }

        public static string[] SnapshotRegisteredTypeNames()
        {
            lock (Sync)
            {
                return [.. Entries.Keys
                    .Select(static x => x.AssemblyQualifiedName)
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .OrderBy(static x => x, StringComparer.Ordinal)];
            }
        }

        /// <summary>Returns registered type identities for a cooker's exact assembly-ownership boundary.</summary>
        public static Type[] SnapshotRegisteredTypes()
        {
            lock (Sync)
                return [.. Entries.Keys.OrderBy(static type => type.AssemblyQualifiedName, StringComparer.Ordinal)];
        }

        internal static bool TryResolveByFullName(string fullTypeName, bool ignoreCase, out Type? assetType)
        {
            StringComparison comparison = ignoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            lock (Sync)
            {
                foreach (Type registeredType in Entries.Keys)
                {
                    if (string.Equals(registeredType.FullName, fullTypeName, comparison))
                    {
                        assetType = registeredType;
                        return true;
                    }
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
                    if (Entries.TryGetValue(current.AssetType, out Entry? registered)
                        && registered.RegistrationId == current.RegistrationId)
                    {
                        Entries.Remove(current.AssetType);
                    }
                }
            }
        }
    }
}
