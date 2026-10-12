namespace XREngine.Rendering;

/// <summary>Writes diagnostic PNG data through a host file output service.</summary>
public interface IRuntimeDiagnosticCaptureFileOutput
{
    /// <summary>Writes PNG data and metrics generated after the written file is hashed.</summary>
    void WritePngAndMetrics(string outputFilePath, byte[] pngBytes,
        RenderedOutputCaptureMetrics metrics,
        Func<RenderedOutputCaptureMetrics, string, string, DateTimeOffset, string> serializeMetrics);

    /// <summary>Writes PNG data into a named export directory.</summary>
    void WritePngInDirectory(string exportDirPath, string fileName, byte[] pngBytes);
}
