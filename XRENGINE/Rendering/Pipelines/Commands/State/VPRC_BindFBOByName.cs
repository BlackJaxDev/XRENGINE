using System;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_BindFBOByName : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public string? FrameBufferName { get; set; }
        public bool Write { get; set; } = true;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;

        /// <summary>
        /// When set, evaluated at render time to determine the FBO name,
        /// overriding <see cref="FrameBufferName"/>.
        /// </summary>
        public Func<string?>? DynamicName { get; set; }

        /// <summary>
        /// When set, evaluated at render time to determine whether to clear color,
        /// overriding <see cref="ClearColor"/>.
        /// </summary>
        public Func<bool>? DynamicClearColor { get; set; }

        /// <summary>
        /// When set, evaluated at render time to determine whether to clear depth,
        /// overriding <see cref="ClearDepth"/>.
        /// </summary>
        public Func<bool>? DynamicClearDepth { get; set; }

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
            string? name = DynamicName?.Invoke() ?? FrameBufferName;
            if (name is null)
                return;

            var fbo = ActivePipelineInstance.GetFBO<XRFrameBuffer>(name);
            if (fbo is null)
                return;

            if (Write)
                fbo.BindForWriting();
            else
                fbo.BindForReading();

            PopCommand.FrameBuffer = fbo;
            PopCommand.Write = Write;

            bool clearColor = DynamicClearColor?.Invoke() ?? ClearColor;
            bool clearDepth = DynamicClearDepth?.Invoke() ?? ClearDepth;

            if (clearColor || clearDepth || ClearStencil)
                Engine.Rendering.State.ClearByBoundFBO(clearColor, clearDepth, ClearStencil);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (FrameBufferName is not null)
                context.PushRenderTarget(FrameBufferName, Write, ClearColor, ClearDepth, ClearStencil);
        }
    }
}
