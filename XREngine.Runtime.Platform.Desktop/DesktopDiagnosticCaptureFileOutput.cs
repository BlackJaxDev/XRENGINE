using System.Security.Cryptography;
using XREngine.Core;
using XREngine.Data;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Writes diagnostic captures to desktop host files.</summary>
internal sealed class DesktopDiagnosticCaptureFileOutput : IRuntimeDiagnosticCaptureFileOutput, IRuntimeDiagnosticTextFileOutput
{
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
