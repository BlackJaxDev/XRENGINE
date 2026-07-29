namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Immutable normalized record for a file consulted or referenced by a model producer.
/// </summary>
public sealed class ModelImportDependency
{
    public ModelImportDependency(
        string normalizedPath,
        ModelImportDependencyKind kind,
        bool isRequired,
        long length,
        long lastWriteTimeUtcTicks,
        string? contentHash = null,
        string? producerKey = null,
        ModelImportDependencyHashMode contentHashMode = ModelImportDependencyHashMode.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (lastWriteTimeUtcTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(lastWriteTimeUtcTicks));

        NormalizedPath = normalizedPath;
        Kind = kind;
        IsRequired = isRequired;
        Length = length;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        ContentHash = string.IsNullOrWhiteSpace(contentHash)
            ? null
            : contentHash.ToLowerInvariant();
        ContentHashMode = ContentHash is null
            ? ModelImportDependencyHashMode.None
            : contentHashMode == ModelImportDependencyHashMode.None
                ? ModelImportDependencyHashMode.ProducerDefined
                : contentHashMode;
        ProducerKey = string.IsNullOrWhiteSpace(producerKey) ? null : producerKey;
    }

    public string NormalizedPath { get; }
    public ModelImportDependencyKind Kind { get; }
    public bool IsRequired { get; }
    public long Length { get; }
    public long LastWriteTimeUtcTicks { get; }
    public string? ContentHash { get; }
    public ModelImportDependencyHashMode ContentHashMode { get; }
    public string? ProducerKey { get; }

    /// <summary>
    /// Captures the inexpensive freshness tuple for a local file without hashing its bytes.
    /// Missing optional files are represented with zero length and timestamp.
    /// </summary>
    public static ModelImportDependency FromFile(
        string path,
        ModelImportDependencyKind kind,
        bool isRequired,
        string? contentHash = null,
        string? producerKey = null,
        ModelImportDependencyHashMode contentHashMode = ModelImportDependencyHashMode.None)
    {
        string normalizedPath = ModelImportPathNormalizer.NormalizeAbsolutePath(path);
        string systemPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        FileInfo fileInfo = new(systemPath);
        long length = fileInfo.Exists ? fileInfo.Length : 0L;
        long lastWriteTimeUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L;

        return new ModelImportDependency(
            normalizedPath,
            kind,
            isRequired,
            length,
            lastWriteTimeUtcTicks,
            contentHash,
            producerKey,
            contentHashMode);
    }
}
