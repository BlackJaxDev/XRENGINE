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
}
