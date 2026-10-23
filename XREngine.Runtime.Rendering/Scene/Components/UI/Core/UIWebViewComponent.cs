using XREngine.Extensions;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Represents a frame of web content. PixelDataPtr points to native memory owned by the backend.
    /// Only valid during the current render call - do not store.
    /// </summary>
    public readonly record struct WebFrame(
        int Width,
        int Height,
        int Stride,
        EPixelFormat PixelFormat,
        EPixelType PixelType,
        IntPtr PixelDataPtr,
        int ByteLength);

    public enum WebMouseButton
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 3,
    }

    /// <summary>
    /// Backend interface for rendering web content into a UI texture/FBO.
    /// </summary>
    public interface IWebRendererBackend : IDisposable
    {
        bool IsInitialized { get; }
        bool SupportsFramebuffer { get; }

        void Initialize(uint width, uint height, bool transparentBackground);
        void Resize(uint width, uint height);
        void LoadUrl(string url);
        void Update(double deltaSeconds);
        void Render();
        bool TryGetFrame(out WebFrame frame);
        void SetTargetFramebuffer(uint framebufferId);
        void SendMouseMove(int x, int y);
        void SendMouseButton(bool pressed, WebMouseButton button, int x, int y);
        void SendScroll(int deltaX, int deltaY);
    }

    internal sealed class NullWebRendererBackend : IWebRendererBackend
    {
        public bool IsInitialized => true;
        public bool SupportsFramebuffer => false;

        public void Initialize(uint width, uint height, bool transparentBackground) { }
        public void Resize(uint width, uint height) { }
        public void LoadUrl(string url) { }
        public void Update(double deltaSeconds) { }
        public void Render() { }
        public bool TryGetFrame(out WebFrame frame)
        {
            frame = default;
            return false;
        }
        public void SetTargetFramebuffer(uint framebufferId) { }
        public void SendMouseMove(int x, int y) { }
        public void SendMouseButton(bool pressed, WebMouseButton button, int x, int y) { }
        public void SendScroll(int deltaX, int deltaY) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Displays a web renderer surface in UI by rendering into a framebuffer-backed texture.
    /// </summary>
    public sealed class UIWebViewComponent : UIMaterialComponent
    {
        private XRMaterialFrameBuffer _fbo;
        private const int MaximumWebSurfaces = 4;
        private readonly List<WebSurface> _webSurfaces = [];
        private WebSurface? _publishedWebSurface;
        private ulong _nextWebSurfaceGeneration;
        private string? _webProducerFailure;
        private readonly Action _invalidateWebVisual;
        private int _webVisualInvalidationQueued;
        private int _webProducerDestroyed;
        private readonly object _webProducerGate = new();
        private bool _webTransientDisposed;
        private int _webProducerActive;
        private bool _webReleasePending;
        private bool _webSurfacesReleased;
        private bool _webTransientDisposePending;

        private sealed class WebSurface(XRMaterialFrameBuffer fbo, XRTexture2D texture) : IUIDrawProducerLease
        {
            private int _references = 1;
            private long _generation;
            public XRMaterialFrameBuffer Fbo { get; } = fbo;
            public XRTexture2D Texture { get; } = texture;
            public Mipmap2D? Mipmap { get; set; }
            public Mipmap2D[] MipmapArray { get; } = new Mipmap2D[1];
            public DataSource? CpuPixels { get; set; }
            public ulong Generation => unchecked((ulong)Volatile.Read(ref _generation));
            public bool IsPoolOnly => Volatile.Read(ref _references) == 1;
            public void Publish(ulong generation) => Volatile.Write(ref _generation, checked((long)generation));
            public void Retain()
            {
                int current;
                do
                {
                    current = Volatile.Read(ref _references);
                    ObjectDisposedException.ThrowIf(current == 0, this);
                } while (Interlocked.CompareExchange(ref _references, checked(current + 1), current) != current);
            }
            public void Release()
            {
                int remaining = Interlocked.Decrement(ref _references);
                if (remaining < 0)
                    throw new InvalidOperationException("Web surface was released twice.");
                if (remaining != 0)
                    return;
                if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                    DestroyResources();
                else
                    RuntimeEngine.EnqueueRenderThreadTask(DestroyResources, "UI.Web.DestroySurface",
                        RenderThreadJobKind.RenderPipelineResource);
            }

            private void DestroyResources()
            {
                if (!ReferenceEquals(Mipmap?.Data, CpuPixels))
                    Mipmap?.Data?.Dispose();
                CpuPixels?.Dispose();
                foreach (Mipmap2D mip in Texture.Mipmaps)
                    if (!ReferenceEquals(mip.Data, CpuPixels) && !ReferenceEquals(mip.Data, Mipmap?.Data))
                        mip.Data?.Dispose();
                Fbo.Destroy();
                Texture.Destroy();
            }
        }
        private IWebRendererBackend _backend = new NullWebRendererBackend();
        private bool _rendering;
        private bool _pendingReinitialize = true;
        private bool _pendingLoadUrl;
        private bool _pendingResize;
        private uint _requestedBackingWidth = 1;
        private uint _requestedBackingHeight = 1;
        private uint _backingWidth = 1;
        private uint _backingHeight = 1;
        private int _currentPboIndex;
        private readonly XRDataBuffer<byte>?[] _pboBuffers = [null, null];
        private bool _loggedBackendInit;
        private bool _loggedTargetFramebuffer;
        private bool _loggedSoftwareFrame;
        private bool _allowSoftwareFallback;
        private string? _lastBackendFailure;

        /// <summary>Allows an accelerated backend failure to switch to the explicitly installed software backend.</summary>
        public bool AllowSoftwareFallback
        {
            get => _allowSoftwareFallback;
            set => SetField(ref _allowSoftwareFallback, value);
        }

        /// <summary>Describes the most recent web backend initialization failure.</summary>
        public string? LastBackendFailure => _lastBackendFailure;

        private string? _url;
        public string? Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        private bool _transparentBackground = true;
        public bool TransparentBackground
        {
            get => _transparentBackground;
            set => SetField(ref _transparentBackground, value);
        }

        public IWebRendererBackend Backend
        {
            get => _backend;
            set => SetField(ref _backend, value ?? new NullWebRendererBackend());
        }

        private bool _usePboUpload = true;
        public bool UsePboUpload
        {
            get => _usePboUpload;
            set => SetField(ref _usePboUpload, value);
        }

        public UIWebViewComponent() : base(CreateWebMaterial(), true)
        {
            _invalidateWebVisual = InvalidateWebVisual;
            DisableBatching = true;
            XRTexture2D texture = (XRTexture2D)Material!.Textures[0]!;
            _fbo = CreateWebFramebuffer(texture);
            WebSurface placeholder = new(_fbo, texture);
            placeholder.Publish(++_nextWebSurfaceGeneration);
            _webSurfaces.Add(placeholder);
            Volatile.Write(ref _publishedWebSurface, placeholder);
        }

        private XRMaterialFrameBuffer CreateWebFramebuffer(XRTexture2D texture)
        {
            XRMaterialFrameBuffer fbo = new(Material, deriveRenderTargetsFromMaterial: false);
            fbo.SetRenderTargets((texture, EFrameBufferAttachment.ColorAttachment0, 0, -1));
            return fbo;
        }

        private WebSurface? AcquireWebSurface(uint width, uint height)
        {
            WebSurface? published = Volatile.Read(ref _publishedWebSurface);
            foreach (WebSurface surface in _webSurfaces)
            {
                if (ReferenceEquals(surface, published) || !surface.IsPoolOnly)
                    continue;
                if (surface.Texture.Width != width || surface.Texture.Height != height)
                    surface.Texture.Resize(width, height);
                return surface;
            }
            if (_webSurfaces.Count >= MaximumWebSurfaces)
                return null;
            XRTexture2D texture = CreateWebTexture(width, height);
            WebSurface created = new(CreateWebFramebuffer(texture), texture);
            _webSurfaces.Add(created);
            return created;
        }

        private void PublishWebSurface(WebSurface surface)
        {
            if (Volatile.Read(ref _webProducerDestroyed) != 0)
                return;
            surface.Publish(++_nextWebSurfaceGeneration);
            Volatile.Write(ref _publishedWebSurface, surface);
            _webProducerFailure = null;
            if (Interlocked.Exchange(ref _webVisualInvalidationQueued, 1) == 0)
                RuntimeEngine.EnqueueAppThreadTask(_invalidateWebVisual, "UI.Web.FramePublished");
        }

        private void InvalidateWebVisual()
        {
            Interlocked.Exchange(ref _webVisualInvalidationQueued, 0);
            if (Volatile.Read(ref _webProducerDestroyed) != 0)
                return;
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }

        private static XRTexture2D CreateWebTexture(uint width, uint height)
            => XRTexture2D.CreateFrameBufferTexture(width, height, EPixelInternalFormat.Rgba8,
                EPixelFormat.Bgra, EPixelType.UnsignedByte, EFrameBufferAttachment.ColorAttachment0);

        private static XRMaterial CreateWebMaterial()
        {
            XRTexture2D texture = CreateWebTexture(1u, 1u);
            texture.Mipmaps[0].Data = new DataSource(new byte[4]);

            return new XRMaterial(
                [texture],
                XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment));
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Url):
                    _pendingLoadUrl = true;
                    break;
                case nameof(TransparentBackground):
                    _pendingReinitialize = true;
                    break;
                case nameof(Backend):
                    (prev as IWebRendererBackend)?.Dispose();
                    _pendingReinitialize = true;
                    break;
            }
        }

        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            if (e.PropertyName == nameof(UIBoundableTransform.AxisAlignedRegion))
                UpdateBackingSize();
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            UpdateBackingSize();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _webTransientDisposed = false;
            RuntimeEngine.Time.Timer.RenderFrame += RenderFrame;
            UpdateBackingSize();
            _pendingReinitialize = true;
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            RuntimeEngine.Time.Timer.RenderFrame -= RenderFrame;
            lock (_webProducerGate)
            {
                if (_webProducerActive != 0)
                    _webTransientDisposePending = true;
                else
                    DisposeWebTransientResources();
            }
        }

        private void DisposeWebTransientResources()
        {
            if (_webTransientDisposed)
                return;
            _backend.Dispose();
            DestroyPbos();
            _webTransientDisposed = true;
        }

        private void ReinitializeBackend()
        {
            if (!IsActive)
                return;

            _backend.Initialize(_backingWidth, _backingHeight, _transparentBackground);
            if (!string.IsNullOrWhiteSpace(_url))
                _backend.LoadUrl(_url!);

            if (!_loggedBackendInit)
            {
                Debug.UI($"[UIWebView] Backend initialized: type={_backend.GetType().Name}, size={_backingWidth}x{_backingHeight}, transparent={_transparentBackground}, supportsFramebuffer={_backend.SupportsFramebuffer}, url='{_url}'");
                _loggedBackendInit = true;
            }

            if (_backend.SupportsFramebuffer)
                UpdateFramebufferTarget();
        }

        private bool UpdateFramebufferTarget()
        {
            _fbo.Material = Material;
            _fbo.Generate();

            IRuntimeRendererHost? renderer = AbstractRenderer.Current;
            if (renderer is null ||
                !renderer.TryGetBackendCapability<IUiFramebufferInteropBackendCapability>(out var capability) ||
                capability is null ||
                !capability.TryGetUiFramebufferInterop(_fbo, out UiFramebufferInteropInfo interopInfo))
            {
                _webProducerFailure = "UI.Web.ProducerInteropUnavailable: the renderer cannot bind the web framebuffer.";
                Debug.UIWarning(_webProducerFailure);
                return false;
            }

            _backend.SetTargetFramebuffer(interopInfo.BindingId);
            if (!_loggedTargetFramebuffer)
            {
                Debug.UI($"[UIWebView] Target framebuffer bound: id={interopInfo.BindingId}");
                _loggedTargetFramebuffer = true;
            }
            return true;
        }

        private void UpdateBackingSize()
        {
            BoundingRectangleF bounds = BoundableTransform.AxisAlignedRegion;
            uint w = (uint)bounds.Width.ClampMin(1.0f);
            uint h = (uint)bounds.Height.ClampMin(1.0f);

            if (w == _requestedBackingWidth && h == _requestedBackingHeight)
                return;

            _requestedBackingWidth = w;
            _requestedBackingHeight = h;
            _pendingResize = true;
        }

        private void RenderFrame()
        {
            lock (_webProducerGate)
            {
                if (Volatile.Read(ref _webProducerDestroyed) != 0)
                    return;
                _webProducerActive++;
                try { RenderFrameCore(); }
                finally
                {
                    _webProducerActive--;
                    if (_webProducerActive == 0 && _webTransientDisposePending)
                    {
                        _webTransientDisposePending = false;
                        DisposeWebTransientResources();
                    }
                    if (_webProducerActive == 0 && _webReleasePending)
                        ReleaseWebSurfacesUnderGate();
                }
            }
        }

        private void RenderFrameCore()
        {
            if (!IsActive || _rendering)
                return;

            _rendering = true;
            try
            {
                ProcessPendingBackendChanges();

                double delta = RuntimeEngine.Time.Timer.Update.Delta;
                _backend.Update(delta);
                if (Volatile.Read(ref _webProducerDestroyed) != 0 || !IsActive)
                    return;
                WebSurface? acceleratedSurface = null;
                if (_backend.SupportsFramebuffer)
                {
                    acceleratedSurface = AcquireWebSurface(_backingWidth, _backingHeight);
                    if (acceleratedSurface is null)
                    {
                        _webProducerFailure = "UI.Web.ProducerCapacity: all web textures still belong to published UI draws.";
                        return;
                    }
                    _fbo = acceleratedSurface.Fbo;
                    if (!UpdateFramebufferTarget())
                        return;
                }
                if (Volatile.Read(ref _webProducerDestroyed) != 0 || !IsActive)
                    return;
                _backend.Render();
                if (Volatile.Read(ref _webProducerDestroyed) != 0)
                    return;

                if (acceleratedSurface is not null)
                    PublishWebSurface(acceleratedSurface);
                else if (_backend.TryGetFrame(out WebFrame frame))
                {
                    if (Volatile.Read(ref _webProducerDestroyed) != 0 || !IsActive)
                        return;
                    if (frame.Width <= 0 || frame.Height <= 0 || frame.ByteLength == 0 || frame.PixelDataPtr == IntPtr.Zero)
                        return;
                    WebSurface? softwareSurface = AcquireWebSurface((uint)frame.Width, (uint)frame.Height);
                    if (softwareSurface is null)
                    {
                        _webProducerFailure = "UI.Web.ProducerCapacity: all web textures still belong to published UI draws.";
                        return;
                    }
                    ApplyFrame(frame, softwareSurface);
                    PublishWebSurface(softwareSurface);
                    if (!_loggedSoftwareFrame)
                    {
                        Debug.UI($"[UIWebView] First software frame: {frame.Width}x{frame.Height}, bytes={frame.ByteLength}");
                        _loggedSoftwareFrame = true;
                    }
                }
            }
            finally
            {
                _rendering = false;
            }
        }

        private void ProcessPendingBackendChanges()
        {
            bool hasResize = _pendingResize;
            bool hasReinitialize = _pendingReinitialize;
            bool hasLoadUrl = _pendingLoadUrl;

            if (!hasResize && !hasReinitialize && !hasLoadUrl)
                return;

            try
            {
                if (hasResize)
                {
                    _pendingResize = false;

                    uint w = _requestedBackingWidth;
                    uint h = _requestedBackingHeight;
                    if (w != _backingWidth || h != _backingHeight)
                    {
                        _backingWidth = w;
                        _backingHeight = h;

                        if (_backend.IsInitialized)
                            _backend.Resize(w, h);
                    }
                }

                if (hasReinitialize)
                {
                    _pendingReinitialize = false;
                    _pendingLoadUrl = false;
                    ReinitializeBackend();
                    return;
                }

                if (hasLoadUrl)
                {
                    _pendingLoadUrl = false;
                    if (!string.IsNullOrWhiteSpace(_url))
                        _backend.LoadUrl(_url!);
                }
            }
            catch (Exception ex)
            {
                SetField(ref _lastBackendFailure, ex.Message, nameof(LastBackendFailure));
                Debug.LogWarning($"UIWebViewComponent backend '{_backend.GetType().Name}' failed: {ex.Message}");
                _pendingReinitialize = false;
                _pendingLoadUrl = false;
                _pendingResize = false;

                if (AllowSoftwareFallback && _backend.SupportsFramebuffer)
                {
                    Backend = WebRendererBackendRegistry.CreateSoftware();
                    Debug.UIWarning("UIWebViewComponent is switching to its explicitly permitted software web backend.");
                }
            }
        }

        private void ApplyFrame(in WebFrame frame, WebSurface surface)
        {
            if (frame.Width <= 0 || frame.Height <= 0 || frame.ByteLength == 0 || frame.PixelDataPtr == IntPtr.Zero)
                return;

            // Reuse or create cached mipmap only when dimensions/format change
            if (surface.Mipmap is null ||
                surface.Mipmap.Width != (uint)frame.Width ||
                surface.Mipmap.Height != (uint)frame.Height ||
                surface.Mipmap.PixelFormat != frame.PixelFormat ||
                surface.Mipmap.PixelType != frame.PixelType)
            {
                surface.Mipmap = new Mipmap2D(
                    (uint)frame.Width,
                    (uint)frame.Height,
                    EPixelInternalFormat.Rgba8,
                    frame.PixelFormat,
                    frame.PixelType,
                    false);
            }

            if (UsePboUpload)
            {
                UploadViaPbo(surface, surface.Mipmap, frame);
            }
            else
            {
                // Direct upload path - still requires copy but avoids extra allocations
                UploadDirect(surface, surface.Mipmap, frame);
            }
        }

        private static unsafe void UploadDirect(WebSurface surface, Mipmap2D mip, in WebFrame frame)
        {
            surface.MipmapArray[0] = mip;
            surface.Texture.Mipmaps = surface.MipmapArray;
            surface.Texture.AutoGenerateMipmaps = false;
            if (surface.CpuPixels is null || surface.CpuPixels.Length < (uint)frame.ByteLength)
            {
                surface.CpuPixels?.Dispose();
                surface.CpuPixels = new DataSource((uint)frame.ByteLength);
            }
            surface.CpuPixels.Length = (uint)frame.ByteLength;
            new ReadOnlySpan<byte>((void*)frame.PixelDataPtr, frame.ByteLength)
                .CopyTo(new Span<byte>(surface.CpuPixels.Address.Pointer, frame.ByteLength));
            mip.Data = surface.CpuPixels;
            surface.Texture.Generate();
            surface.Texture.PushData();
        }

        private void UploadViaPbo(WebSurface surface, Mipmap2D mip, in WebFrame frame)
        {
            int byteLength = frame.ByteLength;
            int index = _currentPboIndex;
            XRDataBuffer<byte>? pbo = _pboBuffers[index];
            _currentPboIndex = (_currentPboIndex + 1) & 1;

            if (pbo is null || pbo.Length != (uint)byteLength)
            {
                pbo?.Destroy();
                _pboBuffers[index] = pbo = new XRDataBuffer<byte>(
                    string.Empty,
                    EBufferTarget.PixelUnpackBuffer,
                    (uint)byteLength)
                {
                    Resizable = false,
                    Usage = EBufferUsage.StreamDraw,
                    RangeFlags = EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
                    StorageFlags = EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Coherent | EBufferMapStorageFlags.Persistent,
                };
            }

            pbo.Generate();
            pbo.Bind();
            {
                mip.StreamingPBO = pbo;
                // Reuse mipmap array to avoid allocation
                surface.MipmapArray[0] = mip;
                surface.Texture.Mipmaps = surface.MipmapArray;
                surface.Texture.AutoGenerateMipmaps = false;
                surface.Texture.Bind();

                CopyPixelsToPbo(frame.PixelDataPtr, byteLength, pbo);
                surface.Texture.PushData();
            }
            pbo.Unbind();
        }

        private static unsafe void CopyPixelsToPbo(IntPtr srcPtr, int byteLength, XRDataBuffer<byte> pbo)
        {
            pbo.MapBufferData();
            pbo.TryWriteMapped(bytes =>
            {
                new ReadOnlySpan<byte>((void*)srcPtr, byteLength).CopyTo(bytes);
                return true;
            });
            pbo.UnmapBufferData();
        }

        private void DestroyPbos()
        {
            for (int i = 0; i < _pboBuffers.Length; i++)
            {
                _pboBuffers[i]?.Destroy();
                _pboBuffers[i] = null;
            }
        }

        protected override bool TryCapturePublicationPayload(in UILayoutNodeOutput output,
            UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
        {
            payload = null;
            if (Volatile.Read(ref _webProducerDestroyed) != 0)
            {
                failure = "UI.Web.ProducerStopped: the web producer is closing.";
                return false;
            }
            WebSurface? surface = Volatile.Read(ref _publishedWebSurface);
            if (surface is null || surface.Generation == 0)
            {
                failure = _webProducerFailure ?? "UI.Web.ProducerPending: no complete web surface is ready.";
                return false;
            }
            surface.Retain();
            try
            {
                if (!ReferenceEquals(surface, Volatile.Read(ref _publishedWebSurface)))
                {
                    failure = "UI.Web.ProducerChanging: the web surface changed during owner capture.";
                    return false;
                }
                builder.ProducerLease = surface;
                builder.ProducerTextureSlot = 0;
                return builder.TryCapture(RenderCommand2D, false, out payload, out failure);
            }
            finally
            {
                surface.Release();
            }
        }

        protected override void OnDestroying()
        {
            Volatile.Write(ref _webProducerDestroyed, 1);
            RuntimeEngine.Time.Timer.RenderFrame -= RenderFrame;
            Volatile.Write(ref _publishedWebSurface, null);
            try { base.OnDestroying(); }
            finally
            {
                lock (_webProducerGate)
                {
                    if (_webProducerActive != 0)
                        _webReleasePending = true;
                    else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                        ReleaseWebSurfacesUnderGate();
                    else
                        RuntimeEngine.EnqueueRenderThreadTask(ReleaseWebSurfaces, "UI.Web.ReleaseSurfaces",
                            RenderThreadJobKind.RenderPipelineResource);
                }
            }
        }

        private void ReleaseWebSurfaces()
        {
            lock (_webProducerGate)
                ReleaseWebSurfacesUnderGate();
        }

        private void ReleaseWebSurfacesUnderGate()
        {
            if (_webSurfacesReleased)
                return;
            if (_webProducerActive != 0)
            {
                _webReleasePending = true;
                return;
            }
            _webSurfacesReleased = true;
            _webReleasePending = false;
            Volatile.Write(ref _publishedWebSurface, null);
            DisposeWebTransientResources();
            foreach (WebSurface surface in _webSurfaces)
                surface.Release();
            _webSurfaces.Clear();
        }
    }
}
