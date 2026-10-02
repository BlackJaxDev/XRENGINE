using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Fetch-backed engine asset catalog with immutable payloads and explicit async-only reads.</summary>
public sealed partial class BrowserEngineAssetSource : IRuntimeAssetSource, IRuntimeAssetCatalog, IDisposable
{
    private int _session;
    private readonly List<BrowserShaderArtifactReference> _shaderArtifacts = [];
    private readonly List<EngineMaterialVariantEntry> _materialVariants = [];
    private readonly Dictionary<string, string> _pipelineArtifactIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _computeArtifactIdentities = new(StringComparer.Ordinal);
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
    public string? DefaultUiFontPath { get; private set; }
    public string? PublishedMetadataPath { get; private set; }
    public string? PublishedMetadataFingerprint { get; private set; }

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
            source.ReadVerifiedWorldPackage(BrowserEngineAssetImports.GetVerifiedWorldPackage(source.RequireSession()));
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
        if (root.TryGetProperty("materialVariants", out JsonElement variants))
        {
            if (variants.ValueKind != JsonValueKind.Array || variants.GetArrayLength() > 256)
                throw new InvalidDataException("AssetSource.MaterialVariantBudgetExceeded.");
            HashSet<EngineMaterialVariantKey> keys = [];
            HashSet<string> identities = _shaderArtifacts.Select(static artifact => artifact.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (JsonElement variant in variants.EnumerateArray())
            {
                EngineMaterialVariantKey key = ShaderProgramArtifactReader.ReadMaterialVariantReferenceKey(variant);
                string identity = ShaderProgramArtifactCatalog.ValidateIdentity(variant.GetProperty("descriptorIdentity").GetString())
                    ?? throw new InvalidDataException("AssetSource.MaterialVariantIdentityMissing.");
                if (!identities.Contains(identity))
                    throw new InvalidDataException($"AssetSource.MaterialVariantArtifactMissing: '{identity}'.");
                if (!keys.Add(key))
                    throw new InvalidDataException($"AssetSource.MaterialVariantDuplicateKey: '{key}'.");
                _materialVariants.Add(new EngineMaterialVariantEntry(key, identity));
            }
        }
        if (root.TryGetProperty("pipelineArtifacts", out JsonElement pipelines))
            ReadPipelineArtifacts(pipelines);
        if (root.TryGetProperty("computeArtifacts", out JsonElement computes))
            ReadComputeArtifacts(computes);
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
        if (root.TryGetProperty("publishedMetadata", out JsonElement metadataReference))
        {
            string? metadataPath = metadataReference.ValueKind == JsonValueKind.String
                ? metadataReference.GetString() : null;
            if (metadataReference.ValueKind != JsonValueKind.String
                || metadataPath != "/engine/Metadata/AotRuntimeMetadata.bin"
                || !_assets.TryGetValue(metadataPath, out RuntimeAssetCatalogEntry? metadataEntry)
                || metadataEntry.Encoding != RuntimeAssetEncoding.CookedBinary
                || metadataEntry.TypeName != typeof(AotRuntimeMetadata).AssemblyQualifiedName
                    && metadataEntry.TypeName != "XREngine.AotRuntimeMetadata, XREngine.Data"
                || metadataEntry.Dependencies.Count != 0)
                throw new InvalidDataException("AssetSource.PublishedMetadataInvalid: expected a standalone published type payload.");
            PublishedMetadataPath = metadataPath;
            foreach (JsonElement item in root.GetProperty("assets").EnumerateArray())
            {
                if (item.GetProperty("path").GetString() != metadataPath)
                    continue;
                PublishedMetadataFingerprint = item.GetProperty("hash").GetString();
                break;
            }
        }
        if (root.TryGetProperty("defaultUiFont", out JsonElement fontReference))
        {
            if (fontReference.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("AssetSource.DefaultUiFontInvalid: expected a cooked font path.");
            string path = fontReference.GetString()!;
            if (!path.StartsWith("/engine/Fonts/", StringComparison.Ordinal)
                || !_assets.TryGetValue(path, out RuntimeAssetCatalogEntry? entry)
                || entry.Encoding != RuntimeAssetEncoding.CookedBinary
                || entry.TypeName != typeof(FontGlyphSet).AssemblyQualifiedName
                || entry.Dependencies.Count != 0)
                throw new InvalidDataException("AssetSource.DefaultUiFontInvalid: expected a standalone cooked engine FontGlyphSet.");
            DefaultUiFontPath = path;
        }
        ReadDeliveryRoots(root);
    }

    private void ReadPipelineArtifacts(JsonElement pipelines)
    {
        if (pipelines.ValueKind != JsonValueKind.Array ||
            pipelines.GetArrayLength() > WebPipelineArtifactCatalog.MaximumEntries)
            throw new InvalidDataException("AssetSource.PipelineArtifactBudgetExceeded.");
        HashSet<string> identities = _shaderArtifacts.Select(static artifact => artifact.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement pipeline in pipelines.EnumerateArray())
        {
            if (pipeline.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("AssetSource.PipelineArtifactInvalid: expected pass, descriptorIdentity, and optional scope.");
            bool hasScope = pipeline.TryGetProperty("scope", out JsonElement scope);
            if (pipeline.EnumerateObject().Count() != (hasScope ? 3 : 2)
                || hasScope && scope.ValueKind != JsonValueKind.String
                || !pipeline.TryGetProperty("pass", out JsonElement pass) || pass.ValueKind != JsonValueKind.String
                || !pipeline.TryGetProperty("descriptorIdentity", out JsonElement identityValue) || identityValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("AssetSource.PipelineArtifactInvalid: expected pass, descriptorIdentity, and optional scope.");
            string bindingKey = WebPipelineArtifactCatalog.GetBindingKey(hasScope ? scope.GetString() : null, pass.GetString()!);
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(identityValue.GetString())
                ?? throw new InvalidDataException("AssetSource.PipelineArtifactIdentityMissing.");
            if (!identities.Contains(identity))
                throw new InvalidDataException($"AssetSource.PipelineArtifactMissing: '{identity}'.");
            if (!_pipelineArtifactIdentities.TryAdd(bindingKey, identity))
                throw new InvalidDataException($"AssetSource.PipelineArtifactDuplicateBinding: '{bindingKey}'.");
        }
    }

    private void ReadComputeArtifacts(JsonElement computes)
    {
        if (computes.ValueKind != JsonValueKind.Array || computes.GetArrayLength() > 4)
            throw new InvalidDataException("AssetSource.ComputeArtifactBudgetExceeded.");
        HashSet<string> identities = _shaderArtifacts.Select(static artifact => artifact.Identity).ToHashSet(StringComparer.Ordinal);
        foreach (JsonElement compute in computes.EnumerateArray())
        {
            if (compute.ValueKind != JsonValueKind.Object || compute.EnumerateObject().Count() != 2 ||
                !compute.TryGetProperty("kernel", out JsonElement kernel) || kernel.ValueKind != JsonValueKind.String ||
                !WebComputeArtifactCatalog.IsSupportedKernel(kernel.GetString()) ||
                !compute.TryGetProperty("descriptorIdentity", out JsonElement identityValue) || identityValue.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("AssetSource.ComputeArtifactInvalid: expected a known kernel and descriptorIdentity.");
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(identityValue.GetString())
                ?? throw new InvalidDataException("AssetSource.ComputeArtifactIdentityMissing.");
            if (!identities.Contains(identity))
                throw new InvalidDataException($"AssetSource.ComputeArtifactMissing: '{identity}'.");
            if (!_computeArtifactIdentities.TryAdd(kernel.GetString()!, identity))
                throw new InvalidDataException($"AssetSource.ComputeArtifactDuplicateKernel: '{kernel.GetString()}'.");
        }
    }

    /// <summary>Loads exact cooked shader companions before world activation, independent of authored source text.</summary>
    public async Task<ShaderProgramArtifactCatalog> LoadShaderArtifactsAsync(CancellationToken cancellationToken = default)
    {
        int session = RequireSession();
        List<ShaderProgramArtifact> artifacts = new(_shaderArtifacts.Count);
        foreach (BrowserShaderArtifactReference reference in _shaderArtifacts)
        {
            byte[] source = await ReadAllBytesAsync(reference.Source, cancellationToken);
            using BrowserAssetStagingLease sourceStaging = new(session, source.Length);
            using RuntimeAssetIntegration descriptor = await ReadForIntegrationAsync(reference.Descriptor, reference.Source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session != RequireSession()) throw new OperationCanceledException("AssetSource.StaleSession.");
            ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(descriptor.Payload, source);
            if (!string.Equals(artifact.Identity, reference.Identity, StringComparison.Ordinal))
                throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{reference.Descriptor}'.");
            artifacts.Add(artifact);
        }
        return new ShaderProgramArtifactCatalog(artifacts);
    }

    /// <summary>Hydrates the explicitly packaged default font before UI can activate.</summary>
    public async Task<FontGlyphSet?> LoadDefaultUiFontAsync(CancellationToken cancellationToken = default)
    {
        RequireSession();
        if (DefaultUiFontPath is not { } path)
            return null;
        FontGlyphSet font = await Engine.Assets.LoadFromRuntimeSourceAsync(path,
            typeof(FontGlyphSet), cancellationToken: cancellationToken) as FontGlyphSet
            ?? throw new InvalidDataException("AssetSource.DefaultUiFontInvalid: cooked font did not hydrate.");
        if (font.AtlasType != EFontAtlasType.Bitmap || font.Glyphs is not { Count: > 0 }
            || font.Atlas is not { Mipmaps.Length: > 0 })
            throw new NotSupportedException("AssetSource.DefaultUiFontUnsupported: expected a bitmap atlas with glyphs and mips.");
        return font;
    }

    /// <summary>Resolves exact hash-owned variant declarations against already verified shader artifacts.</summary>
    public EngineMaterialVariantCatalog LoadEngineMaterialVariants(ShaderProgramArtifactCatalog artifacts)
    {
        RequireSession();
        return new EngineMaterialVariantCatalog(_materialVariants, artifacts);
    }

    /// <summary>Resolves exact pass declarations against verified modules without inferring from names or source paths.</summary>
    public WebPipelineArtifactCatalog LoadPipelineArtifacts(ShaderProgramArtifactCatalog artifacts)
    {
        RequireSession();
        return new WebPipelineArtifactCatalog(_pipelineArtifactIdentities, artifacts);
    }

    /// <summary>Resolves declared engine compute kernels against verified hash-owned modules.</summary>
    public WebComputeArtifactCatalog LoadComputeArtifacts(ShaderProgramArtifactCatalog artifacts)
    {
        RequireSession();
        return new WebComputeArtifactCatalog(_computeArtifactIdentities, artifacts);
    }

    /// <summary>Retains the existing exact-tonemap lookup for compatible diagnostic callers.</summary>
    public ShaderProgramArtifact? LoadTonemapArtifact(ShaderProgramArtifactCatalog artifacts)
    {
        RequireSession();
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!_pipelineArtifactIdentities.TryGetValue("tonemap", out string? identity))
            return null;
        if (!artifacts.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
            throw new InvalidDataException($"AssetSource.PipelineArtifactMissing: '{identity}' is not a verified WebGPUWgsl artifact.");
        if (!string.Equals(artifact.Pass, "tonemap", StringComparison.Ordinal))
            throw new InvalidDataException($"AssetSource.PipelineArtifactPassMismatch: '{identity}' declares '{artifact.Pass}'.");
        return artifact;
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
        _materialVariants.Clear();
        _pipelineArtifactIdentities.Clear();
        _computeArtifactIdentities.Clear();
        _essentialPaths.Clear();
        _retainedNativeSources.Clear();
        _nativeSourceReferences.Clear();
        _retainedNativeBytes = 0;
        EssentialRoots = Array.Empty<string>();
        StreamedRoots = Array.Empty<string>();
        DefaultUiFontPath = null;
        PublishedMetadataPath = null;
        PublishedMetadataFingerprint = null;
        _verifiedWorldIdentity = null;
        _verifiedNativeWorldPath = null;
    }
}
