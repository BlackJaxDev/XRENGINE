using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>
/// Sets the Windows GPU scheduling priority class of the current process.
/// </summary>
/// <remarks>
/// A desktop editor runs next to games and the desktop. At BelowNormal, the Windows GPU
/// scheduler runs work from Normal processes first, so a heavy editor frame cannot make
/// the rest of the PC slow. Do not use BelowNormal while XR is active: a VR compositor
/// at Normal (for example an uncapped Monado service) then gets the GPU ahead of the
/// editor, and the editor submits no XR frames.
/// </remarks>
public static class WindowsGpuSchedulingPriority
{
    // D3DKMT_SCHEDULINGPRIORITYCLASS values.
    private const int BelowNormal = 1;
    private const int Normal = 2;

    /// <summary>
    /// Sets the current process to the BelowNormal or Normal GPU scheduling class.
    /// Returns false, with a reason, when the OS rejects the call.
    /// </summary>
    public static bool TrySetForCurrentProcess(bool belowNormal, out string message)
    {
        string name = belowNormal ? "BelowNormal" : "Normal";
        if (!OperatingSystem.IsWindows())
        {
            message = "GPU scheduling priority is a Windows feature; unchanged.";
            return false;
        }

        try
        {
            using Process process = Process.GetCurrentProcess();
            int status = D3DKMTSetProcessSchedulingPriorityClass(process.Handle, belowNormal ? BelowNormal : Normal);
            message = status == 0
                ? $"GPU scheduling priority set to {name}."
                : $"GPU scheduling priority not set to {name} (NTSTATUS 0x{status:X8}).";
            return status == 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            message = $"GPU scheduling priority not set to {name} ({exception.GetType().Name}).";
            return false;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);
}
