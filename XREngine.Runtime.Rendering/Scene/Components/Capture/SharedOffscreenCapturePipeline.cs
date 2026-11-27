using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights;

/// <summary>
/// Shares one offscreen <see cref="XRViewport"/>, <see cref="XRCamera"/>, and render pipeline
/// across multiple rotating capture output textures so ring-buffered mirrors do not duplicate
/// internal render targets, descriptor pools, or BRDF precompute passes per slot.
/// </summary>
internal sealed class SharedOffscreenCapturePipeline
{
    private readonly ulong _outputIdentity = AdvancedOffscreenTextureCaptureComponent.AllocateOutputIdentity();
    private readonly DrivenWorldTransform _captureTransform = new();
    private XRCamera? _captureCamera;
    private XRViewport? _viewport;
    private XRTexture2D? _scratchColorTexture;
    private XRRenderBuffer? _scratchDepthBuffer;
    private XRFrameBuffer? _scratchFrameBuffer;
    private RenderPipelineOffscreenIntent _registeredIntent;
    private bool _registeredOwner;
    private bool _usesAdvancedPipeline;
    private uint _width;
    private uint _height;

    internal ulong OutputIdentity => _outputIdentity;
    internal DrivenWorldTransform CaptureTransform => _captureTransform;
    internal XRCamera? CaptureCamera => _captureCamera;
    internal XRViewport? Viewport => _viewport;
    /// <summary>
    /// Single scratch framebuffer shared by standalone HDR color captures so rotating ring slots
    /// keep one constant <c>OutputFrameBufferIdentity</c> and reuse one Vulkan resource planner.
    /// </summary>
    internal XRFrameBuffer? ScratchFrameBuffer => _scratchFrameBuffer;
    internal uint Width => _width;
    internal uint Height => _height;

    /// <summary>
    /// Creates or reuses the shared capture viewport and pipeline for the requested extent and
    /// offscreen intent on the render thread.
    /// </summary>
    internal XRViewport EnsureResources(
        IRuntimeWorldContext world,
        uint width,
        uint height,
        bool usesAdvancedPipeline,
        in RenderPipelineOffscreenIntent intent,
        Action<XRViewport>? configureViewport)
    {
        if (_viewport is not null &&
            _width == width &&
            _height == height &&
            _usesAdvancedPipeline == usesAdvancedPipeline &&
            (!usesAdvancedPipeline || _registeredIntent == intent))
        {
            _viewport.WorldInstanceOverride = world.GetRenderWorld();
            return _viewport;
        }

        Destroy();

        _captureCamera ??= new XRCamera(_captureTransform,
            new XRPerspectiveCameraParameters(90.0f, 1.0f, 0.1f, 10000.0f));
        if (usesAdvancedPipeline)
        {
            RuntimeEngine.RegisterAdvancedOffscreenOwner(_outputIdentity, intent);
            _registeredIntent = intent;
            _registeredOwner = true;
        }

        RenderPipelineRequest request = usesAdvancedPipeline
            ? RenderPipelineRequest.AdvancedOffscreenCapture(intent, outputId: _outputIdentity)
            : RenderPipelineRequest.OffscreenCapture();

        XRViewport viewport = new(null, width, height)
        {
            AllowAutomaticInternalResolution = false,
            AllowCameraAspectRatioUpdates = false,
            SetRenderPipelineFromCamera = false,
        };
        if (viewport.Width != width || viewport.Height != height ||
            viewport.InternalWidth != (int)width || viewport.InternalHeight != (int)height)
            viewport.Resize(width, height, setInternalResolution: true,
                internalResolutionWidth: checked((int)width),
                internalResolutionHeight: checked((int)height));

        viewport.PipelineRequest = request;
        viewport.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(request);
        viewport.WorldInstanceOverride = world.GetRenderWorld();
        viewport.Camera = _captureCamera;
        viewport.AutomaticallyCollectVisible = false;
        viewport.AutomaticallySwapBuffers = false;
        viewport.AllowUIRender = false;
        viewport.CullWithFrustum = true;
        configureViewport?.Invoke(viewport);

        if (intent.Output == ERenderPipelineOffscreenOutput.HdrColor)
        {
            _scratchColorTexture = new XRTexture2D(
                width,
                height,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                AutoGenerateMipmaps = false,
                Name = "SharedOffscreenCapturePipeline.ScratchColor",
            };
            _scratchDepthBuffer = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8);
            _scratchFrameBuffer = new XRFrameBuffer { Name = "SharedOffscreenCapturePipeline.ScratchTarget" };
            _scratchFrameBuffer.SetRenderTargets(
                (_scratchColorTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (_scratchDepthBuffer, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
        }

        _viewport = viewport;
        _usesAdvancedPipeline = usesAdvancedPipeline;
        _width = width;
        _height = height;
        return viewport;
    }

    /// <summary>Destroys the shared viewport, scratch framebuffer, and offscreen owner registration.</summary>
    internal void Destroy()
    {
        _viewport?.Destroy();
        _viewport = null;
        _scratchFrameBuffer?.Destroy();
        _scratchFrameBuffer = null;
        _scratchDepthBuffer?.Destroy();
        _scratchDepthBuffer = null;
        _scratchColorTexture?.Destroy();
        _scratchColorTexture = null;
        if (_registeredOwner)
        {
            RuntimeEngine.UnregisterAdvancedOffscreenOwner(_outputIdentity, _registeredIntent);
            _registeredOwner = false;
        }
        _width = 0u;
        _height = 0u;
    }
}
