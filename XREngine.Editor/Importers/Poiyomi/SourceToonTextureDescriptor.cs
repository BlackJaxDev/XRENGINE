using System.Numerics;

namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// A normalized Poiyomi texture binding with its Unity asset and sampler semantics.
/// </summary>
public sealed record SourceToonTextureDescriptor
{
    public required string SourcePropertyName { get; init; }
    public required string SemanticPropertyName { get; init; }
    public required SourceAssetReference Reference { get; init; }
    public required Vector2 Scale { get; init; }
    public required Vector2 Offset { get; init; }
    public required SourceResolvedAsset ResolvedAsset { get; init; }
    public SerializedTextureImportDocument? ImportSettings { get; init; }
    public bool IsMissing => Reference.HasExternalGuid && !ResolvedAsset.Exists;
    public bool RequiresNativeArrayOrCube =>
        ImportSettings?.Shape is SerializedTextureShape.Texture2DArray or
            SerializedTextureShape.Cube or
            SerializedTextureShape.CubeArray;
}
