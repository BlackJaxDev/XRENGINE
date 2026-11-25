using XREngine.Assets.Recovery;
using XREngine.Core.Files;

namespace XREngine.Editor;

/// <summary>
/// Creates project files through the asset creation recovery journal and records the creation as
/// one structural Undo step. Undo moves the files back into the journal. Redo publishes them again
/// without overwriting. The journal keeps the immutable original bytes. Eviction from the Undo
/// history releases the receipt and keeps every file.
/// </summary>
internal static class NativeAssetCreation
{
    /// <summary>
    /// Publishes <paramref name="files"/> and their metadata. Requires an open user change scope.
    /// <paramref name="afterPublish"/> runs after each publish. <paramref name="beforeRetain"/>
    /// runs before Undo moves the files. When the first <paramref name="afterPublish"/> throws, the
    /// files move back into the journal, the receipt is released, and the failure propagates.
    /// </summary>
    internal static AssetCreationRecoveryOperation Create(string description,
        IReadOnlyList<NativeAssetCreationFile> files, Action? afterPublish = null, Action? beforeRetain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new ArgumentException("A creation needs at least one file.", nameof(files));
        AssetManager assets = Engine.Assets
            ?? throw new InvalidOperationException("AssetRecovery.Unavailable: The asset manager is not available.");
        AssetCreationRecoveryStore store = assets.CreationRecovery
            ?? throw new InvalidOperationException("AssetRecovery.Unavailable: No project recovery journal is open, so native asset creation is unavailable.");

        var staged = new List<AssetCreationRecoveryFile>(files.Count * 2);
        foreach (NativeAssetCreationFile file in files)
        {
            if (!assets.TryBuildCreationMetadata(file.AssetPath, file.Guid, out string metadataPath, out byte[] metadata))
                throw new InvalidOperationException($"AssetRecovery.OutsideAssets: '{file.AssetPath}' is not in the current assets root.");
            staged.Add(new AssetCreationRecoveryFile(file.AssetPath, file.Bytes));
            staged.Add(new AssetCreationRecoveryFile(metadataPath, metadata));
        }

        AssetCreationRecoveryOperation operation = store.Prepare(files[0].Guid, staged);
        try { store.Publish(operation); }
        catch
        {
            if (operation.State == EAssetCreationRecoveryState.Prepared)
                store.Release(operation, "Publish was refused.");
            throw;
        }
        SuppressDelayedWatcherEvents(assets, operation);

        try { afterPublish?.Invoke(); }
        catch
        {
            try
            {
                beforeRetain?.Invoke();
                store.Retain(operation);
                SuppressDelayedWatcherEvents(assets, operation);
                store.Release(operation, "The creation callback failed.");
            }
            catch (Exception cleanup)
            {
                Debug.LogWarning($"[AssetRecovery] Operation {operation.Id} kept its published files after a failed creation callback: {cleanup.Message}");
            }
            throw;
        }

        using StructuralChangeHandle handle = Undo.BeginStructuralMutation(description,
            undoAction: () =>
            {
                beforeRetain?.Invoke();
                store.Retain(operation);
                SuppressDelayedWatcherEvents(assets, operation);
            },
            redoAction: () =>
            {
                store.Publish(operation);
                SuppressDelayedWatcherEvents(assets, operation);
                afterPublish?.Invoke();
            });
        handle.RetainResource(new UndoHistoryResource(() =>
        {
            if (operation.State is EAssetCreationRecoveryState.Published or EAssetCreationRecoveryState.Retained)
                store.Release(operation, "The Undo history released the creation.");
        }));
        return operation;
    }

    /// <summary>
    /// Creates a new asset of <paramref name="type"/> in <paramref name="directory"/> as one
    /// "Create Asset" Undo action. Returns the new asset path.
    /// </summary>
    internal static string CreateAsset(string root, string directory, Type type, string displayName)
    {
        ArgumentNullException.ThrowIfNull(type);
        string folder = NativeAssetPathPolicy.RequireUnderRoot(root, directory, allowRoot: true);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The asset folder no longer exists: '{folder}'.");
        if (Activator.CreateInstance(type) is not XRAsset asset)
            throw new InvalidOperationException($"'{type.FullName}' is not a creatable asset type.");
        if (string.IsNullOrWhiteSpace(asset.Name))
            asset.Name = displayName;
        string path = AssetManager.VerifyAssetPath(asset, folder);
        if (File.Exists(path) || Directory.Exists(path))
            path = AssetManager.GetUniqueAssetPath(path);
        path = NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot: false);
        asset.FilePath = path;
        byte[] bytes = SerializeAsset(asset);

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Create Asset");
        Create("Create Asset", [new NativeAssetCreationFile(path, bytes, asset.ID)]);
        Engine.Assets.NotifyExternalAssetSaved(asset, newAsset: true);
        return path;
    }

    /// <summary>Serializes an asset to the exact bytes that a normal save writes.</summary>
    internal static byte[] SerializeAsset(XRAsset asset)
    {
        string temp = Path.Combine(Path.GetTempPath(), "xre-asset-" + Guid.NewGuid().ToString("N") + ".asset");
        try
        {
            asset.SerializeTo(temp, AssetManager.Serializer);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Records an explicit acceptance for every recovery-required operation with a readable
    /// manifest. Files stay where they are. Returns the count accepted.
    /// </summary>
    internal static int AcceptRecoveryRecords()
    {
        AssetCreationRecoveryStore? store = Engine.Assets?.CreationRecovery;
        if (store is null)
            return 0;
        int accepted = 0;
        foreach (AssetCreationRecoveryOperation operation in store.GetOperations())
        {
            if (operation.State != EAssetCreationRecoveryState.RecoveryRequired)
                continue;
            if (operation.Manifest is null)
            {
                Debug.LogWarning($"[AssetRecovery] Operation {operation.Id} has no readable manifest. Inspect '{operation.Directory}' by hand.");
                continue;
            }
            Debug.LogWarning($"[AssetRecovery] Accepting operation {operation.Id} as found: {operation.Problem} " +
                $"Paths: {string.Join(", ", operation.PublicPaths)}. Records: '{operation.Directory}'.");
            store.AcceptRecovery(operation, "Accepted from the native asset browser; files kept as found.");
            accepted++;
        }
        return accepted;
    }

    /// <summary>Removes every released operation that passes the ownership check. Returns the count removed.</summary>
    internal static int CleanReleased()
    {
        AssetCreationRecoveryStore? store = Engine.Assets?.CreationRecovery;
        if (store is null)
            return 0;
        int removed = 0;
        foreach (AssetCreationRecoveryOperation operation in store.GetOperations())
            if (operation.State == EAssetCreationRecoveryState.Released && store.RemoveReleased(operation))
                removed++;
        return removed;
    }

    /// <summary>
    /// Refuses an edit of a path that an unfinished creation protects, and releases published
    /// creation receipts for the path before a normal rename, move, or delete.
    /// </summary>
    internal static void AdmitPathEdit(string path, string reason)
    {
        AssetCreationRecoveryStore? store = Engine.Assets?.CreationRecovery;
        if (store is null)
            return;
        if (store.IsProtected(path))
            throw new IOException($"AssetRecovery.Protected: '{path}' belongs to an unfinished asset creation. Resolve its recovery record first.");
        store.ReleasePublishedFor(path, reason);
    }

    // A delayed watcher event for an unchanged journaled move must not rewrite metadata or reload.
    private static void SuppressDelayedWatcherEvents(AssetManager assets, AssetCreationRecoveryOperation operation)
    {
        foreach (string path in operation.PublicPaths)
            assets.NotifyExternalAssetPathWritten(path);
    }
}
