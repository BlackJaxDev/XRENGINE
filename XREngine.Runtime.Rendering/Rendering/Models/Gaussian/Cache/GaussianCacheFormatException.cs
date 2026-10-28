using System;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Reports a cache that cannot be read. The diagnostic code names the cause.</summary>
public sealed class GaussianCacheFormatException(string diagnosticCode, string message) : FormatException(message)
{
    public const string UnsupportedRepresentationCode = "GaussianCache.UnsupportedRepresentation";
    public const string UnsupportedFormatVersionCode = "GaussianCache.UnsupportedFormatVersion";
    public const string HashMismatchCode = "GaussianCache.HashMismatch";
    public const string ChunkOutOfOrderCode = "GaussianCache.ChunkOutOfOrder";
    public const string MalformedCode = "GaussianCache.Malformed";

    /// <summary>The motion codec version of the header is not <see cref="GaussianCacheHeader.CurrentMotionCodecVersion"/>.</summary>
    public const string UnsupportedMotionCodecVersionCode = "GaussianCache.UnsupportedMotionCodecVersion";

    /// <summary>A count or a size of the header is above a limit of the format (see <see cref="GaussianCacheHeader.Validate"/>).</summary>
    public const string LimitExceededCode = "GaussianCache.LimitExceeded";

    /// <summary>A chunk asset path is not a relative path in the folder of the root asset.</summary>
    public const string InvalidChunkPathCode = "GaussianCache.InvalidChunkPath";

    /// <summary>The decode of a chunk stopped with a read past the end, a value out of range, or an overflow.</summary>
    public const string DecodeFailedCode = "GaussianCache.DecodeFailed";

    public string DiagnosticCode { get; } = diagnosticCode;
}
