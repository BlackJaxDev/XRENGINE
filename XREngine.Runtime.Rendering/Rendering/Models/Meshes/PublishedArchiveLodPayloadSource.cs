using XREngine.Core.Files;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Opens LOD payload entries in the published game content archive, and then in the common assets archive.
    /// A root with no configured archive is skipped.
    /// </summary>
    public sealed class PublishedArchiveLodPayloadSource : ISubMeshLodPayloadSource
    {
        /// <summary>The shared instance. The source has no state.</summary>
        public static PublishedArchiveLodPayloadSource Instance { get; } = new();

        /// <inheritdoc />
        public bool TryOpen(string entryName, out CookedPayloadOwner? payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
            if (TryOpen(EPublishedContentRoot.GameContent, entryName, out payload))
                return true;
            return TryOpen(EPublishedContentRoot.CommonAssets, entryName, out payload);
        }

        private static bool TryOpen(EPublishedContentRoot root, string entryName, out CookedPayloadOwner? payload)
        {
            payload = null;
            if (!PublishedArchiveRegistry.TryGetRoot(root, out PublishedArchiveHandle handle) || !handle.Contains(entryName))
                return false;

            payload = handle.ReadAssetOwned(entryName);
            return true;
        }
    }
}
