using System.Runtime.InteropServices;

namespace XREngine.Native
{
    public static partial class NativeMethods
    {
        public static class UNIX
        {
            const string libX11 = "libX11.so.6";

            [DllImport(libX11)]
            private static extern IntPtr XOpenDisplay(string? display);

            [DllImport(libX11)]
            private static extern int XkbGetIndicatorState(IntPtr display, int deviceSpec, out uint state);

            [DllImport(libX11)]
            private static extern int XCloseDisplay(IntPtr display);

            public static bool DetermineCapsLockState(out bool capsOn)
            {
                capsOn = false;

                IntPtr display = XOpenDisplay(null);
                if (display == IntPtr.Zero)
                    return false;

                if (XkbGetIndicatorState(display, 0, out uint state) == 0)
                {
                    capsOn = (state & 0x01) != 0; // Caps Lock is the first bit
                    XCloseDisplay(display);
                    return true;
                }

                XCloseDisplay(display);
                return false;
            }
        }
    }
}
