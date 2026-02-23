using Extensions;
using System;
using System.IO;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

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
        public void Dispose() { }
    }

    /// <summary>
    /// Displays a web renderer surface in UI by rendering into a framebuffer-backed texture.
    /// </summary>
    public sealed class UIWebViewComponent : UIMaterialComponent
    {
        private readonly XRMaterialFrameBuffer _fbo;
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
        private readonly XRDataBuffer?[] _pboBuffers = [null, null];
        private GLFrameBuffer? _cachedGlFbo;
        
        // Cached mipmap to avoid allocation every frame
        private Mipmap2D? _cachedMipmap;
        private readonly Mipmap2D[] _mipmapArray = new Mipmap2D[1];
        
        // Cached DataSource for direct upload - reused by updating Address/Length
        private DataSource? _cachedDataSource;

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
            _fbo = new XRMaterialFrameBuffer(Material);
        }

        private static XRMaterial CreateWebMaterial()
        {
            XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
                1u,
                1u,
                EPixelInternalFormat.Rgba8,
                EPixelFormat.Bgra,
                EPixelType.UnsignedByte,
                EFrameBufferAttachment.ColorAttachment0);

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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Engine.Time.Timer.RenderFrame += RenderFrame;
            UpdateBackingSize();
            _pendingReinitialize = true;
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.Time.Timer.RenderFrame -= RenderFrame;
            _backend.Dispose();
            DestroyPbos();
        }

        private void ReinitializeBackend()
        {
            if (!IsActive)
                return;

            _backend.Initialize(_backingWidth, _backingHeight, _transparentBackground);
            if (!string.IsNullOrWhiteSpace(_url))
                _backend.LoadUrl(_url!);

            if (_backend.SupportsFramebuffer)
                UpdateFramebufferTarget();
        }

        private void UpdateFramebufferTarget()
        {
            _fbo.Material = Material;
            _fbo.Generate();

            // Cache the GLFrameBuffer to avoid LINQ allocation every call
            _cachedGlFbo = null;
            foreach (var wrapper in _fbo.APIWrappers)
            {
                if (wrapper is GLFrameBuffer glFbo)
                {
                    _cachedGlFbo = glFbo;
                    _backend.SetTargetFramebuffer(glFbo.BindingId);
                    break;
                }
            }
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

        private XRTexture2D EnsureWebTextureSize(uint width, uint height)
        {
            var textures = Material?.Textures;
            XRTexture2D? texture = textures is not null && textures.Count > 0 ? textures[0] as XRTexture2D : null;

            if (texture is null)
            {
                texture = XRTexture2D.CreateFrameBufferTexture(
                    width,
                    height,
                    EPixelInternalFormat.Rgba8,
                    EPixelFormat.Bgra,
                    EPixelType.UnsignedByte,
                    EFrameBufferAttachment.ColorAttachment0);
                Material!.Textures = [texture];
                _fbo.Material = Material;
            }
            else if (texture.Width != width || texture.Height != height)
            {
                texture.Resize(width, height);
            }

            return texture;
        }

        private void RenderFrame()
        {
            if (!IsActive || _rendering)
                return;

            _rendering = true;
            try
            {
                ProcessPendingBackendChanges();

                double delta = Engine.Time.Timer.Update.Delta;
                _backend.Update(delta);
                _backend.Render();

                if (!_backend.SupportsFramebuffer && _backend.TryGetFrame(out WebFrame frame))
                    ApplyFrame(frame);
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

                        EnsureWebTextureSize(w, h);
                        _fbo.Resize(w, h);

                        if (_backend.SupportsFramebuffer)
                            UpdateFramebufferTarget();

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
                Debug.LogWarning($"UIWebViewComponent backend init failed ({ex.Message}). Falling back to software backend.");
                _pendingReinitialize = false;
                _pendingLoadUrl = false;
                _pendingResize = false;

                // Auto-fallback: if the current backend is not already the software renderer, swap and retry once.
                if (_backend is not UltralightWebRendererBackend)
                {
                    _backend.Dispose();
                    _backend = new UltralightWebRendererBackend();
                    _pendingReinitialize = true;
                }
            }
        }

        private void ApplyFrame(in WebFrame frame)
        {
            if (frame.Width <= 0 || frame.Height <= 0 || frame.ByteLength == 0 || frame.PixelDataPtr == IntPtr.Zero)
                return;

            XRTexture2D texture = EnsureWebTextureSize((uint)frame.Width, (uint)frame.Height);

            // Reuse or create cached mipmap only when dimensions/format change
            if (_cachedMipmap is null ||
                _cachedMipmap.Width != (uint)frame.Width ||
                _cachedMipmap.Height != (uint)frame.Height ||
                _cachedMipmap.PixelFormat != frame.PixelFormat ||
                _cachedMipmap.PixelType != frame.PixelType)
            {
                _cachedMipmap = new Mipmap2D(
                    (uint)frame.Width,
                    (uint)frame.Height,
                    EPixelInternalFormat.Rgba8,
                    frame.PixelFormat,
                    frame.PixelType,
                    false);
            }

            if (UsePboUpload)
            {
                UploadViaPbo(texture, _cachedMipmap, frame);
            }
            else
            {
                // Direct upload path - still requires copy but avoids extra allocations
                UploadDirect(texture, _cachedMipmap, frame);
            }
        }

        private void UploadDirect(XRTexture2D texture, Mipmap2D mip, in WebFrame frame)
        {
            // Use mipmap array to avoid allocation
            _mipmapArray[0] = mip;
            texture.Mipmaps = _mipmapArray;
            texture.AutoGenerateMipmaps = false;
            
            // Reuse cached DataSource - just update Address and Length to avoid allocation
            _cachedDataSource ??= new DataSource(frame.PixelDataPtr, (uint)frame.ByteLength, copyInternal: false);
            _cachedDataSource.Address = frame.PixelDataPtr;
            _cachedDataSource.Length = (uint)frame.ByteLength;
            mip.Data = _cachedDataSource;
            
            texture.Generate();
            texture.PushData();
        }

        private void UploadViaPbo(XRTexture2D texture, Mipmap2D mip, in WebFrame frame)
        {
            int byteLength = frame.ByteLength;
            XRDataBuffer? pbo = _pboBuffers[_currentPboIndex];
            _currentPboIndex = (_currentPboIndex + 1) & 1;

            if (pbo is null || pbo.Length != (uint)byteLength)
            {
                pbo?.Destroy();
                _pboBuffers[_currentPboIndex] = pbo = new XRDataBuffer(
                    string.Empty,
                    EBufferTarget.PixelUnpackBuffer,
                    (uint)byteLength,
                    EComponentType.Byte,
                    1,
                    false,
                    false)
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
                _mipmapArray[0] = mip;
                texture.Mipmaps = _mipmapArray;
                texture.AutoGenerateMipmaps = false;
                texture.Bind();

                CopyPixelsToPbo(frame.PixelDataPtr, byteLength, pbo);
                texture.PushData();
            }
            pbo.Unbind();
        }

        private static unsafe void CopyPixelsToPbo(IntPtr srcPtr, int byteLength, XRDataBuffer pbo)
        {
            pbo.MapBufferData();
            var activelyMapping = pbo.ActivelyMapping;
            for (int i = 0; i < activelyMapping.Count; i++)
            {
                var apiBuffer = activelyMapping[i];
                if (apiBuffer is null)
                    continue;

                var dstPtr = apiBuffer.GetMappedAddress();
                if (dstPtr is null || !dstPtr.Value.IsValid)
                    continue;

                // Direct memory copy - no managed array allocation
                Buffer.MemoryCopy((void*)srcPtr, (void*)dstPtr.Value, byteLength, byteLength);
            }
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
    }
}