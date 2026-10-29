namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Names the kind of data in a chunk section.</summary>
public enum GaussianCacheSectionKind : byte
{
    /// <summary>Per-attribute splat substreams. Each substream is a u8 attribute id, an i32 length, and the bytes.</summary>
    SplatStreams = 1,

    /// <summary>Driver triangle indices from the index buffer codec.</summary>
    DriverIndices = 2,

    /// <summary>One block of driver vertex motion residuals.</summary>
    DriverMotion = 3,
}
