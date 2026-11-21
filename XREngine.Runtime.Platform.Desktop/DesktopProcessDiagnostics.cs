using System.Diagnostics;
using System.Numerics;
using XREngine.Native;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Reads desktop process counters for optional diagnostic snapshots.</summary>
internal static class DesktopProcessDiagnostics
{
    public static long? ReadWorkingSetBytes()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    /// <summary>
    /// Reads the number of processors in the current process affinity mask. It returns null when the mask cannot be read
    /// or on systems other than Windows. It allocates nothing.
    /// </summary>
    public static int? ReadEffectiveProcessorCount()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // The pseudo handle of the current process needs no close.
        if (!NativeMethods.GetProcessAffinityMask(new IntPtr(-1), out nuint processMask, out _) || processMask == 0)
            return null;

        // The mask has one bit for each processor of the current processor group.
        return BitOperations.PopCount((ulong)processMask);
    }
}
