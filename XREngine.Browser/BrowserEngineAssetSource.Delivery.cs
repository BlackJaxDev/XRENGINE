using System.Text.Json;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Browser;

public sealed partial class BrowserEngineAssetSource : IRuntimeAssetIntegrationSource
{
    private readonly Dictionary<string, List<BrowserAssetNativeEstimate>> _retainedNativeSources = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<DataSource, BrowserAssetNativeEstimate> _nativeSourceReferences = new();
    private long _retainedNativeBytes;
    private readonly HashSet<string> _essentialPaths = new(StringComparer.Ordinal);
    public IReadOnlyList<string> EssentialRoots { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> StreamedRoots { get; private set; } = Array.Empty<string>();

    /// <summary>Returns source-owned transfer, staging, hydration and lifetime estimates on demand.</summary>
    public string GetDeliverySnapshotJson() => BrowserEngineAssetImports.GetProgress(RequireSession());
    public bool IsEssential(string path) => _essentialPaths.Contains(Path.GetFullPath(path));

    /// <summary>Hydrates declared essential roots through the shared owner without activating scenes.</summary>
    public async Task PreloadEssentialAssetsAsync(CancellationToken cancellationToken = default)
    {
        HashSet<string> externallyConsumed = new(StringComparer.Ordinal);
        if (PublishedMetadataPath is { } metadata) externallyConsumed.Add(metadata);
        foreach (BrowserShaderArtifactReference shader in _shaderArtifacts)
        {
            externallyConsumed.Add(shader.Descriptor);
            externallyConsumed.Add(shader.Source);
        }
        // Snapshot this startup-only walk so cancellation/disposal can clear source
        // bookkeeping while an asynchronous dependency read is in flight.
        foreach (string path in _essentialPaths.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (externallyConsumed.Contains(path)) continue;
            await Engine.Assets.LoadFromRuntimeSourceAsync(path, typeof(XRAsset), cancellationToken: cancellationToken);
        }
    }

    private void ReadDeliveryRoots(JsonElement root)
    {
        static string[] ReadRoots(JsonElement value)
        {
            string[] paths = new string[value.GetArrayLength()];
            int index = 0;
            foreach (JsonElement path in value.EnumerateArray()) paths[index++] = path.GetString()!;
            return paths;
        }
        EssentialRoots = root.TryGetProperty("essentialRoots", out JsonElement essential)
            ? Array.AsReadOnly(ReadRoots(essential)) : Array.AsReadOnly(_assets.Keys.ToArray());
        StreamedRoots = root.TryGetProperty("streamedRoots", out JsonElement streamed)
            ? Array.AsReadOnly(ReadRoots(streamed)) : Array.Empty<string>();
        void Visit(string path)
        {
            if (!_essentialPaths.Add(path)) return;
            foreach (string dependency in _assets[path].Dependencies) Visit(dependency);
        }
        foreach (string path in EssentialRoots) Visit(path);
    }

    /// <summary>Leaves verified bytes in bounded JavaScript staging until global hydration admission is granted.</summary>
    public Task<RuntimeAssetIntegration> ReadForIntegrationAsync(string path, CancellationToken cancellationToken = default)
        => ReadForIntegrationAsync(path, string.Empty, cancellationToken);

    private async Task<RuntimeAssetIntegration> ReadForIntegrationAsync(string path, string companionPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        int session = RequireSession();
        int read = BrowserEngineAssetImports.BeginRead(session, path);
        int admission = 0;
        bool transferred = false;
        try
        {
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
            {
                BrowserEngineAssetImports.ReleaseRead(session, read);
                if (admission != 0) BrowserEngineAssetImports.FinishIntegration(session, admission);
            });
            int length = await BrowserEngineAssetImports.WaitReadAsync(session, read);
            cancellationToken.ThrowIfCancellationRequested();
            admission = BrowserEngineAssetImports.BeginIntegration(session, path, length, companionPath);
            // Activity notifications may synchronously invoke a cancellation handler
            // before the imported call returns its newly created ticket.
            cancellationToken.ThrowIfCancellationRequested();
            await BrowserEngineAssetImports.WaitIntegrationAsync(session, admission);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != RequireSession()) throw new OperationCanceledException("AssetSource.StaleSession.");
            if (length < 1 || length > 4 * 1024 * 1024)
                throw new InvalidDataException("AssetSource.PayloadBudgetExceeded.");
            byte[] payload = new byte[length];
            BrowserEngineAssetImports.CopyRead(session, read, payload);
            var result = new RuntimeAssetIntegration(payload, new BrowserAssetIntegrationAdmission(session, admission));
            transferred = true;
            return result;
        }
        finally
        {
            BrowserEngineAssetImports.ReleaseRead(session, read);
            if (!transferred && admission != 0) BrowserEngineAssetImports.FinishIntegration(session, admission);
        }
    }

    public void RetainAsset(string path, int serializedBytes, long managedAllocationBytes, ObjectCacheOwnership ownership)
    {
        List<BrowserAssetNativeEstimate> nativeSources = [];
        void Count(DataSource? data)
        {
            if (data is null || data.External) return;
            BrowserAssetNativeEstimate tracked = _nativeSourceReferences.GetValue(data, static _ => new BrowserAssetNativeEstimate());
            if (nativeSources.Contains(tracked)) return;
            if (tracked.References == 0) tracked.Bytes = data.Length;
            nativeSources.Add(tracked);
        }
        foreach (XRObjectBase value in ownership.Objects)
        {
            if (value is XRDataBuffer buffer) { Count(buffer.ClientSideSource); Count(buffer.GpuCompressedSource); }
            if (value is XRTexture2D texture)
                foreach (Mipmap2D mip in texture.Mipmaps) Count(mip.Data);
        }
        _retainedNativeSources.Add(path, nativeSources);
        foreach (BrowserAssetNativeEstimate tracked in nativeSources)
        {
            if (tracked.References++ == 0) _retainedNativeBytes += tracked.Bytes;
        }
        BrowserEngineAssetImports.Retain(RequireSession(), path, serializedBytes, ownership.Objects.Count, managedAllocationBytes, _retainedNativeBytes);
    }

    public void ReleaseAsset(string path)
    {
        if (_retainedNativeSources.Remove(path, out List<BrowserAssetNativeEstimate>? nativeSources))
            foreach (BrowserAssetNativeEstimate tracked in nativeSources)
            {
                if (--tracked.References == 0) _retainedNativeBytes -= tracked.Bytes;
            }
        if (_session != 0) BrowserEngineAssetImports.ReleaseAsset(_session, path, _retainedNativeBytes);
    }
}
