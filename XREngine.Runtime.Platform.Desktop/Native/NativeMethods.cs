using System.Runtime.InteropServices;

namespace XREngine.Native
{
    public static partial class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, [Out] out RECT lpRect);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IntersectRect([Out] out RECT lprcDst, [In] ref RECT lprcSrc1, [In] ref RECT lprcSrc2);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, EShowWindowEnum flags);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        public static extern int LockWindowUpdate(IntPtr hwnd);

        [DllImport("shell32.dll")]
        public static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbSizeFileInfo,
            uint uFlags);

        [DllImport("user32.dll")]
        public static extern int DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern int MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hwnd, int wMsg, int wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hwnd, int msg, int wparam, int lparam);
        [DllImport("user32.dll")]
        public static extern int PostMessage(IntPtr hwnd, int msg, int wparam, int lparam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int wMsg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int wMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, int wparam, POINT pos);

        [DllImport("user32.dll")]
        public static extern int PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        public static extern int PostMessage(IntPtr hwnd, int wMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int PostMessage(IntPtr hwnd, int msg, int wparam, POINT pos);

        [DllImport("user32.dll")]
        public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        //[DllImport("user32.dll")]
        //[return: MarshalAs(UnmanagedType.Bool)]
        //public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

        [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        public static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, // x-coordinate of upper-left corner
            int nTopRect, // y-coordinate of upper-left corner
            int nRightRect, // x-coordinate of lower-right corner
            int nBottomRect, // y-coordinate of lower-right corner
            int nWidthEllipse, // height of ellipse
            int nHeightEllipse // width of ellipse
        );

        [DllImport("user32.dll")]
        public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

        //[DllImport("user32.dll", SetLastError = true)]
        //[return: MarshalAs(UnmanagedType.Bool)]
        //public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        public static extern int GetRgnBox(IntPtr hrgn, out RECT lprc);

        [DllImport("user32.dll")]
        public static extern Int32 GetWindowLong(IntPtr hWnd, Int32 Offset);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int smIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

        [DllImport("shell32.dll")]
        public static extern int SHAppBarMessage(uint dwMessage, [In] ref APPBARDATA pData);

        [DllImport("user32.dll")]
        public static extern bool AnimateWindow(IntPtr handle, int time, EAnimateWindowFlags dwFlags);

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

        public static bool TryDetermineSystemCapsLockState(out bool capsOn)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return DetermineWindowsCapsLockState(out capsOn);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
                return UNIX.DetermineCapsLockState(out capsOn);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return OSX.DetermineCapsLockState(out capsOn);

            capsOn = false;
            return false;
        }

        private static bool DetermineWindowsCapsLockState(out bool capsOn)
        {
            capsOn = (GetKeyState(0x14) & 0x0001) != 0; // Windows Caps Lock
            return true;
        }
    }
}
