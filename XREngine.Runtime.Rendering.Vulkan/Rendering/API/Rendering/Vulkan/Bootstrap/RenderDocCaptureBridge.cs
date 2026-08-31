using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides an optional bridge to RenderDoc when its capture module is already
/// loaded into the process. Normal engine launches do not load RenderDoc.
/// </summary>
public static class RenderDocCaptureBridge
{
    private const int RenderDocApiVersion100 = 10000;
    private const int SetCaptureFilePathTemplateIndex = 11;
    private const int TriggerCaptureIndex = 15;
    private const int StartFrameCaptureIndex = 19;
    private const int IsFrameCapturingIndex = 20;
    private const int EndFrameCaptureIndex = 21;
    private static int _ownsExplicitCapture;

    /// <summary>
    /// Starts an explicit diagnostic capture around API work such as a texture
    /// readback that occurs outside presented-frame recording. The caller must
    /// pair a successful start with <see cref="TryEndCapture"/> in a finally block.
    /// Intended for a single-device, single-window isolated editor session.
    /// </summary>
    public static bool TryStartCapture(string captureFilePathTemplate)
    {
        if (string.IsNullOrWhiteSpace(captureFilePathTemplate) ||
            Interlocked.CompareExchange(ref _ownsExplicitCapture, 1, 0) != 0)
            return false;

        bool started = false;
        nint module = 0;
        try
        {
            if (!TryGetApi(out module, out nint table))
                return false;
            RenderDocIsFrameCapturing isCapturing = GetEntry<RenderDocIsFrameCapturing>(table, IsFrameCapturingIndex);
            if (isCapturing() != 0)
                return false;
            GetEntry<RenderDocSetCaptureFilePathTemplate>(table, SetCaptureFilePathTemplateIndex)(captureFilePathTemplate);
            GetEntry<RenderDocStartFrameCapture>(table, StartFrameCaptureIndex)(0, 0);
            started = isCapturing() != 0;
            return started;
        }
        finally
        {
            if (module != 0)
                NativeLibrary.Free(module);
            if (!started)
                Volatile.Write(ref _ownsExplicitCapture, 0);
        }
    }

    /// <summary>Ends only an explicit capture started successfully by this bridge.</summary>
    public static bool TryEndCapture()
    {
        if (Interlocked.CompareExchange(ref _ownsExplicitCapture, 2, 1) != 1)
            return false;
        nint module = 0;
        try
        {
            return TryGetApi(out module, out nint table) &&
                GetEntry<RenderDocEndFrameCapture>(table, EndFrameCaptureIndex)(0, 0) != 0;
        }
        finally
        {
            if (module != 0)
                NativeLibrary.Free(module);
            Volatile.Write(ref _ownsExplicitCapture, 0);
        }
    }

    private static T GetEntry<T>(nint table, int index) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * nint.Size));

    private static bool TryGetApi(out nint module, out nint table)
    {
        table = 0;
        return NativeLibrary.TryLoad("renderdoc.dll", out module) &&
            NativeLibrary.TryGetExport(module, "RENDERDOC_GetAPI", out nint address) &&
            Marshal.GetDelegateForFunctionPointer<RenderDocGetApi>(address)(RenderDocApiVersion100, out table) == 1 &&
            table != 0;
    }

    /// <summary>
    /// Queues capture of the next presented frame when RenderDoc is attached.
    /// </summary>
    /// <param name="captureFilePathTemplate">
    /// Optional UTF-8 path template for the capture. RenderDoc appends its frame suffix.
    /// </param>
    /// <returns><see langword="true"/> when the request reached RenderDoc.</returns>
    public static bool TryTriggerCapture(string? captureFilePathTemplate = null)
    {
        if (Volatile.Read(ref _ownsExplicitCapture) != 0 ||
            !NativeLibrary.TryLoad("renderdoc.dll", out nint module))
            return false;

        try
        {
            if (!NativeLibrary.TryGetExport(module, "RENDERDOC_GetAPI", out nint getApiAddress))
                return false;

            RenderDocGetApi getApi = Marshal.GetDelegateForFunctionPointer<RenderDocGetApi>(getApiAddress);
            if (getApi(RenderDocApiVersion100, out nint apiTable) != 1 || apiTable == 0)
                return false;

            if (GetEntry<RenderDocIsFrameCapturing>(apiTable, IsFrameCapturingIndex)() != 0)
                return false;

            if (!string.IsNullOrWhiteSpace(captureFilePathTemplate))
            {
                nint setPathAddress = Marshal.ReadIntPtr(apiTable, SetCaptureFilePathTemplateIndex * nint.Size);
                if (setPathAddress == 0)
                    return false;

                RenderDocSetCaptureFilePathTemplate setPath =
                    Marshal.GetDelegateForFunctionPointer<RenderDocSetCaptureFilePathTemplate>(setPathAddress);
                setPath(captureFilePathTemplate);
            }

            nint triggerAddress = Marshal.ReadIntPtr(apiTable, TriggerCaptureIndex * nint.Size);
            if (triggerAddress == 0)
                return false;

            Marshal.GetDelegateForFunctionPointer<RenderDocTriggerCapture>(triggerAddress)();
            return true;
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RenderDocGetApi(int version, out nint apiTable);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderDocSetCaptureFilePathTemplate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string captureFilePathTemplate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderDocTriggerCapture();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderDocStartFrameCapture(nint device, nint window);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint RenderDocIsFrameCapturing();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint RenderDocEndFrameCapture(nint device, nint window);
}
