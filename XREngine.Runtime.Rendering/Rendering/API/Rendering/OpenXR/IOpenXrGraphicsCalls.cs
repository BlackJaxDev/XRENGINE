namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Synchronous OpenXR calls used by a renderer while the runtime owns native handles.</summary>
public interface IOpenXrGraphicsCalls
{
    int CreateSession(nint graphicsBindingChain, out ulong sessionHandle);
    int DestroySession(ulong sessionHandle);
    int EnumerateSwapchainFormats(Span<long> formats, out uint count);
    int CreateSwapchain(in OpenXrSwapchainDescriptor descriptor, out ulong swapchainHandle);
    int EnumerateSwapchainImages(ulong swapchainHandle, uint capacity, nint pinnedRendererStorage, out uint count);
    int DestroySwapchain(ulong swapchainHandle);
    int BeginFrame();
    int AcquireSwapchainImage(ulong swapchainHandle, out uint imageIndex);
    int WaitSwapchainImage(ulong swapchainHandle, long timeoutNs);
    int ReleaseSwapchainImage(ulong swapchainHandle);
    int EndFrame(bool submitLayer);
}
