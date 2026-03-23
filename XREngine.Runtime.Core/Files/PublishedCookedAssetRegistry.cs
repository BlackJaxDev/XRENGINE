namespace XREngine.Core.Files
{
    public delegate byte[] PublishedCookedAssetSerializeDelegate(object asset);

    public delegate object? PublishedCookedAssetDeserializeDelegate(byte[] payload, Type assetType);

    public static class PublishedCookedAssetRegistry
    {
        private sealed record Entry(
            Type AssetType,
            PublishedCookedAssetSerializeDelegate Serialize,
            PublishedCookedAssetDeserializeDelegate Deserialize);

        private static readonly object Sync = new();
        private static readonly Dictionary<Type, Entry> Entries = [];

        public static void Register(
            Type assetType,
            PublishedCookedAssetSerializeDelegate serialize,
            PublishedCookedAssetDeserializeDelegate deserialize)
        {
            ArgumentNullException.ThrowIfNull(assetType);
            ArgumentNullException.ThrowIfNull(serialize);
            ArgumentNullException.ThrowIfNull(deserialize);

            if (!typeof(XRAsset).IsAssignableFrom(assetType))
                throw new ArgumentException($"Type '{assetType}' must derive from {nameof(XRAsset)}.", nameof(assetType));

            lock (Sync)
                Entries[assetType] = new Entry(assetType, serialize, deserialize);
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
    }
}