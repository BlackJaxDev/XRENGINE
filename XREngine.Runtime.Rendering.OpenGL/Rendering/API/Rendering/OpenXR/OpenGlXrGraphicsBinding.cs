using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Silk.NET.Windowing;
using XREngine.Data.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// OpenGL implementation of the OpenXR graphics binding contract.
/// </summary>
internal sealed unsafe partial class OpenGlXrGraphicsBinding : IXrGraphicsBinding
{
    private OpenXRAPI? _host;
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

    private OpenXRAPI Host
        => _host ?? throw new InvalidOperationException("The OpenGL OpenXR binding is not attached to an API host.");

    private void Attach(OpenXRAPI api)
        => _host = api;

    public RendererBackendId BackendId => RendererBackendId.OpenGL;
    public string BackendName => "OpenGL";

    public bool IsCompatible(AbstractRenderer renderer) => renderer is OpenGLRenderer;

    public bool RequiresDeferredSessionCreation => true;
    public bool RequiresRenderThreadForTeardown => true;
    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

    public bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        CreateOpenGLSession((OpenGLRenderer)renderer);
        return true;
    }

    public void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        InitializeOpenGLSwapchains((OpenGLRenderer)renderer);
    }

    public void CleanupSwapchains(OpenXRAPI api)
    {
        Attach(api);
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

    public bool WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        _gl?.Finish();
        return true;
    }

    public Result AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex)
    {
        SwapchainImageAcquireInfo acquireInfo = new()
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };
        imageIndex = 0;
        return api.Api.AcquireSwapchainImage(swapchain, in acquireInfo, ref imageIndex);
    }

    public Result WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs)
    {
        SwapchainImageWaitInfo waitInfo = new()
        {
            Type = StructureType.SwapchainImageWaitInfo,
            Timeout = timeoutNs
        };
        return api.Api.WaitSwapchainImage(swapchain, in waitInfo);
    }

    public Result ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain)
    {
        SwapchainImageReleaseInfo releaseInfo = new()
        {
            Type = StructureType.SwapchainImageReleaseInfo
        };
        return api.Api.ReleaseSwapchainImage(swapchain, in releaseInfo);
    }

    public void RenderViews(
        OpenXRAPI api,
        in CompositionLayerProjectionView projectionView,
        uint viewIndex)
    {
        // Rendering remains coordinated by the backend-neutral frame lifecycle.
    }

    public bool TryRenderEye(
        OpenXRAPI api,
        uint viewIndex,
        uint imageIndex,
        OpenXRAPI.DelRenderToFBO? renderCallback)
    {
        Attach(api);
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
            return true;
        }
        finally
        {
            _openXrCurrentSwapchainFramebuffer = 0;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
    }

    public void Flush(OpenXRAPI api)
    {
        Attach(api);
        _gl?.Flush();
    }

    public bool TryRenderDesktopMirrorComposition(
        OpenXRAPI api,
        uint targetWidth,
        uint targetHeight)
    {
        Attach(api);
        return TryRenderDesktopMirrorComposition(targetWidth, targetHeight);
    }

    public void DestroyBackendResources(OpenXRAPI api)
    {
        Attach(api);
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
        if (wglGetCurrentContext() != 0 || Window is null)
            return;

        try
        {
            Window.Window.MakeCurrent();
        }
        catch
        {
            // Resource teardown is best effort during runtime loss and process shutdown.
        }
    }
}
