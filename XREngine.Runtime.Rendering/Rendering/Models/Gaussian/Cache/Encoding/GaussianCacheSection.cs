using System;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>One section of chunk data. The wire form is a 24-byte header followed by the payload.</summary>
public readonly record struct GaussianCacheSection(
    GaussianCacheSectionKind Kind,
    byte Level,
    byte TemporalLevel,
    byte Flags,
    int First,
    int Count,
    int FrameFirst,
    int FrameCount,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>Size in bytes of the fixed header before the payload.</summary>
    public const int HeaderSize = 24;

    /// <summary>Flag bit that marks wide motion residuals.</summary>
    public const byte WideResidualsFlag = 1;

    public bool WideResiduals => (Flags & WideResidualsFlag) != 0;
}
