using System.Security.Cryptography;
using System.Text;
using XREngine.Core;
using XREngine.Data;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Writes diagnostic captures to desktop host files.</summary>
internal sealed class DesktopDiagnosticCaptureFileOutput : IRuntimeDiagnosticCaptureFileOutput, IRuntimeDiagnosticTextFileOutput, IRuntimeGLSubmitTraceFileOutput, IRuntimeProfileCaptureFileOutput
{
    public IRuntimeOwnedTextLog OpenGLSubmitTraceLog()
    {
        string logsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Build", "Logs");
        Directory.CreateDirectory(logsRoot);
        string path = Path.Combine(logsRoot, "gl-submit-trace.log");

        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            // Keep each flushed line available after a driver fastfail.
            stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough);
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;
            writer.NewLine = "\n";
            return new DesktopGLSubmitTraceLog(stream, writer);
        }
        catch
        {
            try { writer?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            throw;
        }
    }

    public void EnsureProfileDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
    }

    public void EnforceProfileRetention(string profileRoot, int retainedCount)
    {
        string rootFullPath = Path.GetFullPath(profileRoot);
        string rootWithSeparator = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (DirectoryInfo directory in new DirectoryInfo(rootFullPath)
            .GetDirectories()
            .OrderByDescending(static d => d.CreationTimeUtc)
            .Skip(retainedCount))
        {
            string directoryFullPath = Path.GetFullPath(directory.FullName);
            if (!directoryFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                directory.Delete(recursive: true);
            }
            catch
            {
                // Retention must not disrupt profiling.
            }
        }
    }

    public void WriteProfileText(string filePath, string contents, bool append)
    {
        if (append)
            File.AppendAllText(filePath, contents, Encoding.UTF8);
        else
            File.WriteAllText(filePath, contents, Encoding.UTF8);
    }

    public void EnsureDiagnosticLogDirectory(string directoryPath)
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Diagnostic log output");
        Directory.CreateDirectory(directoryPath);
    }

    public void AppendDiagnosticLogText(string filePath, string text)
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Diagnostic log output");
        File.AppendAllText(filePath, text);
    }

    public void WritePngAndMetrics(string outputFilePath, byte[] pngBytes,
        RenderedOutputCaptureMetrics metrics,
        Func<RenderedOutputCaptureMetrics, string, string, DateTimeOffset, string> serializeMetrics)
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Frame capture output");
        string filePath = Path.GetFullPath(outputFilePath);
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(filePath, pngBytes);
        string sha256;
        using (FileStream captureStream = File.OpenRead(filePath))
            sha256 = Convert.ToHexString(SHA256.HashData(captureStream));

        string metricsJson = serializeMetrics(metrics, filePath, sha256, DateTimeOffset.UtcNow);
        File.WriteAllText(filePath + ".metrics.json", metricsJson);
    }

    public void WritePngInDirectory(string exportDirPath, string fileName, byte[] pngBytes)
    {
        RuntimeAssetReadServices.EnsureHostFileAccess("Pipeline capture output");
        string filePath = Path.Combine(exportDirPath, fileName);
        Utility.EnsureDirPathExists(exportDirPath);
        File.WriteAllBytes(filePath, pngBytes);
    }
}
