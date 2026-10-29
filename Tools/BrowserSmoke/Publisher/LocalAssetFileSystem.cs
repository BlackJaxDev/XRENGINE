using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Runtime.Platform.Desktop;

/// <summary>Provides local authored-file discovery, metadata operations, and direct reads for the publisher.</summary>
internal sealed class LocalAssetFileSystem : IAssetFileSystem, IAssetMetadataFileBackend, IRuntimeHostFileReadBackend, IRuntimeHostFileAsyncReadBackend, IHostBinaryCacheFileBackend, ITextureStreamingCacheFileBackend, ITextureStreamingCacheReplaceBackend
{
    public bool SupportsChangeNotifications => false;
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption option)
        => Directory.EnumerateFiles(path, pattern, option);
    public IEnumerable<string> EnumerateDirectories(string path, string pattern, SearchOption option)
        => Directory.EnumerateDirectories(path, pattern, option);
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
        => Directory.EnumerateFileSystemEntries(path);
    public IAssetChangeMonitor CreateChangeMonitor()
        => throw new NotSupportedException("The publisher qualifier does not monitor source changes.");

    public bool FileExists(string path) => File.Exists(path);
    public Stream OpenRead(string path, FileShare share) => new FileStream(path, FileMode.Open, FileAccess.Read, share);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteAllBytesAtomic(string path, byte[] bytes) => HostBinaryCacheFileWriter.WriteAllBytesAtomic(path, bytes);
    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) => File.ReadAllBytesAsync(path, cancellationToken);
    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);
    public void PrepareDirectory(string cachePath) => TextureStreamingCacheFileWriter.PrepareDirectory(cachePath);
    public void WriteStaged(string cachePath, ReadOnlySpan<byte> payload) => TextureStreamingCacheFileWriter.WriteStaged(cachePath, payload);
    public void PublishStaged(string cachePath, DateTime timestamp) => TextureStreamingCacheFileWriter.PublishStaged(cachePath, timestamp);
    public void WriteAndReplace(string cachePath, ReadOnlySpan<byte> payload, DateTime timestamp) => TextureStreamingCacheFileWriter.WriteAndReplace(cachePath, payload, timestamp);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path) => Directory.Delete(path);
    public void DeleteFile(string path) => File.Delete(path);
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Move(sourcePath, destinationPath, overwrite);
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public Guid TryExtractAssetGuid(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Guid.Empty;

        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]))
                        continue;

                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Guid.TryParse(trimmed[3..].Trim(), out Guid guid))
                        return guid;
                }

                break;
            }
            catch (IOException)
            {
                if (attempt == maxAttempts - 1)
                    break;

                Thread.Sleep(15 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
        }

        return Guid.Empty;
    }
}
