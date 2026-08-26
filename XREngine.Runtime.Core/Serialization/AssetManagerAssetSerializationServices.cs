using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>Adapts the Runtime.Core asset owner to the lower serialization contract.</summary>
public sealed class AssetManagerAssetSerializationServices(AssetManager assets) : IAssetSerializationServices
{
    private readonly AssetManager _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    public string AssetExtension => AssetManager.AssetExtension;
    public string? GameAssetsPath => _assets.GameAssetsPath;
    public string? EngineAssetsPath => _assets.EngineAssetsPath;
    public string? CurrentDeserializationPath => AssetDeserializationContext.CurrentFilePath;
    public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => AssetManager.YamlTypeConverters;

    public void EnsureYamlAssetRuntimeSupported(string? path = null)
        => AssetManager.EnsureYamlAssetRuntimeSupported(path);

    public bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset)
        => _assets.TryGetAssetByID(assetId, out asset);

    public bool TryResolveAssetPathById(
        Guid assetId,
        string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath)
        => _assets.TryResolveAssetPathById(assetId, referenceAssetPath, out assetPath);

    public bool TryCreatePortableAssetReference(
        string assetPath,
        [NotNullWhen(true)] out string? reference)
    {
        if (AssetReferencePath.TryCreate(
            _assets.GameAssetsPath,
            assetPath,
            AssetReferencePath.GamePrefix,
            out reference))
        {
            return true;
        }

        return AssetReferencePath.TryCreate(
            _assets.EngineAssetsPath,
            assetPath,
            AssetReferencePath.EnginePrefix,
            out reference);
    }

    public XRAsset? LoadImmediate(string assetPath, Type assetType)
        => _assets.LoadImmediate(assetPath, assetType);

    public bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset)
        => DeferredAssetReferenceContext.TryDeferAssetLoad(assetPath, assetType, out asset);

    public bool TryHandleScalarAsset(
        IParser reader,
        Type expectedType,
        Scalar scalar,
        out object? value)
        => XRAssetDeserializer.TryHandleScalarXRAsset(reader, expectedType, scalar, out value);
}
