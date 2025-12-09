using System.Diagnostics;
using System.Runtime.InteropServices;
using XREngine.Data;

namespace System
{
    public static class Win32
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public class SafeHandle(VoidPtr handle) : IDisposable
        {
            public VoidPtr Handle { get; private set; } = handle;

            ~SafeHandle()
            {
                Dispose();
            }

            public void Dispose()
            {
                if (Handle != 0)
                {
                    CloseHandle(Handle);
                    Handle = 0;
                    GC.SuppressFinalize(this);
                }
            }

            public void ErrorCheck()
            {
                if (Handle == 0)
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            public static implicit operator SafeHandle(VoidPtr handle) { return new SafeHandle(handle); }

            internal static unsafe SafeHandle Duplicate(VoidPtr hFile)
            {
                VoidPtr hProc = Process.GetCurrentProcess().Handle;
                if (!DuplicateHandle(hProc, hFile, hProc, out hFile, 0, false, 2))
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                return new SafeHandle(hFile);
            }
        }

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool CloseHandle(VoidPtr hObject);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DuplicateHandle(VoidPtr hSourceProcessHandle, VoidPtr hSourceHandle, VoidPtr hTargetProcessHandle, out VoidPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);


        [DllImport("Kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void MoveMemory(VoidPtr dest, VoidPtr src, uint size);
        [DllImport("Kernel32.dll", EntryPoint = "RtlFillMemory", SetLastError = false)]
        public static extern void FillMemory(VoidPtr dest, uint length, byte value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern VoidPtr GetDC(VoidPtr hWnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(VoidPtr hWnd, VoidPtr hDC);

        #region File Mapping
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern VoidPtr CreateFileMapping(VoidPtr hFile, VoidPtr lpAttributes, _FileMapProtect flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FlushViewOfFile(VoidPtr lpBaseAddress, uint dwNumberOfBytesToFlush);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern VoidPtr MapViewOfFile(VoidPtr hFileMappingObject, _FileMapAccess dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern VoidPtr MapViewOfFileEx(VoidPtr hFileMappingObject, _FileMapAccess dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap, VoidPtr lpBaseAddress);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern VoidPtr OpenFileMapping(_FileMapAccess dwDesiredAccess, bool bInheritHandle, string lpName);
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool UnmapViewOfFile(VoidPtr lpBaseAddress);

        [Flags]
        public enum _FileMapProtect : uint
        {
            ExecuteRead = 0x20,
            ExecuteReadWrite = 0x40,
            ExecuteWriteCopy = 0x80,
            ReadOnly = 0x02,
            ReadWrite = 0x04,
            WriteCopy = 0x08,

            Commit = 0x8000000,
            Image = 0x1000000,
            LargePages = 0x80000000,
            NoCache = 0x10000000,
            Reserve = 0x4000000,
            WriteCombine = 0x40000000
        }

        [Flags]
        public enum _FileMapAccess : uint
        {
            Copy = 0x01,
            Write = 0x02,
            Read = 0x04,
            Execute = 0x20,
            All = 0x000F001F
        }
        #endregion
    }
}
