using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace XREngine.Rendering;

/// <summary>
/// Owns unmanaged callback entry points that must outlive any collectible renderer generation.
/// </summary>
public static unsafe class RendererNativeCallbackBridge
{
    private static readonly object StreamlineSync = new();
    private static Action<int, nint>? _streamlineLogHandler;
    private static StreamlineLogRegistration? _streamlineLogOwner;
    private static nint _clipboardReturnBuffer;
    private static readonly ConcurrentDictionary<nint, Func<uint, uint, nint, nint, uint>>
        VulkanDebugHandlers = new();
    private static long _nextVulkanDebugHandlerId;

    public static nint StreamlineLogCallbackPointer
        => (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&OnStreamlineLogMessage;

    public static nint GetClipboardTextCallbackPointer
        => (nint)(delegate* unmanaged[Cdecl]<void*, byte*>)&GetClipboardText;

    public static nint SetClipboardTextCallbackPointer
        => (nint)(delegate* unmanaged[Cdecl]<void*, byte*, void>)&SetClipboardText;

    public static nint VulkanDebugCallbackPointer
        => (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint>)&OnVulkanDebugMessage;

    public static IDisposable RegisterStreamlineLogHandler(Action<int, nint> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        StreamlineLogRegistration registration = new(handler);
        lock (StreamlineSync)
        {
            _streamlineLogOwner = registration;
            _streamlineLogHandler = handler;
        }

        return registration;
    }

    public static VulkanDebugRegistration RegisterVulkanDebugHandler(
        Func<uint, uint, nint, nint, uint> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        nint id = (nint)Interlocked.Increment(ref _nextVulkanDebugHandlerId);
        if (!VulkanDebugHandlers.TryAdd(id, handler))
            throw new InvalidOperationException("Failed to register the Vulkan debug callback handler.");
        return new(id);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamlineLogMessage(int type, nint message)
    {
        Action<int, nint>? handler;
        lock (StreamlineSync)
            handler = _streamlineLogHandler;

        try
        {
            handler?.Invoke(type, message);
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnVulkanDebugMessage(
        uint messageSeverity,
        uint messageTypes,
        nint callbackData,
        nint userData)
    {
        if (!VulkanDebugHandlers.TryGetValue(userData, out Func<uint, uint, nint, nint, uint>? handler))
            return 0;

        try
        {
            return handler(messageSeverity, messageTypes, callbackData, userData);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetClipboardText(void* userData)
    {
        if (_clipboardReturnBuffer != 0)
        {
            Marshal.FreeHGlobal(_clipboardReturnBuffer);
            _clipboardReturnBuffer = 0;
        }

        try
        {
            string? text = RuntimeClipboardServices.Current?.GetText();
            if (text is null)
                return null;
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            _clipboardReturnBuffer = Marshal.AllocHGlobal(utf8.Length + 1);
            Marshal.Copy(utf8, 0, _clipboardReturnBuffer, utf8.Length);
            Marshal.WriteByte(_clipboardReturnBuffer, utf8.Length, 0);
            return (byte*)_clipboardReturnBuffer;
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetClipboardText(void* userData, byte* text)
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

    private sealed class StreamlineLogRegistration(Action<int, nint> handler) : IDisposable
    {
        private Action<int, nint>? _handler = handler;

        public void Dispose()
        {
            Action<int, nint>? current = Interlocked.Exchange(ref _handler, null);
            if (current is null)
                return;

            lock (StreamlineSync)
            {
                if (ReferenceEquals(_streamlineLogOwner, this))
                {
                    _streamlineLogOwner = null;
                    _streamlineLogHandler = null;
                }
            }
        }
    }

    public sealed class VulkanDebugRegistration : IDisposable
    {
        private nint _id;

        internal VulkanDebugRegistration(nint id)
            => _id = id;

        public nint UserData => Volatile.Read(ref _id);

        public void Dispose()
        {
            nint id = Interlocked.Exchange(ref _id, 0);
            if (id != 0)
                VulkanDebugHandlers.TryRemove(id, out _);
        }
    }

}
