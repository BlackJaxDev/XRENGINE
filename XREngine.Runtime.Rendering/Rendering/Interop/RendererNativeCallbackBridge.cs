using System.Collections.Concurrent;

namespace XREngine.Rendering;

/// <summary>
/// Routes native callbacks to managed handlers that can belong to a collectible renderer
/// generation. The native-callable addresses come from the host through
/// <see cref="EntryPoints"/>, so they outlive every renderer generation.
/// </summary>
public static class RendererNativeCallbackBridge
{
    private static readonly object StreamlineSync = new();
    private static Action<int, nint>? _streamlineLogHandler;
    private static StreamlineLogRegistration? _streamlineLogOwner;
    private static readonly ConcurrentDictionary<nint, Func<uint, uint, nint, nint, uint>>
        VulkanDebugHandlers = new();
    private static long _nextVulkanDebugHandlerId;
    private static IRendererNativeCallbackEntryPoints? _entryPoints;

    /// <summary>The host-installed native-callable addresses, or null when no native host is composed.</summary>
    public static IRendererNativeCallbackEntryPoints? EntryPoints
    {
        get => Volatile.Read(ref _entryPoints);
        set => Volatile.Write(ref _entryPoints, value);
    }

    private static IRendererNativeCallbackEntryPoints RequiredEntryPoints => EntryPoints ??
        throw new InvalidOperationException(
            "Renderer native callback entry points are not installed. Install a desktop platform backend before creating a native renderer.");

    public static nint StreamlineLogCallbackPointer => RequiredEntryPoints.StreamlineLog;

    public static nint GetClipboardTextCallbackPointer => RequiredEntryPoints.GetClipboardText;

    public static nint SetClipboardTextCallbackPointer => RequiredEntryPoints.SetClipboardText;

    public static nint VulkanDebugCallbackPointer => RequiredEntryPoints.VulkanDebug;

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

    /// <summary>
    /// Forwards a Streamline log message to the registered handler. Called from the host's
    /// native entry point, so it never lets an exception escape.
    /// </summary>
    public static void DispatchStreamlineLog(int type, nint message)
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

    /// <summary>
    /// Forwards a Vulkan debug message to the handler registered under <paramref name="userData"/>.
    /// Called from the host's native entry point, so it never lets an exception escape.
    /// </summary>
    public static uint DispatchVulkanDebug(
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
