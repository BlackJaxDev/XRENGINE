using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Rendering;

/// <summary>
/// Process-lifetime unmanaged entry points for renderer native callbacks. They live in this
/// non-collectible assembly so a native library never holds a code address from a renderer
/// generation that can be unloaded.
/// </summary>
internal sealed unsafe class DesktopRendererNativeCallbackEntryPoints : IRendererNativeCallbackEntryPoints
{
    // ImGui's managed bindings require a non-null string pointer, including on provider failure.
    // This NUL byte shares the process lifetime of the native entry point addresses.
    private static readonly nint EmptyClipboardText;
    private static nint _clipboardReturnBuffer;

    static DesktopRendererNativeCallbackEntryPoints()
        => EmptyClipboardText = Marshal.StringToHGlobalAnsi(string.Empty);

    public nint StreamlineLog
        => (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&OnStreamlineLogMessage;

    public nint GetClipboardText
        => (nint)(delegate* unmanaged[Cdecl]<void*, byte*>)&OnGetClipboardText;

    public nint SetClipboardText
        => (nint)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&OnSetClipboardText;

    public nint VulkanDebug
        => (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint>)&OnVulkanDebugMessage;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamlineLogMessage(int type, nint message)
        => RendererNativeCallbackBridge.DispatchStreamlineLog(type, message);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnVulkanDebugMessage(
        uint messageSeverity,
        uint messageTypes,
        nint callbackData,
        nint userData)
        => RendererNativeCallbackBridge.DispatchVulkanDebug(messageSeverity, messageTypes, callbackData, userData);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* OnGetClipboardText(void* userData)
    {
        // The returned buffer must stay valid until the caller copies it, so it is released on
        // the next request rather than before returning.
        if (_clipboardReturnBuffer != 0)
        {
            Marshal.FreeHGlobal(_clipboardReturnBuffer);
            _clipboardReturnBuffer = 0;
        }

        try
        {
            string text = RuntimeClipboardServices.Current?.GetText() ?? string.Empty;
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            _clipboardReturnBuffer = Marshal.AllocHGlobal(utf8.Length + 1);
            Marshal.Copy(utf8, 0, _clipboardReturnBuffer, utf8.Length);
            Marshal.WriteByte(_clipboardReturnBuffer, utf8.Length, 0);
            return (byte*)_clipboardReturnBuffer;
        }
        catch
        {
            return (byte*)EmptyClipboardText;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetClipboardText(void* userData, byte* text)
    {
        try
        {
            if (text is not null)
                RuntimeClipboardServices.Current?.SetText(Marshal.PtrToStringUTF8((nint)text) ?? string.Empty);
        }
        catch
        {
        }
    }
}
