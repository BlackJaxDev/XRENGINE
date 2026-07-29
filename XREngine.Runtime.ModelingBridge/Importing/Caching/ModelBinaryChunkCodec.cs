namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Storage codec reserved by each chunk entry. Schema v1 supports only uncompressed bytes.
/// </summary>
internal enum ModelBinaryChunkCodec : uint
{
    None = 0,
}
