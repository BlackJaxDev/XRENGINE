using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XREngine.Execution;

/// <summary>
/// Windows render-worker QoS helpers. High QoS explicitly disables EcoQoS and
/// raises only the persistent background render lanes to AboveNormal.
/// </summary>
internal static class WindowsThreadQos
{
    private const int ThreadPowerThrottling = 5;
    private const uint ThreadPowerThrottlingCurrentVersion = 1;
    private const uint ThreadPowerThrottlingExecutionSpeed = 0x1;

    internal static void ApplyHighRenderPriority()
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        if (!OperatingSystem.IsWindows())
            return;

        var state = new ThreadPowerThrottlingState
        {
            Version = ThreadPowerThrottlingCurrentVersion,
            ControlMask = ThreadPowerThrottlingExecutionSpeed,
            StateMask = 0,
        };

        if (!SetThreadInformation(
                GetCurrentThread(),
                ThreadPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<ThreadPowerThrottlingState>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Failed to opt a high-priority render worker out of Windows EcoQoS.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadPowerThrottlingState
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadInformation(
        nint thread,
        int threadInformationClass,
        ref ThreadPowerThrottlingState threadInformation,
        uint threadInformationSize);
}
