namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// SHA-256-derived semantic cache variant key plus diagnostic-only build identity.
/// </summary>
public sealed class ModelCacheVariantFingerprint
{
    private readonly byte[] _canonicalBytes;

    internal ModelCacheVariantFingerprint(
        string value,
        string fullHash,
        byte[] canonicalBytes,
        string? engineBuildIdentity)
    {
        Value = value;
        FullHash = fullHash;
        _canonicalBytes = canonicalBytes;
        EngineBuildIdentity = engineBuildIdentity;
    }

    /// <summary>
    /// Gets the first 128 bits of SHA-256 as 32 lowercase hexadecimal characters.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the complete SHA-256 digest for diagnostics and collision investigation.
    /// </summary>
    public string FullHash { get; }

    /// <summary>
    /// Gets build identity recorded for diagnostics only. It is not part of <see cref="Value"/>.
    /// </summary>
    public string? EngineBuildIdentity { get; }

    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes;

    public override string ToString() => Value;
}
