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

    public string DiagnosticCode { get; } = diagnosticCode;
}
