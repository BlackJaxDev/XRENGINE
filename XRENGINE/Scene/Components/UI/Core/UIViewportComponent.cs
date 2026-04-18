using XREngine.Extensions;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
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
        private uint _requestedWidth = 1u;
        private uint _requestedHeight = 1u;
        private bool _pendingResize;

        //These bools are to prevent infinite pre-rendering recursion
        private bool _collecting = false;
        private bool _swapping = false;
        private bool _rendering = false;

        public UIViewportComponent() : base(GetViewportMaterial())
        {
            _fbo = new XRMaterialFrameBuffer(Material);

            if (RenderCommand3D.Mesh is not null)
                RenderCommand3D.Mesh.SettingUniforms += SetUniforms;
        }

        private static XRMaterial GetViewportMaterial()
            => new([XRTexture2D.CreateFrameBufferTexture(1u, 1u,
                    EPixelInternalFormat.Rgba16f,
                    EPixelFormat.Rgba,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0)],
                XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment));

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

            Engine.Time.Timer.SwapBuffers += SwapBuffers;
            Engine.Time.Timer.CollectVisible += CollectVisible;
            Engine.Time.Timer.RenderFrame += Render;
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
            Engine.Time.Timer.CollectVisible -= CollectVisible;
            Engine.Time.Timer.RenderFrame -= Render;
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
            using var sample = Engine.Profiler.Start("UIViewportComponent.CollectVisible");

            if (!IsActive || _collecting)
                return;

            _collecting = true;
            try
            {
                Viewport.CollectVisible(false);
            }
            finally
            {
                _collecting = false;
            }
        }
        public void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("UIViewportComponent.SwapBuffers");

            if (!IsActive || _swapping)
                return;

            _swapping = true;
            try
            {
                ApplyPendingSize();
                Viewport.SwapBuffers();
            }
            finally
            {
                _swapping = false;
            }
        }
        public void Render()
        {
            using var sample = Engine.Profiler.Start("UIViewportComponent.Render");

            if (!IsActive || _rendering)
                return;

            _rendering = true;
            try
            {
                ApplyPendingSize();
                Viewport.Render(_fbo, null, null, false);
            }
            finally
            {
                _rendering = false;
            }
        }
    }
}
