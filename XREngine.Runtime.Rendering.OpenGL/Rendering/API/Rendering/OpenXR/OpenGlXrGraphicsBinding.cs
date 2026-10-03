using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// OpenGL implementation of the OpenXR graphics binding contract.
/// </summary>
internal sealed unsafe partial class OpenGlXrGraphicsBinding : IXrGraphicsBinding
{
    private IOpenXrGraphicsHost? _host;
    private GL? _gl;
    private readonly SwapchainImageOpenGLKHR*[] _swapchainImagesGL =
        new SwapchainImageOpenGLKHR*[RenderFrameViewSet.MaxViewCount];
    private readonly uint[]?[] _swapchainFramebuffers =
        new uint[]?[RenderFrameViewSet.MaxViewCount];
    private nint _openXrSessionHdc;
    private nint _openXrSessionHglrc;
    private string _openXrSessionGlBindingTag = string.Empty;
    private uint _blitReadFbo;
    private uint _blitDrawFbo;
    private nint _blitFboHglrc;
    private uint _openXrCurrentSwapchainFramebuffer;
    private XRTexture2D? _viewportMirrorColor;
    private XRRenderBuffer? _viewportMirrorDepth;
    private XRFrameBuffer? _viewportMirrorFbo;
    private uint _viewportMirrorWidth;
    private uint _viewportMirrorHeight;
    private XRTexture2D? _previewLeftEyeTexture;
    private XRTexture2D? _previewRightEyeTexture;
    private uint _previewEyeTextureWidth;
    private uint _previewEyeTextureHeight;
    private EPixelInternalFormat _previewEyeTextureInternalFormat = EPixelInternalFormat.Rgba8;
    private ESizedInternalFormat _previewEyeTextureSizedFormat = ESizedInternalFormat.Rgba8;
    private int _openXrDebugFrameIndex;
    private ulong _previewLeftEyeFrameId;
    private ulong _previewRightEyeFrameId;

    private IOpenXrGraphicsHost Host
        => _host ?? throw new InvalidOperationException("The OpenGL OpenXR binding is not attached to an API host.");

    private void Attach(IOpenXrGraphicsHost host)
        => _host = host;

    public RendererBackendId BackendId => RendererBackendId.OpenGL;
    public string BackendName => "OpenGL";

    public bool IsCompatible(AbstractRenderer renderer) => renderer is OpenGLRenderer;

    public bool RequiresDeferredSessionCreation => true;
    public bool RequiresRenderThreadForTeardown => true;
    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public ulong PreviewLeftEyeFrameId => Volatile.Read(ref _previewLeftEyeFrameId);
    public ulong PreviewRightEyeFrameId => Volatile.Read(ref _previewRightEyeFrameId);
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

    private void ClearPreviewEyeFrameId(uint viewIndex)
    {
        if (viewIndex == 0)
            Volatile.Write(ref _previewLeftEyeFrameId, 0);
        else if (viewIndex == 1)
            Volatile.Write(ref _previewRightEyeFrameId, 0);
    }

    private void RecordPreviewEyeCopyIssued(uint viewIndex)
    {
        ulong renderFrameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
        if (viewIndex == 0)
            Volatile.Write(ref _previewLeftEyeFrameId, renderFrameId);
        else if (viewIndex == 1)
            Volatile.Write(ref _previewRightEyeFrameId, renderFrameId);
    }

    public bool TryCreateSession(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        Attach(host);
        CreateOpenGLSession((OpenGLRenderer)renderer);
        return true;
    }

    public void CreateSwapchains(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        Attach(host);
        InitializeOpenGLSwapchains((OpenGLRenderer)renderer);
    }

    public void CleanupSwapchains(IOpenXrGraphicsHost host)
    {
        Attach(host);
        EnsureCurrentContextForResourceDeletion();

        for (int i = 0; i < _swapchainFramebuffers.Length; i++)
        {
            uint[]? framebuffers = _swapchainFramebuffers[i];
            if (framebuffers is not null && _gl is not null && wglGetCurrentContext() != 0)
            {
                foreach (uint framebuffer in framebuffers)
                {
                    try
                    {
                        _gl.DeleteFramebuffer(framebuffer);
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            if (_swapchainImagesGL[i] is not null)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal((nint)_swapchainImagesGL[i]);
                _swapchainImagesGL[i] = null;
            }

            _swapchainFramebuffers[i] = null;
        }
    }

    public bool WaitForGpuIdle(IOpenXrGraphicsHost host, AbstractRenderer renderer)
    {
        Attach(host);
        _gl?.Finish();
        return true;
    }

    public int AcquireSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, out uint imageIndex)
    {
        Attach(host);
        return host.GraphicsCalls.AcquireSwapchainImage(swapchain, out imageIndex);
    }

    public int WaitSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, long timeoutNs)
    {
        Attach(host);
        return host.GraphicsCalls.WaitSwapchainImage(swapchain, timeoutNs);
    }

    public int ReleaseSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain)
    {
        Attach(host);
        return host.GraphicsCalls.ReleaseSwapchainImage(swapchain);
    }

    public void RenderViews(IOpenXrGraphicsHost host, uint viewIndex)
    {
        // Rendering remains coordinated by the backend-neutral frame lifecycle.
    }

    public bool TryRenderEye(
        IOpenXrGraphicsHost host,
        uint viewIndex,
        uint imageIndex,
        OpenXrRenderToEyeCallback? renderCallback)
    {
        Attach(host);
        if (_gl is null)
            return false;

        uint[]? swapchainFramebuffers = _swapchainFramebuffers[viewIndex];
        SwapchainImageOpenGLKHR* swapchainImages = _swapchainImagesGL[viewIndex];
        if (swapchainFramebuffers is null || swapchainImages is null)
            return false;

        if (imageIndex >= swapchainFramebuffers.Length)
        {
            throw new InvalidOperationException(
                $"OpenXR acquired swapchain image index {imageIndex}, but view {viewIndex} only has " +
                $"{swapchainFramebuffers.Length} OpenGL framebuffers.");
        }

        _openXrCurrentSwapchainFramebuffer = swapchainFramebuffers[imageIndex];
        try
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _openXrCurrentSwapchainFramebuffer);
            _gl.Viewport(0, 0, GetOpenXrSwapchainWidth(viewIndex), GetOpenXrSwapchainHeight(viewIndex));
            _gl.Disable(EnableCap.ScissorTest);
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(true);
            (renderCallback ?? RenderViewportsToSwapchain)(swapchainImages[imageIndex].Image, viewIndex);
            host.StageProjectionView(viewIndex);
            return true;
        }
        finally
        {
            _openXrCurrentSwapchainFramebuffer = 0;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    public void Flush(IOpenXrGraphicsHost host)
    {
        Attach(host);
        _gl?.Flush();
    }

    public bool TryRenderDesktopMirrorComposition(
        IOpenXrGraphicsHost host,
        uint targetWidth,
        uint targetHeight)
    {
        Attach(host);
        return TryRenderDesktopMirrorComposition(targetWidth, targetHeight);
    }

    public void DestroyBackendResources(IOpenXrGraphicsHost host)
    {
        Attach(host);
        EnsureCurrentContextForResourceDeletion();

        if (_gl is not null)
        {
            if (_blitReadFbo != 0)
                _gl.DeleteFramebuffer(_blitReadFbo);
            if (_blitDrawFbo != 0)
                _gl.DeleteFramebuffer(_blitDrawFbo);
        }

        _blitReadFbo = 0;
        _blitDrawFbo = 0;
        _viewportMirrorFbo?.Destroy();
        _viewportMirrorDepth?.Destroy();
        _viewportMirrorColor?.Destroy();
        _viewportMirrorFbo = null;
        _viewportMirrorDepth = null;
        _viewportMirrorColor = null;
        DestroyOpenXrPreviewTargets();
    }

    private void EnsureCurrentContextForResourceDeletion()
    {
        if (wglGetCurrentContext() != 0 || Window?.DesktopGlContext is not { } desktopGlContext)
            return;

        try
        {
            desktopGlContext.AssertOwnerThread();
            desktopGlContext.MakeCurrent();
        }
        catch
        {
            // Resource teardown is best effort during runtime loss and process shutdown.
        }
    }
}
