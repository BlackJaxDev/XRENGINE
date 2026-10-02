using System.Text;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Serialization;

namespace XREngine;

public partial class AssetManager
{
    /// <summary>Whether this owner loads virtual packaged identities rather than host files.</summary>
    internal bool UsesRuntimeAssetCatalog => _runtimeCatalogOwner || _runtimeAssetSource is IRuntimeAssetCatalog;

    /// <summary>Binds a replacement catalog after the previous world's assets have been released.</summary>
    public void BindRuntimeSource(IRuntimeAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is not IRuntimeAssetCatalog catalog)
            throw new ArgumentException("AssetSource.CatalogRequired: replacement sources must expose runtime identities.", nameof(source));
        if (!string.Equals(Path.GetFullPath(catalog.EngineAssetsRoot), EngineAssetsPath, StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(catalog.GameAssetsRoot), GameAssetsPath, StringComparison.Ordinal))
            throw new InvalidOperationException("AssetSource.RootMismatch: a runtime source cannot change this asset owner's virtual roots.");
        lock (_runtimePublicationGate)
        {
            if (_runtimeSourceTeardown || _runtimeSourceUnbinding)
                throw new InvalidOperationException("AssetSource.TeardownPending: wait for the previous content owner to finish teardown.");
            if (ReferenceEquals(_runtimeAssetSource, source))
                return;
            if (_runtimeSourceObjects.Count != 0)
                throw new InvalidOperationException("AssetSource.TeardownPending: the previous content owner still owns engine objects.");
            if (_runtimeAssetSource is not null)
                throw new InvalidOperationException("AssetSource.AlreadyBound: unload the current world before replacing its content owner.");
            _runtimeAssetSource = source;
            _runtimeSourceLifetime.Dispose();
            _runtimeSourceLifetime = new CancellationTokenSource();
            Interlocked.Increment(ref _runtimeSourceEpoch);
        }
    }

    /// <summary>Invalidates pending reads and releases cached assets after their world has stopped.</summary>
    public void UnbindRuntimeSource(IRuntimeAssetSource source)
    {
        lock (_runtimePublicationGate)
        {
            if (!ReferenceEquals(_runtimeAssetSource, source) || _runtimeSourceUnbinding || _runtimeSourceDisposing)
                return;
            _runtimeSourceUnbinding = true;
            Interlocked.Increment(ref _runtimeSourceEpoch);
            _runtimeSourceTeardown = true;
        }
        // Keep the source bound if destruction fails so shutdown can retry the exact
        // ownership ledger. Cancellation callbacks can reenter this method, but only
        // the outer unbind may release objects or clear the binding.
        try
        {
            try { _runtimeSourceLifetime.Cancel(); }
            finally
            {
                lock (_runtimePublicationGate)
                    DisposeRuntimeSourceObjects();
            }
            lock (_runtimePublicationGate)
            {
                _runtimeAssetSource = null;
                _runtimeSourceTeardown = false;
            }
        }
        finally
        {
            lock (_runtimePublicationGate)
                _runtimeSourceUnbinding = false;
        }
    }

    /// <summary>Loads a catalog asset and its dependencies without scheduling a blocking file job.</summary>
    public async Task<XRAsset?> LoadFromRuntimeSourceAsync(
        string path,
        Type expectedType,
        Action<AssetLoadProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        IRuntimeAssetSource? source = _runtimeAssetSource;
        if (source is not IRuntimeAssetCatalog catalog)
            throw new NotSupportedException("AssetSource.CatalogUnavailable: the installed asset source has no runtime catalog.");

        int epoch = Volatile.Read(ref _runtimeSourceEpoch);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtimeSourceLifetime.Token);
        using IDisposable progress = AssetLoadProgressContext.Begin(path, progressCallback);
        return await LoadCatalogAssetAsync(Path.GetFullPath(path), expectedType, source, catalog, epoch,
            new HashSet<string>(StringComparer.Ordinal), lifetime.Token).ConfigureAwait(false);
    }

    private async Task<XRAsset> LoadCatalogAssetAsync(
        string path,
        Type expectedType,
        IRuntimeAssetSource source,
        IRuntimeAssetCatalog catalog,
        int epoch,
        HashSet<string> ancestors,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureRuntimeSourceCurrent(source, epoch);
        if (!catalog.TryGetAsset(path, out RuntimeAssetCatalogEntry? entry))
            throw new FileNotFoundException($"AssetSource.NotPackaged: '{path}' is not in the runtime content catalog.", path);
        if (TryGetAssetByPath(path, out XRAsset? cached))
            return RequireRuntimeAssetType(path, cached, expectedType);
        if (ancestors.Count >= 32)
            throw new InvalidDataException($"AssetSource.DependencyDepthExceeded: '{path}' exceeds the runtime graph bound.");
        if (!ancestors.Add(path))
            throw new InvalidDataException($"AssetSource.DependencyCycle: '{path}' has a cyclic external dependency. Cook the cycle into one asset graph.");

        try
        {
            Type type = AotRuntimeMetadataStore.ResolveType(entry.TypeName)
                ?? throw new InvalidDataException($"AssetSource.TypeNotRegistered: '{entry.TypeName}' for '{path}'.");
            if (!typeof(XRAsset).IsAssignableFrom(type) || !expectedType.IsAssignableFrom(type))
                throw new InvalidDataException($"AssetSource.TypeMismatch: '{path}' declares '{type}' but requires '{expectedType}'.");

            foreach (string dependency in entry.Dependencies)
                await LoadCatalogAssetAsync(Path.GetFullPath(dependency), typeof(XRAsset), source, catalog, epoch,
                    ancestors, cancellationToken).ConfigureAwait(false);

            byte[] payload = await source.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRuntimeSourceCurrent(source, epoch);
            // Another asynchronous request can have completed this identity while bytes were fetched.
            if (TryGetAssetByPath(path, out cached))
                return RequireRuntimeAssetType(path, cached, expectedType);

            using IDisposable scope = AssetDeserializationContext.Push(path);
            // Deserialization owns new allocations, including replaced constructor defaults.
            // Separate catalog dependencies were loaded before this synchronous batch and
            // remain borrowed references until the entire source is released.
            using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
            XRAsset? asset;
            switch (entry.Encoding)
            {
                case RuntimeAssetEncoding.CookedBinary:
                    using (CookedBinaryReadBudget.BeginRuntimeCatalogRead())
                        asset = CookedAssetReader.LoadAsset(payload, type) as XRAsset;
                    break;
                case RuntimeAssetEncoding.Yaml:
                    EnsureYamlAssetRuntimeSupported(path);
                    ResetYamlReadContext();
                    using (var reader = new StringReader(new UTF8Encoding(false, true).GetString(payload)))
                        asset = Deserializer.Deserialize(reader, type) as XRAsset;
                    break;
                case RuntimeAssetEncoding.Utf8Text:
                    if (type != typeof(TextFile))
                        throw new InvalidDataException($"AssetSource.TextTypeMismatch: '{path}' must declare TextFile; derived assets require a cooked object payload.");
                    asset = new TextFile { Text = new UTF8Encoding(false, true).GetString(payload) };
                    break;
                default:
                    throw new NotSupportedException($"AssetSource.EncodingUnsupported: '{path}' uses '{entry.Encoding}'.");
            }

            if (asset is null)
                throw new InvalidDataException($"AssetSource.DeserializeFailed: '{path}' did not produce '{type}'.");
            ObjectCacheOwnership? ownership = null;
            try
            {
                lock (_runtimePublicationGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureRuntimeSourceCurrent(source, epoch);
                    if (TryGetAssetByPath(path, out cached))
                    {
                        return RequireRuntimeAssetType(path, cached, expectedType);
                    }
                    RequireRuntimeAssetType(path, asset, expectedType);
                    if (asset.IsDestroyed)
                        throw new InvalidDataException($"AssetSource.DestroyedRoot: '{path}' produced an already destroyed asset.");
                    if (asset.ID == Guid.Empty)
                        throw new InvalidDataException($"AssetSource.EmptyIdentity: '{path}' has no stable asset identity.");
                    if (LoadedAssetsByIDInternal.TryGetValue(asset.ID, out XRAsset? owner) && !ReferenceEquals(owner, asset))
                        throw new InvalidDataException($"AssetSource.DuplicateIdentity: '{path}' and '{owner.FilePath}' declare the same asset ID '{asset.ID}'.");
                    ownership = publication.CompleteWithOwnership();
                    _runtimeSourceObjects.Add(ownership);
                    _runtimeSourceAssets.Add(asset);
                    PostLoaded(path, asset, ownership);
                    EnsureRuntimeSourceCurrent(source, epoch);
                    return asset;
                }
            }
            catch (Exception error)
            {
                lock (_runtimePublicationGate)
                {
                    _runtimeSourceAssets.Remove(asset);
                    asset.PropertyChanged -= AssetPropertyChanged;
                    LoadedAssetsByIDInternal.TryRemove(new KeyValuePair<Guid, XRAsset>(asset.ID, asset));
                    LoadedAssetsByPathInternal.TryRemove(new KeyValuePair<string, XRAsset>(path, asset));
                    DirtyAssets.TryRemove(new KeyValuePair<Guid, XRAsset>(asset.ID, asset));
                    // An uncommitted scope aborts every allocation on disposal. A failure
                    // after publication must release the whole ledger, not just the root asset.
                    if (ownership is not null)
                    {
                        bool previousDisposing = _runtimeSourceDisposing;
                        _runtimeSourceDisposing = true;
                        try
                        {
                            ownership.Dispose();
                            _runtimeSourceObjects.Remove(ownership);
                        }
                        catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
                        finally { _runtimeSourceDisposing = previousDisposing; }
                    }
                }
                throw;
            }
        }
        finally
        {
            ancestors.Remove(path);
        }
    }

    private void DisposeRuntimeSourceObjects()
    {
        if (_runtimeSourceDisposing)
            return;
        _runtimeSourceDisposing = true;
        try
        {
            // Catalog references may borrow an already cached desktop asset. Only roots
            // materialized by this source lose cache registration and property subscriptions.
            foreach (XRAsset asset in _runtimeSourceAssets)
                asset.PropertyChanged -= AssetPropertyChanged;
            foreach (var pair in LoadedAssetsByIDInternal)
                if (_runtimeSourceAssets.Contains(pair.Value))
                    LoadedAssetsByIDInternal.TryRemove(pair);
            foreach (var pair in LoadedAssetsByPathInternal)
                if (_runtimeSourceAssets.Contains(pair.Value))
                    LoadedAssetsByPathInternal.TryRemove(pair);
            foreach (var pair in LoadedAssetsByOriginalPathInternal)
                if (_runtimeSourceAssets.Contains(pair.Value))
                    LoadedAssetsByOriginalPathInternal.TryRemove(pair);
            foreach (var pair in DirtyAssets)
                if (_runtimeSourceAssets.Contains(pair.Value))
                    DirtyAssets.TryRemove(pair);
            _runtimeSourceAssets.Clear();

            List<Exception>? failures = null;
            bool hadRuntimeOwnership = _runtimeSourceObjects.Count != 0;
            for (int index = _runtimeSourceObjects.Count - 1; index >= 0; index--)
            {
                try
                {
                    _runtimeSourceObjects[index].Dispose();
                    _runtimeSourceObjects.RemoveAt(index);
                }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (hadRuntimeOwnership)
            {
                try { XRObjectBase.ProcessPendingDestructions(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
            if (failures is not null)
                throw new AggregateException("AssetSource.TeardownFailed: source-owned objects could not all be released.", failures);
        }
        finally
        {
            _runtimeSourceDisposing = false;
        }
    }

    private XRAsset RequireCachedRuntimeAsset(string path, Type expectedType)
    {
        path = Path.GetFullPath(path);
        if (_runtimeAssetSource is IRuntimeAssetCatalog catalog && !catalog.TryGetAsset(path, out _))
            throw new FileNotFoundException($"AssetSource.NotPackaged: '{path}' is not in the runtime content catalog.", path);
        if (TryGetAssetByPath(path, out XRAsset? asset))
            return RequireRuntimeAssetType(path, asset, expectedType);
        throw new NotSupportedException($"AssetSource.AsyncReadRequired: '{path}' must be loaded asynchronously before synchronous reference resolution.");
    }

    private void EnsureRuntimeSourceCurrent(IRuntimeAssetSource source, int epoch)
    {
        if (_runtimeSourceTeardown || !ReferenceEquals(_runtimeAssetSource, source)
            || epoch != Volatile.Read(ref _runtimeSourceEpoch))
            throw new OperationCanceledException("AssetSource.StaleSession: the world content owner was replaced.");
    }

    private static XRAsset RequireRuntimeAssetType(string path, XRAsset asset, Type expectedType)
        => expectedType.IsInstanceOfType(asset) ? asset
            : throw new InvalidDataException($"AssetSource.TypeMismatch: '{path}' contains '{asset.GetType()}' but requires '{expectedType}'.");
}
