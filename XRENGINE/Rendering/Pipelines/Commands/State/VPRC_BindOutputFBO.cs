using XREngine.Data.Rendering;
using XREngine.Data.Colors;
using XREngine.Rendering.RenderGraph;
using System;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Binds the FBO that is set as the output FBO in the pipeline.
    /// This FBO may be null if the pipeline is not rendering to a framebuffer.
    /// </summary>
    /// <param name="pipeline"></param>
    public class VPRC_BindOutputFBO : ViewportStateRenderCommand<VPRC_UnbindFBO>
    {
        public bool Write { get; set; } = true;
        public bool ClearColor { get; set; } = true;
        public bool ClearDepth { get; set; } = true;
        public bool ClearStencil { get; set; } = true;

        public void SetOptions(bool write = true, bool clearColor = true, bool clearDepth = true, bool clearStencil = true)
        {
            Write = write;
            ClearColor = clearColor;
            ClearDepth = clearDepth;
            ClearStencil = clearStencil;
        }

        protected override void Execute()
        {
            using var passScope = Engine.Rendering.State.CurrentRenderGraphPassIndex == int.MinValue
                ? Engine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.PreRender)
                : default;

            var fbo = ActivePipelineInstance.RenderState.OutputFBO;

            /*
            //if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_DEBUG_PRESENT_CLEAR")))
            {
                Debug.RenderingEvery(
                    $"PresentDbg.BindOutputFBO.{Engine.PlayMode.State}",
                    TimeSpan.FromSeconds(1),
                    "[PresentDbg] BindOutputFBO. PlayMode={0} OutputFBO_Null={1} OutputFBO={2} Write={3}",
                    Engine.PlayMode.State,
                    fbo is null,
                    fbo?.Name ?? "<null>",
                    Write);
            }
            */

            if (fbo is null)
            {
                // OutputFBO == null means "render to the window". We must still explicitly bind the
                // default framebuffer; otherwise, any previous pass/callback that bound an FBO will
                // cause the final blit to render offscreen (black window).
                Engine.Rendering.State.UnbindFrameBuffers(EFramebufferTarget.Framebuffer);

                // Debug aid: set `XRE_DEBUG_PRESENT_CLEAR=1` to clear the default framebuffer to a vivid color.
                // If the window stays black, we're likely not binding/presenting the default framebuffer.
                // If it turns magenta but the scene never appears, the final blit/composite isn't drawing.
                bool debugPresentClear = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XRE_DEBUG_PRESENT_CLEAR"));
                if (debugPresentClear)
                {
                    Engine.Rendering.State.ClearColor(ColorF4.Magenta);
                    Engine.Rendering.State.Clear(true, true, true);
                    // Skip the normal clear so the magenta survives to present.
                }
                else if (ClearColor || ClearDepth || ClearStencil)
                {
                    Engine.Rendering.State.Clear(ClearColor, ClearDepth, ClearStencil);
                }

                return;
            }
            
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
            context.PushRenderTarget(RenderGraphResourceNames.OutputRenderTarget, Write, ClearColor, ClearDepth, ClearStencil);
        }
    }
}
