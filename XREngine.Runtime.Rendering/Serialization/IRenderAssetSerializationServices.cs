using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace XREngine.Rendering;

/// <summary>
/// Host-owned asset lookup operations required by rendering asset YAML converters.
/// </summary>
public interface IRenderAssetSerializationServices
{
    void EnsureYamlAssetRuntimeSupported(string? path = null);

    bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset);

    bool TryResolveAssetPathById(
        Guid assetId,
        string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath);

    XRAsset? LoadImmediate(string assetPath, Type assetType);

    bool TryHandleScalarAsset(
        IParser reader,
        Type expectedType,
        Scalar scalar,
        out object? value);
}
