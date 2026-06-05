using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_UnbindFBO : ViewportPopStateRenderCommand
    {
        /// <summary>
        /// The framebuffer to unbind. This should be set by bind command, and will be set to null after execution.
        /// </summary>
        public XRFrameBuffer? FrameBuffer { get; set; }
        public IDisposable? RenderTargetScope { get; set; }
        public bool Write { get; set; } = true;

        public void SetOptions(XRFrameBuffer frameBuffer, bool write)
        {
            FrameBuffer = frameBuffer;
            Write = write;
        }

        protected override void Execute()
        {
            try
            {
                if (FrameBuffer is not null)
                {
                    if (Write)
                        FrameBuffer.UnbindFromWriting();
                    else
                        FrameBuffer.UnbindFromReading();
                }
            }
            finally
            {
                FrameBuffer = null;
                RenderTargetScope?.Dispose();
                RenderTargetScope = null;
            }
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            context.PopRenderTarget();
        }
    }
}
