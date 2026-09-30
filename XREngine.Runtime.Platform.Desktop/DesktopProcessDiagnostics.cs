using System.Diagnostics;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Reads desktop process counters for optional diagnostic snapshots.</summary>
internal static class DesktopProcessDiagnostics
{
    public static long? ReadWorkingSetBytes()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }
}
