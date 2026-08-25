using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Runtime-owned asset lookup and path policy consumed by lower and feature YAML serializers.
/// Data owns this contract; Runtime.Core supplies the process implementation.
/// </summary>
public interface IAssetSerializationServices
{
    string AssetExtension { get; }

    string? GameAssetsPath { get; }

    string? EngineAssetsPath { get; }

    string? CurrentDeserializationPath { get; }

    IReadOnlyList<IYamlTypeConverter> YamlTypeConverters { get; }

    void EnsureYamlAssetRuntimeSupported(string? path = null);

    bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset);

    bool TryResolveAssetPathById(
        Guid assetId,
        string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath);

    bool TryCreatePortableAssetReference(
        string assetPath,
        [NotNullWhen(true)] out string? reference);

    XRAsset? LoadImmediate(string assetPath, Type assetType);

    bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset);

    bool TryHandleScalarAsset(
        IParser reader,
        Type expectedType,
        Scalar scalar,
        out object? value);
}
