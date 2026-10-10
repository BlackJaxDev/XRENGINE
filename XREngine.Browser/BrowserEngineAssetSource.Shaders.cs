using System.Text.Json;
using XREngine.Core.Files;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Browser;

public sealed partial class BrowserEngineAssetSource : IRuntimeAssetPreparationSource
{
    private readonly SemaphoreSlim _shaderLoadGate = new(1, 1);
    private readonly CancellationTokenSource _shaderLifetime = new();
    private readonly Dictionary<string, BrowserShaderArtifactReference> _shaderReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _sceneShaderIdentities = new(StringComparer.Ordinal);
    private string[] _startupShaderIdentities = [];
    private ShaderArtifactCatalogProvider? _shaderCatalogs;

    private void ReadShaderDelivery(JsonElement root)
    {
        foreach (BrowserShaderArtifactReference reference in _shaderArtifacts)
            _shaderReferences.Add(reference.Identity, reference);
        _startupShaderIdentities = [.. _shaderReferences.Keys];
        if (!root.TryGetProperty("shaderDelivery", out JsonElement delivery))
            return; // Legacy packages eagerly validate every globally declared module.
        if (!root.TryGetProperty("essentialRoots", out _) || !root.TryGetProperty("streamedRoots", out _)
            || delivery.ValueKind != JsonValueKind.Object || delivery.EnumerateObject().Count() != 3
            || !delivery.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int version) || version != 1
            || !delivery.TryGetProperty("startup", out JsonElement startup)
            || !delivery.TryGetProperty("scenes", out JsonElement scenes) || scenes.ValueKind != JsonValueKind.Array
            || scenes.GetArrayLength() > 256 || scenes.GetArrayLength() != StreamedRoots.Count)
            throw new InvalidDataException("AssetSource.ShaderDeliveryInvalid.");
        _startupShaderIdentities = ReadIdentities(startup);
        HashSet<string> required = new(_startupShaderIdentities, StringComparer.Ordinal);
        if (_materialVariants.Any(entry => !required.Contains(entry.DescriptorIdentity))
            || _pipelineArtifactIdentities.Values.Any(identity => !required.Contains(identity))
            || _computeArtifactIdentities.Values.Any(identity => !required.Contains(identity)))
            throw new InvalidDataException("AssetSource.GlobalShaderBindingNotEssential.");
        HashSet<string> covered = new(required, StringComparer.Ordinal);
        foreach (JsonElement scene in scenes.EnumerateArray())
        {
            if (scene.ValueKind != JsonValueKind.Object || scene.EnumerateObject().Count() != 2
                || !scene.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String
                || !scene.TryGetProperty("identities", out JsonElement identities)
                || pathValue.GetString() is not { } path || !StreamedRoots.Contains(path, StringComparer.Ordinal)
                || _sceneShaderIdentities.ContainsKey(path))
                throw new InvalidDataException("AssetSource.SceneShaderDeliveryInvalid.");
            string[] members = ReadIdentities(identities);
            _sceneShaderIdentities.Add(path, members);
            covered.UnionWith(members);
        }
        if (!covered.SetEquals(_shaderReferences.Keys))
            throw new InvalidDataException("AssetSource.ShaderDeliveryIncomplete.");
        foreach (BrowserShaderArtifactReference reference in _shaderArtifacts)
            if (_assets[reference.Descriptor].Dependencies.Count != 0 || _assets[reference.Source].Dependencies.Count != 0
                || required.Contains(reference.Identity) != IsEssential(reference.Descriptor)
                || required.Contains(reference.Identity) != IsEssential(reference.Source))
                throw new InvalidDataException("AssetSource.ShaderDeliveryEssentialMismatch.");

        string[] ReadIdentities(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > _shaderReferences.Count)
                throw new InvalidDataException("AssetSource.ShaderDeliveryIdentitiesInvalid.");
            HashSet<string> unique = new(StringComparer.Ordinal);
            foreach (JsonElement identity in value.EnumerateArray())
                if (identity.ValueKind != JsonValueKind.String || identity.GetString() is not { } text
                    || !_shaderReferences.ContainsKey(text) || !unique.Add(text))
                    throw new InvalidDataException("AssetSource.ShaderDeliveryIdentityMissingOrRepeated.");
            return [.. unique];
        }
    }

    /// <summary>Loads exact startup companions and returns the stable resolver installed for the session.</summary>
    public async Task<ShaderProgramArtifactCatalog> LoadShaderArtifactsAsync(CancellationToken cancellationToken = default)
    {
        await LoadShaderBatchAsync(_startupShaderIdentities, cancellationToken);
        RequireSession();
        return _shaderCatalogs!.Artifacts;
    }

    /// <summary>Admits all scene shaders before any of its dependencies or material callbacks hydrate.</summary>
    public async Task PrepareAssetAsync(string catalogPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSession();
        if (_sceneShaderIdentities.TryGetValue(catalogPath, out string[]? identities))
            await LoadShaderBatchAsync(identities, cancellationToken);
    }

    private async Task LoadShaderBatchAsync(string[] identities, CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shaderLifetime.Token);
        CancellationToken token = lifetime.Token;
        await _shaderLoadGate.WaitAsync(token);
        try
        {
            int session = RequireSession();
            token.ThrowIfCancellationRequested();
            _shaderCatalogs ??= new ShaderArtifactCatalogProvider(_shaderReferences.Keys,
                _materialVariants, _pipelineArtifactIdentities, _computeArtifactIdentities);
            // Snapshot references before asynchronous reads so retirement can clear
            // source bookkeeping without invalidating an in-flight enumerator.
            BrowserShaderArtifactReference[] pending = [.. identities
                .Where(identity => !_shaderCatalogs.Artifacts.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out _))
                .Select(identity => _shaderReferences[identity])];
            List<ShaderProgramArtifact> artifacts = new(pending.Length);
            foreach (BrowserShaderArtifactReference reference in pending)
            {
                byte[] source = await ReadAllBytesAsync(reference.Source, token);
                using BrowserAssetStagingLease sourceStaging = new(session, source.Length);
                using RuntimeAssetIntegration descriptor = await ReadForIntegrationAsync(reference.Descriptor, reference.Source, token);
                token.ThrowIfCancellationRequested();
                if (session != RequireSession()) throw new OperationCanceledException("AssetSource.StaleSession.");
                ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(descriptor.Payload, source);
                if (artifact.Identity != reference.Identity || artifact.Target != ShaderCompileTarget.WebGPUWgsl)
                    throw new InvalidDataException($"ShaderArtifact.IdentityMismatch: '{reference.Descriptor}'.");
                artifacts.Add(artifact);
            }
            token.ThrowIfCancellationRequested();
            if (session != RequireSession()) throw new OperationCanceledException("AssetSource.StaleSession.");
            // Publication is all-or-nothing. Once admitted, valid modules remain
            // source-owned even if later scene hydration fails: another root may
            // already borrow the same immutable programs.
            if (artifacts.Count != 0)
                _shaderCatalogs.Publish(artifacts, token);
        }
        finally
        {
            _shaderLoadGate.Release();
        }
    }

    private void RetireShaderDelivery()
    {
        _shaderLifetime.Cancel();
        _shaderCatalogs?.Dispose();
        _shaderReferences.Clear();
        _sceneShaderIdentities.Clear();
        _startupShaderIdentities = [];
    }
}
