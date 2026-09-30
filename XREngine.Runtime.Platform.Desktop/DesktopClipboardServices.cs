using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Copies Unicode clipboard data under the native clipboard ownership rules.</summary>
internal sealed class DesktopClipboardServices : IRuntimeClipboardServices
{
    public string? GetText()
    {
        if (!OpenClipboard(0))
            return null;
        try
        {
            nint handle = GetClipboardData(CfUnicodeText);
            if (handle == 0)
                return null;
            nint data = GlobalLock(handle);
            if (data == 0)
                return null;
            try { return Marshal.PtrToStringUni(data); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public void SetText(string text)
    {
        if (!OpenClipboard(0))
            return;
        nint ownedHandle = 0;
        try
        {
            if (!EmptyClipboard())
                return;
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text);
            ownedHandle = GlobalAlloc(GmemMoveable, (nuint)(bytes.Length + 2));
            if (ownedHandle == 0)
                return;
            nint data = GlobalLock(ownedHandle);
            if (data == 0)
                return;
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                Marshal.WriteInt16(data, bytes.Length, 0);
            }
            finally { GlobalUnlock(ownedHandle); }
            if (SetClipboardData(CfUnicodeText, ownedHandle) != 0)
                ownedHandle = 0;
        }
        finally
        {
            if (ownedHandle != 0)
                GlobalFree(ownedHandle);
            CloseClipboard();
        }
    }

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalFree(nint memory);
}
