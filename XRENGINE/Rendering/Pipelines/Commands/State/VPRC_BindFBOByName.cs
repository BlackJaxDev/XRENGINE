using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_BindFBOByName : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public string? FrameBufferName { get; set; }
        public bool Write { get; set; } = true;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;

        public void SetOptions(string frameBufferName, bool write = true, bool clearColor = true, bool clearDepth = true, bool clearStencil = true)
        {
            FrameBufferName = frameBufferName;
            Write = write;
            ClearColor = clearColor;
            ClearDepth = clearDepth;
            ClearStencil = clearStencil;
        }

        protected override void Execute()
        {
            if (FrameBufferName is null)
                return;

            var fbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(FrameBufferName);
            if (fbo is null)
                return;

            if (Write)
                fbo.BindForWriting();
            else
                fbo.BindForReading();

            PopCommand.FrameBuffer = fbo;
            PopCommand.Write = Write;

            if (ClearColor || ClearDepth || ClearStencil)
                Engine.Rendering.State.ClearByBoundFBO(ClearColor, ClearDepth, ClearStencil);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (FrameBufferName is not null)
                context.PushRenderTarget(FrameBufferName, Write, ClearColor, ClearDepth, ClearStencil);
        }
    }
}
