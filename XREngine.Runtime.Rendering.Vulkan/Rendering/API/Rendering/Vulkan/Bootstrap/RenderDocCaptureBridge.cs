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

    /// <summary>
    /// Queues capture of the next presented frame when RenderDoc is attached.
    /// </summary>
    /// <param name="captureFilePathTemplate">
    /// Optional UTF-8 path template for the capture. RenderDoc appends its frame suffix.
    /// </param>
    /// <returns><see langword="true"/> when the request reached RenderDoc.</returns>
    public static bool TryTriggerCapture(string? captureFilePathTemplate = null)
    {
        if (!NativeLibrary.TryLoad("renderdoc.dll", out nint module))
            return false;

        try
        {
            if (!NativeLibrary.TryGetExport(module, "RENDERDOC_GetAPI", out nint getApiAddress))
                return false;

            RenderDocGetApi getApi = Marshal.GetDelegateForFunctionPointer<RenderDocGetApi>(getApiAddress);
            if (getApi(RenderDocApiVersion100, out nint apiTable) != 1 || apiTable == 0)
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
}
