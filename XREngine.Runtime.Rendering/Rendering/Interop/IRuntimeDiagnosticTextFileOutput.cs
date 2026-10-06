namespace XREngine.Rendering;

/// <summary>Writes diagnostic text through a host file output service.</summary>
public interface IRuntimeDiagnosticTextFileOutput
{
    /// <summary>Creates a directory for diagnostic log files.</summary>
    void EnsureDiagnosticLogDirectory(string directoryPath);

    /// <summary>Appends text to a diagnostic log file.</summary>
    void AppendDiagnosticLogText(string filePath, string text);
}
