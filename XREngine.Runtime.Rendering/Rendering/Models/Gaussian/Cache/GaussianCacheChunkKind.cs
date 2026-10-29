namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Tells which data a chunk holds.</summary>
public enum GaussianCacheChunkKind : byte
{
    Root = 0,
    DriverLevel = 1,
    SplatLevel = 2,
    DriverTemporal = 3,
}
