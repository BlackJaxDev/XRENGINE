using XREngine.Extensions;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI.State;
using System.Threading;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Houses a viewport that renders a scene from a designated camera.
    /// </summary>
    public class UIViewportComponent : UIMaterialComponent
    {
        public event DelSetUniforms? SettingUniforms;

        private XRMaterialFrameBuffer _fbo;
        private XRTexture2D _depthStencilTexture;
        private const int MaxProducedSurfaces = 4;
        private readonly List<ViewportSurface> _surfaces = [];
        private ViewportSurface? _publishedSurface;
        private ulong _nextSurfaceGeneration;
        private string? _lastProducerFailure;
        private readonly Action _invalidateViewportVisual;
        private int _viewportVisualInvalidationQueued;
        private int _viewportProducerDestroyed;
        private readonly object _viewportProducerGate = new();
        private int _viewportProducerActive;
        private bool _viewportReleasePending;
        private bool _viewportSurfacesReleased;

        private sealed class ViewportSurface(XRMaterialFrameBuffer fbo, XRTexture2D color,
            XRTexture2D depth) : IUIDrawProducerLease
        {
            private int _references = 1; // component's pool ownership
            private long _generation;
            public XRMaterialFrameBuffer Fbo { get; } = fbo;
            public XRTexture2D Texture { get; } = color;
            public XRTexture2D Depth { get; } = depth;
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
                if (remaining < 0) throw new InvalidOperationException("Viewport surface was released twice.");
                if (remaining != 0) return;
                if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                    DestroyResources();
                else
                    RuntimeEngine.EnqueueRenderThreadTask(DestroyResources, "UI.Viewport.DestroySurface",
                        RenderThreadJobKind.RenderPipelineResource);
            }

            private void DestroyResources()
            {
                Fbo.Destroy();
                Depth.Destroy();
                foreach (Mipmap2D mip in Texture.Mipmaps)
                    mip.Data?.Dispose();
                Texture.Destroy();
            }
        }
        private uint _requestedWidth = 1u;
        private uint _requestedHeight = 1u;
        private bool _pendingResize;

        //These bools are to prevent infinite pre-rendering recursion
        private bool _collecting = false;
        private bool _swapping = false;
        private bool _rendering = false;
        private XRWindow? _renderWindow;
        private ulong _collectVisibleAttempts;
        private ulong _collectVisibleCompleted;
        private ulong _collectVisibleSkipped;
        private ulong _swapBuffersAttempts;
        private ulong _swapBuffersCompleted;
        private ulong _swapBuffersSkipped;
        private ulong _renderAttempts;
        private ulong _renderCompleted;
        private ulong _renderSkipped;
        private ulong _renderTargetIncompleteSkipped;
        private ulong _lastRenderFrameId;

        public UIViewportComponent() : base(GetViewportMaterial())
        {
            _invalidateViewportVisual = InvalidateViewportVisual;
            DisableBatching = true;
            SetBlendModeAllDrawBuffers(BlendMode.Disabled());
            Viewport.AutomaticallyCollectVisible = false;
            Viewport.AutomaticallySwapBuffers = false;
            Viewport.UseDirectFboTargetCommandsWhenRenderingToFbo = false;
            Viewport.AllowUIRender = false;
            Viewport.AllowAutomaticInternalResolution = false;
            _depthStencilTexture = CreateDepthStencilTexture(1u, 1u);
            _fbo = new XRMaterialFrameBuffer(Material, deriveRenderTargetsFromMaterial: false);
            ApplyUniqueRenderTargetNames();
            ApplyFramebufferTargets();
            ViewportSurface placeholder = new(_fbo, (XRTexture2D)RenderedTexture!, _depthStencilTexture);
            placeholder.Publish(++_nextSurfaceGeneration);
            _surfaces.Add(placeholder);
            Volatile.Write(ref _publishedSurface, placeholder);

            if (RenderCommand3D.Mesh is not null)
                RenderCommand3D.Mesh.SettingUniforms += SetUniforms;
        }

        private void ApplyUniqueRenderTargetNames()
        {
            string suffix = ID.ToString("N");
            if (RenderedTexture is XRTexture colorTexture)
                colorTexture.Name = $"UIViewportColor_{suffix}";

            _depthStencilTexture.Name = $"UIViewportDepthStencil_{suffix}";
            _fbo.Name = $"UIViewportFBO_{suffix}";
        }

        private static XRTexture2D CreateColorTexture(uint width, uint height)
        {
            var texture = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            texture.Name = "UIViewportColor";
            return texture;
        }

        private static XRTexture2D CreateDepthStencilTexture(uint width, uint height)
        {
            var texture = XRTexture2D.CreateFrameBufferTexture(
                width,
                height,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                EFrameBufferAttachment.DepthStencilAttachment);
            texture.MinFilter = ETexMinFilter.Nearest;
            texture.MagFilter = ETexMagFilter.Nearest;
            texture.AutoGenerateMipmaps = false;
            texture.Name = "UIViewportDepthStencil";
            texture.SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8;
            return texture;
        }

        private static XRMaterial GetViewportMaterial()
        {
            XRTexture2D color = CreateColorTexture(1u, 1u);
            color.Mipmaps[0].Data = new DataSource(new byte[8]);
            return new XRMaterial([color],
                XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedOpaqueForward.fs"), EShaderType.Fragment));
        }

        private void InvalidateViewportVisual()
        {
            Interlocked.Exchange(ref _viewportVisualInvalidationQueued, 0);
            if (Volatile.Read(ref _viewportProducerDestroyed) != 0)
                return;
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }

        private void ApplyFramebufferTargets()
        {
            if (RenderedTexture is not IFrameBufferAttachement colorAttachment)
                return;

            _fbo.SetRenderTargets(
                (colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (_depthStencilTexture, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
        }

        private void SetUniforms(XRRenderProgram vertexProgram, XRRenderProgram materialProgram)
            => SettingUniforms?.Invoke(materialProgram);

        [YamlIgnore]
        public XRViewport Viewport { get; private set; } = new XRViewport(null, 1, 1);

        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(UIBoundableTransform.AxisAlignedRegion):
                    UpdateSize();
                    break;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            RuntimeEngine.Time.Timer.SwapBuffers += SwapBuffers;
            RuntimeEngine.Time.Timer.CollectVisible += CollectVisible;
            EnsureRenderWindowHooked();
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            RuntimeEngine.Time.Timer.SwapBuffers -= SwapBuffers;
            RuntimeEngine.Time.Timer.CollectVisible -= CollectVisible;
            UnhookRenderWindow();
        }

        private void EnsureRenderWindowHooked()
        {
            XRWindow? window = Viewport.Window;
            window ??= RuntimeEngine.Windows.FirstOrDefault();

            if (ReferenceEquals(_renderWindow, window))
                return;

            UnhookRenderWindow();
            _renderWindow = window;
            Viewport.Window = _renderWindow;
            if (_renderWindow is not null)
                _renderWindow.RenderViewportsCallback += Render;
        }

        private void UnhookRenderWindow()
        {
            if (_renderWindow is not null)
                _renderWindow.RenderViewportsCallback -= Render;

            if (ReferenceEquals(Viewport.Window, _renderWindow))
                Viewport.Window = null;

            _renderWindow = null;
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            UpdateSize();
        }

        private void UpdateSize()
        {
            var tfm = BoundableTransform;
            uint w = (uint)tfm.AxisAlignedRegion.Width;
            uint h = (uint)tfm.AxisAlignedRegion.Height;
            w = w.ClampMin(1u);
            h = h.ClampMin(1u);

            if (!_pendingResize
                && _requestedWidth == w
                && _requestedHeight == h
                && (uint)Viewport.Width == w
                && (uint)Viewport.Height == h
                && _fbo.Width == w
                && _fbo.Height == h)
            {
                return;
            }

            _requestedWidth = w;
            _requestedHeight = h;
            _pendingResize = true;
        }

        // Apply layout-driven resizes at a frame boundary so the viewport, pipeline,
        // and output FBO are updated together instead of being invalidated mid-frame.
        private void ApplyPendingSize()
        {
            if (!_pendingResize)
                return;

            _pendingResize = false;

            if ((uint)Viewport.Width != _requestedWidth || (uint)Viewport.Height != _requestedHeight ||
                (uint)Viewport.InternalWidth != _requestedWidth || (uint)Viewport.InternalHeight != _requestedHeight)
                Viewport.Resize(_requestedWidth, _requestedHeight,
                    internalResolutionWidth: checked((int)_requestedWidth),
                    internalResolutionHeight: checked((int)_requestedHeight));
        }

        private ViewportSurface? AcquireProducedSurface()
        {
            ViewportSurface? published = Volatile.Read(ref _publishedSurface);
            foreach (ViewportSurface surface in _surfaces)
            {
                if (ReferenceEquals(surface, published) || !surface.IsPoolOnly)
                    continue;
                if (surface.Fbo.Width != _requestedWidth || surface.Fbo.Height != _requestedHeight)
                {
                    // The unleased placeholder becomes a GPU target. Do not resample its CPU pixels.
                    foreach (Mipmap2D mip in surface.Texture.Mipmaps)
                        mip.Data = null;
                    surface.Fbo.Resize(_requestedWidth, _requestedHeight);
                }
                return surface;
            }
            if (_surfaces.Count >= MaxProducedSurfaces)
                return null;
            XRTexture2D color = CreateColorTexture(_requestedWidth, _requestedHeight);
            XRTexture2D depth = CreateDepthStencilTexture(_requestedWidth, _requestedHeight);
            XRMaterialFrameBuffer fbo = new(Material, deriveRenderTargetsFromMaterial: false);
            fbo.SetRenderTargets(
                (color, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (depth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1));
            string suffix = $"{ID:N}_{_surfaces.Count}";
            color.Name = $"UIViewportColor_{suffix}";
            depth.Name = $"UIViewportDepthStencil_{suffix}";
            fbo.Name = $"UIViewportFBO_{suffix}";
            ViewportSurface created = new(fbo, color, depth);
            _surfaces.Add(created);
            return created;
        }

        public void CollectVisible()
        {
            using var sample = RuntimeEngine.Profiler.Start("UIViewportComponent.CollectVisible");

            _collectVisibleAttempts++;
            if (!IsActive || _collecting)
            {
                _collectVisibleSkipped++;
                return;
            }

            EnsureRenderWindowHooked();
            _collecting = true;
            try
            {
                bool wasSceneCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
                RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
                try
                {
                    Viewport.CollectVisible(false, allowScreenSpaceUICollectVisible: false);
                }
                finally
                {
                    RuntimeEngine.Rendering.State.IsSceneCapturePass = wasSceneCapturePass;
                }
                _collectVisibleCompleted++;
            }
            finally
            {
                _collecting = false;
            }
        }
        public void SwapBuffers()
        {
            using var sample = RuntimeEngine.Profiler.Start("UIViewportComponent.SwapBuffers");

            _swapBuffersAttempts++;
            if (!IsActive || _swapping)
            {
                _swapBuffersSkipped++;
                return;
            }

            EnsureRenderWindowHooked();
            _swapping = true;
            try
            {
                ApplyPendingSize();
                bool wasSceneCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
                RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
                try
                {
                    Viewport.SwapBuffers(allowScreenSpaceUISwap: false);
                }
                finally
                {
                    RuntimeEngine.Rendering.State.IsSceneCapturePass = wasSceneCapturePass;
                }
                _swapBuffersCompleted++;
            }
            finally
            {
                _swapping = false;
            }
        }
        public void Render()
        {
            lock (_viewportProducerGate)
            {
                if (Volatile.Read(ref _viewportProducerDestroyed) != 0)
                    return;
                _viewportProducerActive++;
                try { RenderCore(); }
                finally
                {
                    _viewportProducerActive--;
                    if (_viewportProducerActive == 0 && _viewportReleasePending)
                        ReleaseViewportSurfacesUnderGate();
                }
            }
        }

        private void RenderCore()
        {
            using var sample = RuntimeEngine.Profiler.Start("UIViewportComponent.Render");

            _renderAttempts++;
            if (!IsActive || _rendering)
            {
                _renderSkipped++;
                return;
            }

            _rendering = true;
            try
            {
                ApplyPendingSize();
                ViewportSurface? surface = AcquireProducedSurface();
                if (surface is null)
                {
                    _lastProducerFailure = "UI.Viewport.ProducerCapacity: all viewport textures still belong to published UI draws.";
                    _renderTargetIncompleteSkipped++;
                    return;
                }
                _fbo = surface.Fbo;
                _depthStencilTexture = surface.Depth;
                if (!_fbo.IsLastCheckComplete)
                {
                    _lastProducerFailure = "UI.Viewport.RenderTargetIncomplete: the selected framebuffer is not ready.";
                    _renderTargetIncompleteSkipped++;
                    return;
                }

                bool wasSceneCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
                RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
                try
                {
                    Viewport.Render(_fbo, null, null, false);
                    if (Volatile.Read(ref _viewportProducerDestroyed) != 0)
                        return;
                    AbstractRenderer.Current?.PublishFrameBufferAttachmentsForSampling(_fbo);
                    AbstractRenderer.Current?.MemoryBarrier(
                        EMemoryBarrierMask.Framebuffer |
                        EMemoryBarrierMask.TextureFetch |
                        EMemoryBarrierMask.TextureUpdate);
                }
                finally
                {
                    RuntimeEngine.Rendering.State.IsSceneCapturePass = wasSceneCapturePass;
                }
                if (Volatile.Read(ref _viewportProducerDestroyed) != 0)
                    return;
                _lastRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                surface.Publish(++_nextSurfaceGeneration);
                Volatile.Write(ref _publishedSurface, surface);
                _lastProducerFailure = null;
                if (Interlocked.Exchange(ref _viewportVisualInvalidationQueued, 1) == 0)
                    RuntimeEngine.EnqueueAppThreadTask(_invalidateViewportVisual, "UI.Viewport.FramePublished");
                _renderCompleted++;
            }
            finally
            {
                _rendering = false;
            }
        }

        [YamlIgnore]
        /// <summary>Gets the latest viewport texture. The caller must not keep it across frames.</summary>
        public XRTexture? RenderedTexture
        {
            get => Volatile.Read(ref _publishedSurface)?.Texture ??
                (Material?.Textures?.Count > 0 ? Material.Textures[0] : null);
        }

        [YamlIgnore]
        public XRFrameBuffer RenderTargetFBO => _fbo;

        [YamlIgnore]
        public string? LastProducerFailure => _lastProducerFailure;

        protected override bool TryCapturePublicationPayload(in UILayoutNodeOutput output,
            UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
        {
            payload = null;
            if (Volatile.Read(ref _viewportProducerDestroyed) != 0)
            {
                failure = "UI.Viewport.ProducerStopped: the viewport producer is closing.";
                return false;
            }
            // Activation can replace the mesh. With no public callback, the built-in bridge is optional.
            if (SettingUniforms is not null || Mesh is not { } sourceMesh ||
                (sourceMesh.HasSettingUniformsHandlers && !sourceMesh.HasOnlySettingUniformsHandler(SetUniforms)) ||
                sourceMesh.HasRenderDataPreparation)
            {
                failure = "UI.Viewport.CallbackAdapterRequired: authored viewport callbacks need a value capture adapter.";
                return false;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ViewportSurface? surface = Volatile.Read(ref _publishedSurface);
                if (surface is null || surface.Generation == 0)
                {
                    failure = _lastProducerFailure ?? "UI.Viewport.ProducerPending: no complete viewport surface is ready.";
                    return false;
                }
                surface.Retain();
                try
                {
                    if (!ReferenceEquals(surface, Volatile.Read(ref _publishedSurface)))
                        continue;
                    builder.ProducerLease = surface;
                    builder.ProducerTextureSlot = 0;
                    return builder.TryCapture(RenderCommand2D, true, out payload, out failure);
                }
                finally
                {
                    surface.Release();
                }
            }
            failure = "UI.Viewport.ProducerChanging: the viewport surface changed during owner capture.";
            return false;
        }

        protected override void OnDestroying()
        {
            Volatile.Write(ref _viewportProducerDestroyed, 1);
            RuntimeEngine.Time.Timer.SwapBuffers -= SwapBuffers;
            RuntimeEngine.Time.Timer.CollectVisible -= CollectVisible;
            if (RenderCommand3D.Mesh is { } renderer)
                renderer.SettingUniforms -= SetUniforms;
            UnhookRenderWindow();
            Volatile.Write(ref _publishedSurface, null);
            try { base.OnDestroying(); }
            finally
            {
                lock (_viewportProducerGate)
                {
                    if (_viewportProducerActive != 0)
                        _viewportReleasePending = true;
                    else if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                        ReleaseViewportSurfacesUnderGate();
                    else
                        RuntimeEngine.EnqueueRenderThreadTask(ReleaseViewportSurfaces, "UI.Viewport.ReleaseSurfaces",
                            RenderThreadJobKind.RenderPipelineResource);
                }
            }
        }

        private void ReleaseViewportSurfaces()
        {
            lock (_viewportProducerGate)
                ReleaseViewportSurfacesUnderGate();
        }

        private void ReleaseViewportSurfacesUnderGate()
        {
            if (_viewportSurfacesReleased)
                return;
            if (_viewportProducerActive != 0)
            {
                _viewportReleasePending = true;
                return;
            }
            _viewportSurfacesReleased = true;
            _viewportReleasePending = false;
            Volatile.Write(ref _publishedSurface, null);
            foreach (ViewportSurface surface in _surfaces)
                surface.Release();
            _surfaces.Clear();
        }

        [YamlIgnore]
        public ulong CollectVisibleAttempts => _collectVisibleAttempts;

        [YamlIgnore]
        public ulong CollectVisibleCompleted => _collectVisibleCompleted;

        [YamlIgnore]
        public ulong CollectVisibleSkipped => _collectVisibleSkipped;

        [YamlIgnore]
        public ulong SwapBuffersAttempts => _swapBuffersAttempts;

        [YamlIgnore]
        public ulong SwapBuffersCompleted => _swapBuffersCompleted;

        [YamlIgnore]
        public ulong SwapBuffersSkipped => _swapBuffersSkipped;

        [YamlIgnore]
        public ulong RenderAttempts => _renderAttempts;

        [YamlIgnore]
        public ulong RenderCompleted => _renderCompleted;

        [YamlIgnore]
        public ulong RenderSkipped => _renderSkipped;

        [YamlIgnore]
        public ulong RenderTargetIncompleteSkipped => _renderTargetIncompleteSkipped;

        [YamlIgnore]
        public ulong LastRenderFrameId => _lastRenderFrameId;
    }
}
