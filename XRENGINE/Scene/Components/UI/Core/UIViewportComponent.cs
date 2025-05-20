using Extensions;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Houses a viewport that renders a scene from a designated camera.
    /// </summary>
    public class UIViewportComponent : UIMaterialComponent
    {
        public event DelSetUniforms? SettingUniforms;

        private readonly XRMaterialFrameBuffer _fbo;

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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            Engine.Time.Timer.SwapBuffers += SwapBuffers;
            Engine.Time.Timer.CollectVisible += CollectVisible;
            Engine.Time.Timer.RenderFrame += Render;
        }
        protected internal override void OnComponentDeactivated()
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
            Viewport.Resize(w, h);
            _fbo.Resize(w, h);
        }

        public void CollectVisible()
        {
            if (!IsActive || _collecting)
                return;

            _collecting = true;
            Viewport.CollectVisible(false);
            _collecting = false;
        }
        public void SwapBuffers()
        {
            if (!IsActive || _swapping)
                return;

            _swapping = true;
            Viewport.SwapBuffers();
            _swapping = false;
        }
        public void Render()
        {
            if (!IsActive || _rendering)
                return;

            _rendering = true;
            Viewport.Render(_fbo, null, null, false);
            _rendering = false;
        }
    }
}
