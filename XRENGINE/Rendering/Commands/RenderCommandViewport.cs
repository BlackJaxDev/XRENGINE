using System.Numerics;

namespace XREngine.Rendering.Commands
{
    public class RenderCommandViewport : RenderCommandMesh2D
    {
        public XRViewport? Viewport { get; set; }
        public XRMaterialFrameBuffer? Framebuffer { get; set; }

        //public RenderCommandViewport() : base() { }
        public RenderCommandViewport(int renderPass) : base(renderPass) { }
        public RenderCommandViewport(
            int renderPass,
            XRViewport viewport,
            XRMeshRenderer quad,
            XRMaterialFrameBuffer viewportFBO,
            Matrix4x4 worldMatrix,
            int zIndex)
            : base(renderPass, quad, worldMatrix, zIndex, null)
        {
            Viewport = viewport;
            Framebuffer = viewportFBO;
        }

        public override void Render()
        {
            //TODO: viewport renders all viewed items to the framebuffer,
            //But this method is called within the parent's rendering to its framebuffer.
            //Viewport.Render(Framebuffer);
            //FrameBuffer.CurrentlyBound?.Bind(EFramebufferTarget.DrawFramebuffer);
            base.Render();
        }
    }
}
