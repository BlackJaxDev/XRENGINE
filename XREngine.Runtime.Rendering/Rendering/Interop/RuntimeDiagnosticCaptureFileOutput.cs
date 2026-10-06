namespace XREngine.Rendering;

/// <summary>Stores the optional diagnostic file output service installed by the host.</summary>
public static class RuntimeDiagnosticCaptureFileOutput
{
    private static IRuntimeDiagnosticCaptureFileOutput? _current;

    public static IRuntimeDiagnosticCaptureFileOutput? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>Gets the installed host writer or reports that file output is unavailable.</summary>
    public static IRuntimeDiagnosticCaptureFileOutput Require()
        => Current ?? throw new InvalidOperationException("Diagnostic capture file output is unavailable on this host.");

    /// <summary>Gets the installed host writer with diagnostic text output support.</summary>
    public static IRuntimeDiagnosticTextFileOutput RequireTextOutput()
    {
        IRuntimeDiagnosticCaptureFileOutput output = Require();
        return output as IRuntimeDiagnosticTextFileOutput
            ?? throw new InvalidOperationException("Installed diagnostic capture file output does not support diagnostic text files.");
    }
}
