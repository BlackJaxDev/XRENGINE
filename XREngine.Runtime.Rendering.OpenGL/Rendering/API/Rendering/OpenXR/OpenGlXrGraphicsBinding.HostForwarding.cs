using Silk.NET.OpenXR;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.OpenGL;

using OpenXrEyeSwapchainExtent = OpenXRAPI.OpenXrEyeSwapchainExtent;

/// <summary>
/// Maps the legacy backend implementation names to the narrow, backend-neutral
/// host surface owned by <see cref="OpenXRAPI"/>.
/// </summary>
internal sealed unsafe partial class OpenGlXrGraphicsBinding
{
    private OpenXRAPI.OpenXrGraphicsBindingHost BindingHost
        => Host.GraphicsBindingHost;

    private XR Api => BindingHost.Api;
    private XRWindow? Window => BindingHost.Window;
    private ref Instance _instance => ref BindingHost.Instance;
    private ref Session _session => ref BindingHost.Session;
    private ulong _systemId => BindingHost.SystemId;
    private uint _viewCount => BindingHost.ViewCount;
    private ViewConfigurationView[] _viewConfigViews => BindingHost.ViewConfigurationViews;
    private Swapchain[] _swapchains => BindingHost.Swapchains;
    private uint[] _swapchainImageCounts => BindingHost.SwapchainImageCounts;
    private IRuntimeRenderWorld? _openXrFrameWorld => BindingHost.FrameWorld;
    private XRCamera? _openXrLeftEyeCamera => BindingHost.LeftEyeCamera;
    private XRCamera? _openXrRightEyeCamera => BindingHost.RightEyeCamera;
    private XRViewport? _openXrLeftViewport => BindingHost.LeftViewport;
    private XRViewport? _openXrRightViewport => BindingHost.RightViewport;
    private int _openXrPendingFrameNumber => BindingHost.PendingFrameNumber;

    private static bool OpenXrDebugLifecycle
        => RuntimeEngine.Rendering.Settings.OpenXrDebugLifecycle;
    private static bool OpenXrDebugGl
        => RuntimeEngine.Rendering.Settings.OpenXrDebugGl;
    private static bool OpenXrDebugClearOnly
        => RuntimeEngine.Rendering.Settings.OpenXrDebugClearOnly;
    private const int OpenXrDebugLogEveryNFrames = 60;

    private Result CheckResult(Result result, string operation)
        => BindingHost.CheckResult(result, operation);

    private bool TryResolveOpenXrFoveation(
        ERenderLibrary backend,
        out VrFoveationResolution resolution)
        => BindingHost.TryResolveOpenXrFoveation(backend, out resolution);

    private void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
        => BindingHost.InitializeOpenXrViewsForActiveConfiguration(backendLabel);

    private static bool IsLeftEyeLikeOpenXrView(uint viewIndex)
        => OpenXRAPI.OpenXrGraphicsBindingHost.IsLeftEyeLikeOpenXrView(viewIndex);

    private XRViewport? GetOpenXrEyeViewport(uint viewIndex)
        => BindingHost.GetOpenXrEyeViewport(viewIndex);

    private XRCamera? GetOpenXrEyeCamera(uint viewIndex)
        => BindingHost.GetOpenXrEyeCamera(viewIndex);

    private XRTexture2D? GetOpenXrPreviewTexture(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _previewLeftEyeTexture
            : _previewRightEyeTexture;

    private static void EnsureOpenXrViewportExtent(
        XRViewport viewport,
        uint width,
        uint height)
        => OpenXRAPI.OpenXrGraphicsBindingHost.EnsureOpenXrViewportExtent(
            viewport,
            width,
            height);

    private void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
        => BindingHost.ApplyOpenXrEyePoseForRenderThread(viewIndex);

    private OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex)
        => BindingHost.ResolveOpenXrEyeSwapchainExtent(viewIndex);

    private uint GetOpenXrSwapchainWidth(uint viewIndex)
        => BindingHost.GetOpenXrSwapchainWidth(viewIndex);

    private uint GetOpenXrSwapchainHeight(uint viewIndex)
        => BindingHost.GetOpenXrSwapchainHeight(viewIndex);

    private void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height)
        => BindingHost.RecordOpenXrSwapchainExtent(viewIndex, width, height);

    private void LogOpenXrEyeSwapchainExtent(
        string backend,
        uint viewIndex,
        OpenXrEyeSwapchainExtent extent)
        => BindingHost.LogOpenXrEyeSwapchainExtent(backend, viewIndex, extent);

    private void RecordSmokeSwapchain(
        string backend,
        int viewIndex,
        uint width,
        uint height,
        long format,
        uint sampleCount,
        uint imageCount)
        => BindingHost.RecordSmokeSwapchain(
            backend,
            viewIndex,
            width,
            height,
            format,
            sampleCount,
            imageCount);

    private void RecordSmokeSwapchainsCreated()
        => BindingHost.RecordSmokeSwapchainsCreated();

    private void RecordSmokeDesktopMirrorComposed()
        => BindingHost.RecordSmokeDesktopMirrorComposed();

    private static bool ShouldLogLifecycle(int frameNumber)
        => OpenXRAPI.OpenXrGraphicsBindingHost.ShouldLogLifecycle(frameNumber);

    private static string? TryGetOpenXRActiveRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            const string keyPath = @"SOFTWARE\Khronos\OpenXR\1";
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue("ActiveRuntime") as string;
        }
        catch
        {
            return null;
        }
    }
}
