using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Maps renderer operations to the runtime-owned graphics host.
/// </summary>
internal sealed unsafe partial class OpenGlXrGraphicsBinding
{
    private IOpenXrGraphicsHost BindingHost => Host;

    private XRWindow? Window => BindingHost.Window;
    private ulong _systemId => BindingHost.SystemId;
    private uint _viewCount => BindingHost.ViewCount;
    private ReadOnlySpan<OpenXrViewConfiguration> _viewConfigViews => BindingHost.ViewConfigurationViews;
    private ReadOnlySpan<ulong> _swapchains => BindingHost.Swapchains;
    private ReadOnlySpan<uint> _swapchainImageCounts => BindingHost.SwapchainImageCounts;
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

    private int CheckResult(int result, string operation)
        => BindingHost.CheckResult(result, operation);

    private bool TryResolveOpenXrFoveation(
        ERenderLibrary backend,
        out VrFoveationResolution resolution)
        => BindingHost.TryResolveOpenXrFoveation(backend, out resolution);

    private void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
        => BindingHost.InitializeOpenXrViewsForActiveConfiguration(backendLabel);

    private bool IsLeftEyeLikeOpenXrView(uint viewIndex)
        => BindingHost.IsLeftEyeLikeOpenXrView(viewIndex);

    private XRViewport? GetOpenXrEyeViewport(uint viewIndex)
        => BindingHost.GetOpenXrEyeViewport(viewIndex);

    private XRCamera? GetOpenXrEyeCamera(uint viewIndex)
        => BindingHost.GetOpenXrEyeCamera(viewIndex);

    private XRTexture2D? GetOpenXrPreviewTexture(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _previewLeftEyeTexture
            : _previewRightEyeTexture;

    private void EnsureOpenXrViewportExtent(
        XRViewport viewport,
        uint width,
        uint height)
        => BindingHost.EnsureOpenXrViewportExtent(
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

    private bool ShouldLogLifecycle(int frameNumber)
        => BindingHost.ShouldLogLifecycle(frameNumber);

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
