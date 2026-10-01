using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Browser;

/// <summary>Fetch-backed engine asset catalog with immutable payloads and explicit async-only reads.</summary>
public sealed class BrowserEngineAssetSource : IRuntimeAssetSource, IRuntimeAssetCatalog, IDisposable
{
    private int _session;
    private readonly List<BrowserShaderArtifactReference> _shaderArtifacts = [];
    private readonly Dictionary<string, RuntimeAssetCatalogEntry> _assets = new(StringComparer.Ordinal);
    private BrowserEngineAssetSource(int session) => _session = session;
    public bool SupportsSynchronousReads => false;
    public bool IsAccelerated => false;
    public string Status => _session != 0 ? "Hash-verified browser fetch catalog." : "Browser asset source disposed.";
    public IAssetFileSystem? FileSystem => null;
    public string EngineAssetsRoot => "/engine";
    public string GameAssetsRoot => "/game";
    public string StartupWorldPath { get; private set; } = string.Empty;
    public string? StartupSettingsPath { get; private set; }

    /// <summary>Validates the complete manifest before exposing asset identities to the engine.</summary>
    public static async Task<BrowserEngineAssetSource> OpenAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new BrowserEngineAssetSource(BrowserEngineAssetImports.Create(manifestUrl));
        try
        {
            using CancellationTokenRegistration cancellation = cancellationToken.Register(source.Dispose);
            await BrowserEngineAssetImports.OpenAsync(source.RequireSession());
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadManifest(BrowserEngineAssetImports.GetManifest(source.RequireSession()));
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void ReadManifest(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        StartupWorldPath = root.GetProperty("startupWorld").GetString()!;
        StartupSettingsPath = root.TryGetProperty("startupSettings", out JsonElement settings) && settings.ValueKind == JsonValueKind.String
            ? settings.GetString() : null;
        if (root.TryGetProperty("shaderArtifacts", out JsonElement shaders) && shaders.ValueKind == JsonValueKind.Array)
            foreach (JsonElement shader in shaders.EnumerateArray())
                _shaderArtifacts.Add(new BrowserShaderArtifactReference(shader.GetProperty("identity").GetString()!,
                    shader.GetProperty("descriptor").GetString()!, shader.GetProperty("source").GetString()!));
        foreach (JsonElement item in root.GetProperty("assets").EnumerateArray())
        {
            string path = item.GetProperty("path").GetString()!;
            string type = item.GetProperty("type").GetString()!;
            RuntimeAssetEncoding encoding = item.GetProperty("encoding").GetString() switch
            {
                "cooked-binary" => RuntimeAssetEncoding.CookedBinary,
                "yaml" => RuntimeAssetEncoding.Yaml,
                "utf8-text" => RuntimeAssetEncoding.Utf8Text,
                _ => throw new InvalidDataException($"AssetSource.EncodingUnsupported: '{path}'."),
            };
            JsonElement dependencies = item.GetProperty("dependencies");
            string[] paths = new string[dependencies.GetArrayLength()];
            int index = 0;
            foreach (JsonElement dependency in dependencies.EnumerateArray())
                paths[index++] = dependency.GetString()!;
            _assets.Add(path, new RuntimeAssetCatalogEntry(path, type, encoding, Array.AsReadOnly(paths)));
        }
    }

    /// <summary>Loads exact cooked shader companions before world activation, independent of authored source text.</summary>
    public async Task<ShaderProgramArtifactCatalog> LoadShaderArtifactsAsync(CancellationToken cancellationToken = default)
    {
        int session = RequireSession();
        List<ShaderProgramArtifact> artifacts = new(_shaderArtifacts.Count);
        foreach (BrowserShaderArtifactReference reference in _shaderArtifacts)
        {
            byte[] descriptor = await ReadAllBytesAsync(reference.Descriptor, cancellationToken);
            byte[] source = await ReadAllBytesAsync(reference.Source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != RequireSession()) throw new OperationCanceledException("AssetSource.StaleSession.");
            ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(descriptor, source);
            if (!string.Equals(artifact.Identity, reference.Identity, StringComparison.Ordinal))
                throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{reference.Descriptor}'.");
            artifacts.Add(artifact);
        }
        return new ShaderProgramArtifactCatalog(artifacts);
    }

    public bool Exists(string path) => TryGetAsset(path, out _);
    public bool TryGetAsset(string path, [NotNullWhen(true)] out RuntimeAssetCatalogEntry? entry)
    {
        RequireSession();
        return _assets.TryGetValue(Path.GetFullPath(path), out entry);
    }

    public async ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        => new MemoryStream(await ReadAllBytesAsync(path, cancellationToken), writable: false);

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int session = RequireSession();
        int ticket = BrowserEngineAssetImports.BeginRead(session, Path.GetFullPath(path));
        try
        {
            using CancellationTokenRegistration cancellation = cancellationToken.Register(
                () => BrowserEngineAssetImports.ReleaseRead(session, ticket));
            int length = await BrowserEngineAssetImports.WaitReadAsync(session, ticket);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != RequireSession())
                throw new InvalidOperationException("AssetSource.StaleSession: asset source was replaced during fetch.");
            if (length < 1 || length > 4 * 1024 * 1024)
                throw new InvalidDataException("AssetSource.PayloadBudgetExceeded: returned payload exceeds the declared bound.");
            byte[] bytes = new byte[length];
            BrowserEngineAssetImports.CopyRead(session, ticket, bytes);
            return bytes;
        }
        finally
        {
            BrowserEngineAssetImports.ReleaseRead(session, ticket);
        }
    }

    public async Task<byte[]> ReadRangeAsync(string path, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] bytes = await ReadAllBytesAsync(path, cancellationToken);
        if (offset > bytes.LongLength || length > bytes.LongLength - offset)
            throw new ArgumentOutOfRangeException(nameof(length), "AssetSource.RangeOutOfBounds.");
        return bytes.AsSpan(checked((int)offset), length).ToArray();
    }

    public byte[] ReadAllBytes(string path) => throw SynchronousRead(path);
    public byte[] ReadRange(string path, long offset, int length) => throw SynchronousRead(path);
    public unsafe bool TryReadInto(string path, long offset, int length, void* destination, CancellationToken cancellationToken = default)
        => throw SynchronousRead(path);
    public unsafe bool TryReadFileInto(string path, void* destination, int destinationSize, CancellationToken cancellationToken = default)
        => throw SynchronousRead(path);
    public IAssetReadBatch CreateBatch() => new BrowserAssetReadBatch(this);

    private static NotSupportedException SynchronousRead(string path)
        => new($"AssetSource.AsyncReadRequired: browser fetch cannot synchronously read '{path}'.");
    private int RequireSession() => _session != 0 ? _session
        : throw new ObjectDisposedException(nameof(BrowserEngineAssetSource));
    public void Dispose()
    {
        int session = Interlocked.Exchange(ref _session, 0);
        if (session != 0) BrowserEngineAssetImports.Dispose(session);
        _assets.Clear();
        _shaderArtifacts.Clear();
    }
}
