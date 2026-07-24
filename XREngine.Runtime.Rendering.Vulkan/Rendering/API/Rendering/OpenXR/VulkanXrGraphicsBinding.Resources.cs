using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanXrGraphicsBinding
{
    private bool TryRenderVulkanDesktopMirrorComposition(
        VulkanRenderer renderer,
        uint targetWidth,
        uint targetHeight)
    {
        if (_viewportMirrorFbo is null || _viewportMirrorColor is null)
            return false;

        uint resolvedTargetWidth = Math.Max(1u, targetWidth);
        uint resolvedTargetHeight = Math.Max(1u, targetHeight);
        if (_viewportMirrorWidth == 0 || _viewportMirrorHeight == 0)
            return false;

        try
        {
            renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true);
            if (_viewportMirrorDepth is not null)
                renderer.GetOrCreateAPIRenderObject(_viewportMirrorDepth, generateNow: true);
            renderer.GetOrCreateAPIRenderObject(_viewportMirrorFbo, generateNow: true);

            XRRenderPipelineInstance? mirrorPipeline =
                _openXrLeftViewport?.RenderPipelineInstance ??
                _openXrRightViewport?.RenderPipelineInstance;
            using var pipelineScope =
                RuntimeEngine.Rendering.State.PushRenderingPipelineOverride(mirrorPipeline);
            using var passScope =
                RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(
                    (int)EDefaultRenderPass.PostRender);
            renderer.Blit(
                _viewportMirrorFbo,
                null,
                0,
                0,
                _viewportMirrorWidth,
                _viewportMirrorHeight,
                0,
                0,
                resolvedTargetWidth,
                resolvedTargetHeight,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);
            renderer.TrackWindowPresentSource(_viewportMirrorColor, _viewportMirrorFbo);

            RecordSmokeDesktopMirrorComposed();
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.DesktopMirrorCompositionFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan desktop mirror composition failed: {0}",
                ex.Message);
            return false;
        }
    }

    private void EnsureViewportMirrorTargets(
        AbstractRenderer renderer,
        uint width,
        uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_viewportMirrorFbo is not null &&
            _viewportMirrorWidth == width &&
            _viewportMirrorHeight == height)
        {
            return;
        }

        DestroyViewportMirrorTargets();

        _viewportMirrorWidth = width;
        _viewportMirrorHeight = height;

        _viewportMirrorColor = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        _viewportMirrorColor.Resizable = true;
        _viewportMirrorColor.MinFilter = ETexMinFilter.Linear;
        _viewportMirrorColor.MagFilter = ETexMagFilter.Linear;
        _viewportMirrorColor.UWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.VWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.Name = "OpenXRViewportMirrorColor";

        _viewportMirrorDepth = new XRRenderBuffer(
            width,
            height,
            ERenderBufferStorage.Depth24Stencil8,
            EFrameBufferAttachment.DepthStencilAttachment)
        {
            Name = "OpenXRViewportMirrorDepth"
        };

        _viewportMirrorFbo = new XRFrameBuffer(
            (_viewportMirrorColor, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (_viewportMirrorDepth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = "OpenXRViewportMirrorFBO"
        };

        renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorDepth, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorFbo, generateNow: true);
    }

    private void EnsureOpenXrPreviewTargets(
        AbstractRenderer renderer,
        uint width,
        uint height)
        => EnsureOpenXrPreviewTargets(
            renderer,
            width,
            height,
            EPixelInternalFormat.Rgba8,
            ESizedInternalFormat.Rgba8);

    private void EnsureOpenXrPreviewTargets(
        AbstractRenderer renderer,
        uint width,
        uint height,
        EPixelInternalFormat internalFormat,
        ESizedInternalFormat sizedFormat)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_previewLeftEyeTexture is not null &&
            _previewRightEyeTexture is not null &&
            _previewEyeTextureWidth == width &&
            _previewEyeTextureHeight == height &&
            _previewEyeTextureInternalFormat == internalFormat &&
            _previewEyeTextureSizedFormat == sizedFormat)
        {
            return;
        }

        DestroyOpenXrPreviewTargets();

        _previewEyeTextureWidth = width;
        _previewEyeTextureHeight = height;
        _previewEyeTextureInternalFormat = internalFormat;
        _previewEyeTextureSizedFormat = sizedFormat;
        _previewLeftEyeTexture = CreateOpenXrPreviewTexture(
            width,
            height,
            internalFormat,
            sizedFormat,
            "OpenXRPreviewLeftEyeColor");
        _previewRightEyeTexture = CreateOpenXrPreviewTexture(
            width,
            height,
            internalFormat,
            sizedFormat,
            "OpenXRPreviewRightEyeColor");

        renderer.GetOrCreateAPIRenderObject(_previewLeftEyeTexture, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_previewRightEyeTexture, generateNow: true);
    }

    private static XRTexture2D CreateOpenXrPreviewTexture(
        uint width,
        uint height,
        EPixelInternalFormat internalFormat,
        ESizedInternalFormat sizedFormat,
        string name)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        texture.SizedInternalFormat = sizedFormat;
        texture.Resizable = true;
        texture.MinFilter = ETexMinFilter.Linear;
        texture.MagFilter = ETexMagFilter.Linear;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.Name = name;
        return texture;
    }

    private void DestroyOpenXrPreviewTargets()
    {
        try
        {
            _previewLeftEyeTexture?.Destroy();
            _previewLeftEyeTexture = null;
            _previewRightEyeTexture?.Destroy();
            _previewRightEyeTexture = null;
        }
        catch
        {
            // Best-effort cleanup during renderer teardown.
        }

        _previewEyeTextureWidth = 0;
        _previewEyeTextureHeight = 0;
        _previewEyeTextureInternalFormat = EPixelInternalFormat.Rgba8;
        _previewEyeTextureSizedFormat = ESizedInternalFormat.Rgba8;
    }

    private void DestroyViewportMirrorTargets()
    {
        try
        {
            _viewportMirrorFbo?.Destroy();
            _viewportMirrorFbo = null;
            _viewportMirrorDepth?.Destroy();
            _viewportMirrorDepth = null;
            _viewportMirrorColor?.Destroy();
            _viewportMirrorColor = null;
        }
        catch
        {
            // Best-effort cleanup during renderer teardown.
        }

        _viewportMirrorWidth = 0;
        _viewportMirrorHeight = 0;
    }
}
