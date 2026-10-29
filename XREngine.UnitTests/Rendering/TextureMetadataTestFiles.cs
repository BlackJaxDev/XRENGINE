using XREngine.Data;

namespace XREngine.UnitTests.Rendering;

/// <summary>Supplies virtual metadata and explicit read-admission controls without file access.</summary>
internal sealed class TextureMetadataTestFiles : IRuntimeFileDiscovery, IRuntimeHostFileMetadataBackend, IRuntimeAssetReadSource
{
    public long TimestampTicks { get; set; } = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    public long Length { get; set; } = 16;
    public bool ExistsResult { get; set; } = true;
    public Exception? Failure { get; set; }
    public Action? OnProbe { get; set; }
    public int Calls { get; private set; }
    public bool SupportsSynchronousReads { get; set; } = true;
    public bool SupportsHostFileAccess { get; set; } = true;

    public bool TryGetFileMetadata(string path, out long lastWriteTimeUtcTicks, out long length)
    {
        Calls++;
        OnProbe?.Invoke();
        if (Failure is not null)
            throw Failure;
        lastWriteTimeUtcTicks = TimestampTicks;
        length = Length;
        return ExistsResult;
    }

    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option) => throw new NotSupportedException();
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => throw new NotSupportedException();
    public bool Exists(string path) => throw new NotSupportedException();
    public byte[] ReadAllBytes(string path) => throw new NotSupportedException();
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
}
