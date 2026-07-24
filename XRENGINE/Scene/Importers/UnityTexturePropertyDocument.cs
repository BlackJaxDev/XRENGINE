using System.Numerics;

namespace XREngine.Scene.Importers;

/// <summary>
/// A material texture reference and its authored UV scale and offset.
/// </summary>
public sealed record UnityTexturePropertyDocument(
    UnityAssetReference TextureReference,
    Vector2 Scale,
    Vector2 Offset);
