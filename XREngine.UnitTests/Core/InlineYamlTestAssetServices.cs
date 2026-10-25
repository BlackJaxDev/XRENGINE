using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Core;

/// <summary>Uses runtime YAML converters without loading external test assets.</summary>
internal sealed class InlineYamlTestAssetServices : IAssetSerializationServices
{
    public string AssetExtension => AssetManager.AssetExtension;
    public string? GameAssetsPath => null;
    public string? EngineAssetsPath => null;
    public string? CurrentDeserializationPath => null;
    public IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => AssetManager.YamlTypeConverters;

    public void EnsureYamlAssetRuntimeSupported(string? path = null)
        => AssetManager.EnsureYamlAssetRuntimeSupported(path);

    public bool TryGetAssetById(Guid assetId, [NotNullWhen(true)] out XRAsset? asset)
    {
        asset = null;
        return false;
    }

    public bool TryResolveAssetPathById(Guid assetId, string? referenceAssetPath,
        [NotNullWhen(true)] out string? assetPath)
    {
        assetPath = null;
        return false;
    }

    public bool TryCreatePortableAssetReference(string assetPath,
        [NotNullWhen(true)] out string? reference)
    {
        reference = null;
        return false;
    }

    public XRAsset? LoadImmediate(string assetPath, Type assetType)
        => throw new InvalidOperationException("This YAML test must not load external assets.");

    public bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset)
    {
        asset = null;
        return false;
    }

    public bool TryHandleScalarAsset(IParser reader, Type expectedType,
        Scalar scalar, out object? value)
        => XRAssetDeserializer.TryHandleScalarXRAsset(reader, expectedType, scalar, out value);
}
