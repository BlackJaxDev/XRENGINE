using System.Numerics;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// A normalized Poiyomi texture binding with its Unity asset and sampler semantics.
/// </summary>
public sealed record PoiyomiTextureDescriptor
{
    public required string SourcePropertyName { get; init; }
    public required string SemanticPropertyName { get; init; }
    public required UnityAssetReference Reference { get; init; }
    public required Vector2 Scale { get; init; }
    public required Vector2 Offset { get; init; }
    public required UnityResolvedAsset ResolvedAsset { get; init; }
    public UnityTextureImportDocument? ImportSettings { get; init; }
    public bool IsMissing => Reference.HasExternalGuid && !ResolvedAsset.Exists;
    public bool RequiresNativeArrayOrCube =>
        ImportSettings?.Shape is UnityTextureShape.Texture2DArray or
            UnityTextureShape.Cube or
            UnityTextureShape.CubeArray;
}
