using System.Text;
using XREngine.Assets.Recovery;
using XREngine.Core.Engine;

namespace XREngine;

public partial class AssetManager
{
    private AssetCreationRecoveryStore? _creationRecovery;

    /// <summary>
    /// Gets the creation recovery journal of the current project, or null when no project is
    /// configured or the journal could not be opened. Native asset creation is unavailable while
    /// this is null.
    /// </summary>
    public AssetCreationRecoveryStore? CreationRecovery => Volatile.Read(ref _creationRecovery);

    /// <summary>
    /// Returns true when a journaled creation protects <paramref name="path"/>. Watcher events,
    /// metadata synchronization, and saves skip a protected path.
    /// </summary>
    public bool IsCreationRecoveryProtected(string path)
        => CreationRecovery?.IsProtected(path) == true;

    /// <summary>
    /// Builds the metadata file bytes for a file that a journaled creation publishes. The bytes use
    /// the same format that metadata synchronization writes. Returns false when the path is not in
    /// the current assets root.
    /// </summary>
    public bool TryBuildCreationMetadata(string assetPath, Guid guid, out string metadataPath, out byte[] bytes)
    {
        bytes = [];
        lock (_metadataLock)
        {
            AuthoringContext? context = TryCaptureAuthoringContext();
            if (context is null || !TryMapMetadataPath(context, assetPath, out metadataPath, out string relativePath))
            {
                metadataPath = string.Empty;
                return false;
            }
            string extension = Path.GetExtension(assetPath);
            var meta = new AssetMetadata
            {
                Name = Path.GetFileName(assetPath),
                RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                IsDirectory = false,
                Guid = guid == Guid.Empty ? Guid.NewGuid() : guid,
                LastSyncedUtc = DateTime.UtcNow,
            };
            if (!extension.Equals($".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                meta.Import = new AssetImportMetadata
                {
                    SourceExtension = extension.StartsWith('.') ? extension[1..] : extension,
                    ImporterType = ResolveImporterNameForExtension(extension),
                };
            bytes = new UTF8Encoding(false).GetBytes(Serializer.Serialize(meta));
            return true;
        }
    }

    private void OpenCreationRecovery(string? projectRoot)
    {
        if (projectRoot is null)
        {
            Volatile.Write(ref _creationRecovery, null);
            return;
        }
        AssetCreationRecoveryStore? current = CreationRecovery;
        if (current is not null && string.Equals(current.ProjectRoot,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        AssetCreationRecoveryStore? store = null;
        try { store = AssetCreationRecoveryStore.Open(projectRoot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.LogWarning($"[AssetRecovery] The creation recovery journal could not be opened. Native asset creation is unavailable: {exception.Message}");
        }
        Volatile.Write(ref _creationRecovery, store);
        if (store is null)
            return;
        foreach (AssetCreationRecoveryOperation operation in store.GetOperations())
            if (operation.State == EAssetCreationRecoveryState.RecoveryRequired)
                Debug.LogWarning($"[AssetRecovery] Operation {operation.Id} requires recovery: {operation.Problem} " +
                    $"Protected paths: {string.Join(", ", operation.PublicPaths)}. Records: '{operation.Directory}'.");
    }

    /// <summary>
    /// Admits a normal save to <paramref name="path"/>. A protected path refuses the save. A
    /// published creation receipt for the path is released first, so its Undo cannot replay.
    /// </summary>
    private void AdmitAuthoringWrite(string path)
    {
        AssetCreationRecoveryStore? store = CreationRecovery;
        if (store is null)
            return;
        if (store.IsProtected(path))
            throw new IOException($"AssetRecovery.Protected: '{path}' belongs to an unfinished asset creation. Resolve its recovery record first.");
        store.ReleasePublishedFor(path, "Saved");
    }
}
