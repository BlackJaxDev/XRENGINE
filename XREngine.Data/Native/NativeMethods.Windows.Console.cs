using System.Runtime.InteropServices;

namespace XREngine.Native
{
    public static partial class NativeMethods
    {
        private const uint AttachParentProcess = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        public static bool EnsureConsoleForProcess()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            if (GetConsoleWindow() != IntPtr.Zero)
                return true;

            if (AttachConsole(AttachParentProcess))
                return true;

            int error = Marshal.GetLastWin32Error();
            const int errorAccessDenied = 5;
            if (error == errorAccessDenied)
                return true;

            return AllocConsole();
        }
    }
}
