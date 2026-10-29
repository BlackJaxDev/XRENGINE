using System.Text;
using XREngine.Data;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Writes authoring asset files to desktop host paths.</summary>
public sealed class DesktopHostAssetFileOutput : IHostAssetFileOutput, IHostAssetFileAsyncOutput, IHostAssetFileOpenWrite, IHostAssetFileSharedOutput, IHostAssetFileTransactionalOutput
{
    public void WriteTextWithReplacement(string path, Encoding encoding, Action<TextWriter> serialize)
    {
        EnsureDirectoryExists(path);
        string tempPath = CreateTempWritePath(path);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, encoding))
            {
                serialize(writer);
                writer.Flush();
                stream.Flush(true);
            }

            ReplaceSerializedAssetFile(tempPath, path);
        }
        finally
        {
            TryDeleteTempWritePath(tempPath);
        }
    }

    public async Task WriteTextWithReplacementAsync(string path, Encoding encoding, Func<string> serialize)
    {
        EnsureDirectoryExists(path);
        string tempPath = CreateTempWritePath(path);
        string text = serialize();
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(stream, encoding))
            {
                await writer.WriteAsync(text).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                stream.Flush(true);
            }

            ReplaceSerializedAssetFile(tempPath, path);
        }
        finally
        {
            TryDeleteTempWritePath(tempPath);
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static string CreateTempWritePath(string path)
        => $"{path}.{Guid.NewGuid():N}.tmp";

    private static void ReplaceSerializedAssetFile(string tempPath, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(tempPath, path);
            return;
        }

        // A new destination can have a short-lived share lock. Retry the replacement.
        const int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static void TryDeleteTempWritePath(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
        }
    }

    public Stream OpenWrite(string path) => File.OpenWrite(path);

    public Stream CreateFileWithParentDirectory(string path, FileShare share)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        return new FileStream(path, FileMode.Create, FileAccess.Write, share);
    }

    public Stream CreateFileWithParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        return File.Create(path);
    }

    public void WriteAllText(string path, string text, Encoding encoding)
        => File.WriteAllText(path, text, encoding);

    public Task WriteAllTextAsync(string path, string text, Encoding encoding)
        => File.WriteAllTextAsync(path, text, encoding);

    public Task WriteAllBytesWithParentDirectoryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        return File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }
}
