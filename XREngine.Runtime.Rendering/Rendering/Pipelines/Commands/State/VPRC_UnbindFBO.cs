using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_UnbindFBO : ViewportPopStateRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
            => requirements.RequireOperation("framebuffer");

        /// <summary>
        /// The framebuffer to unbind. This should be set by bind command, and will be set to null after execution.
        /// </summary>
        public XRFrameBuffer? FrameBuffer { get; set; }
        public IDisposable? RenderTargetScope { get; set; }
        [System.ComponentModel.DefaultValue(true)]
        public bool Write { get; set; } = true;

        public void SetOptions(XRFrameBuffer frameBuffer, bool write)
        {
            FrameBuffer = frameBuffer;
            Write = write;
        }

        /// <summary>Releases an acquired binding when execution cannot reach its queued pop.</summary>
        internal void UnwindBinding()
        {
            try { Execute(); }
            finally { ShouldExecute = true; }
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
                IDisposable? targetScope = RenderTargetScope;
                RenderTargetScope = null;
                targetScope?.Dispose();
            }
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            context.PopRenderTarget();
        }
    }
}
