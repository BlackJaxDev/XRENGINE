using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Runtime.Core-owned decorator for the lower asset serialization service contract.
/// The concrete asset catalog is supplied by composition and is not coupled to a facade manager.
/// </summary>
public class RuntimeAssetSerializationServices(IAssetSerializationServices inner)
    : IAssetSerializationServices
{
    private readonly IAssetSerializationServices _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    public string AssetExtension => _inner.AssetExtension;
    public string? GameAssetsPath => _inner.GameAssetsPath;
    public string? EngineAssetsPath => _inner.EngineAssetsPath;
    public string? CurrentDeserializationPath => _inner.CurrentDeserializationPath;
    public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => _inner.YamlTypeConverters;

    public void EnsureYamlAssetRuntimeSupported(string? path = null)
        => _inner.EnsureYamlAssetRuntimeSupported(path);

    public bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset)
        => _inner.TryGetAssetById(assetId, out asset);

    public bool TryResolveAssetPathById(
        Guid assetId,
        string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath)
        => _inner.TryResolveAssetPathById(assetId, referenceAssetPath, out assetPath);

    public bool TryCreatePortableAssetReference(
        string assetPath,
        [NotNullWhen(true)] out string? reference)
        => _inner.TryCreatePortableAssetReference(assetPath, out reference);

    public XRAsset? LoadImmediate(string assetPath, Type assetType)
        => _inner.LoadImmediate(assetPath, assetType);

    public bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset)
        => _inner.TryDeferAssetLoad(assetPath, assetType, out asset);

    public bool TryHandleScalarAsset(
        IParser reader,
        Type expectedType,
        Scalar scalar,
        out object? value)
        => _inner.TryHandleScalarAsset(reader, expectedType, scalar, out value);
}
