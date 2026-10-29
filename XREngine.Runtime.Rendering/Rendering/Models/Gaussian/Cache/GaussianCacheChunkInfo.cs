namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Describes one chunk of a cache.</summary>
/// <param name="Index">The chunk position. Chunk 0 is the root chunk.</param>
/// <param name="Kind">The kind of data in the chunk.</param>
/// <param name="SplatLevel">The splat level the chunk adds.</param>
/// <param name="DriverLevel">The driver level the chunk adds.</param>
/// <param name="TemporalLevel">The temporal level the chunk adds.</param>
/// <param name="ByteLength">The size of the chunk data in bytes.</param>
/// <param name="Sha256">The SHA-256 hash of the chunk data as 64 lowercase hex characters.</param>
public readonly record struct GaussianCacheChunkInfo(
    int Index,
    GaussianCacheChunkKind Kind,
    byte SplatLevel,
    byte DriverLevel,
    byte TemporalLevel,
    int ByteLength,
    string Sha256);
