using XREngine.Extensions;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Houses a viewport that renders a scene from a designated camera.
    /// </summary>
    public class UIViewportComponent : UIMaterialComponent
    {
        public event DelSetUniforms? SettingUniforms;

        private readonly XRMaterialFrameBuffer _fbo;
        private readonly XRTexture2D _depthStencilTexture;
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
            => new([CreateColorTexture(1u, 1u)],
                XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedOpaqueForward.fs"), EShaderType.Fragment));

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

            if (_fbo.Width != _requestedWidth || _fbo.Height != _requestedHeight)
                _fbo.Resize(_requestedWidth, _requestedHeight);

            if ((uint)Viewport.Width != _requestedWidth || (uint)Viewport.Height != _requestedHeight)
                Viewport.Resize(_requestedWidth, _requestedHeight);
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
                if (!_fbo.IsLastCheckComplete)
                {
                    _renderTargetIncompleteSkipped++;
                    return;
                }

                bool wasSceneCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
                RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
                try
                {
                    Viewport.Render(_fbo, null, null, false);
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
                _lastRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                _renderCompleted++;
            }
            finally
            {
                _rendering = false;
            }
        }

        [YamlIgnore]
        public XRTexture? RenderedTexture
        {
            get => Material?.Textures?.Count > 0 ? Material.Textures[0] : null;
        }

        [YamlIgnore]
        public XRFrameBuffer RenderTargetFBO => _fbo;

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
