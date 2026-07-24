using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using XREngine.Rendering;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace XREngine;

/// <summary>
/// Connects rendering-owned YAML converters to facade-owned asset services.
/// </summary>
internal sealed class AssetManagerRenderAssetSerializationServices : IRenderAssetSerializationServices
{
    public static AssetManagerRenderAssetSerializationServices Instance { get; } = new();

    private AssetManagerRenderAssetSerializationServices()
    {
    }

    public void EnsureYamlAssetRuntimeSupported(string? path)
        => AssetManager.EnsureYamlAssetRuntimeSupported(path);

    public bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset)
        => Engine.Assets.TryGetAssetByID(assetId, out asset);

    public bool TryResolveAssetPathById(
        Guid assetId,
        string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath)
        => Engine.Assets.TryResolveAssetPathById(assetId, referenceAssetPath, out assetPath);

    public XRAsset? LoadImmediate(string assetPath, Type assetType)
        => Engine.Assets.LoadImmediate(assetPath, assetType);

    public bool TryHandleScalarAsset(
        IParser reader,
        Type expectedType,
        Scalar scalar,
        out object? value)
        => XRAssetDeserializer.TryHandleScalarXRAsset(reader, expectedType, scalar, out value);
}
