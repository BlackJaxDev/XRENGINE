using System;
using System.Collections.Generic;
using System.IO;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Renders an FBO quad to another FBO.
    /// Useful for transforming every pixel of previous FBO.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    [XRTypeRedirect("XREngine.Rendering.Pipelines.Commands.VPRC_RenderQuadFBO")]
    [RenderPipelineScriptCommand]
    public partial class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        private static bool DiagnosticsEnabled => RenderDiagnosticsFlags.DiagQuadBlit;

        /// <summary>
        /// The name of the source FBO that contains the quad to render.
        /// </summary>
        public string? SourceQuadFBOName { get; set; }
        /// <summary>
        /// The name of the destination FBO to render into. 
        /// If null, the current render target will be used.
        /// </summary>
        public string? DestinationFBOName { get; set; } = null;
        /// <summary>
        /// The name of the source FBO that contains the quad to render.
        /// </summary>
        public string? FrameBufferName
        {
            get => SourceQuadFBOName;
            set => SourceQuadFBOName = value;
        }
        /// <summary>
        /// The name of the destination FBO to render into.
        /// </summary>
        public string? TargetFrameBufferName
        {
            get => DestinationFBOName;
            set => DestinationFBOName = value;
        }

        /// <summary>
        /// If true, the source FBO will be rendered into itself.
        /// </summary>
        public bool RenderToSourceFrameBuffer { get; set; }
        /// <summary>
        /// If true, the render area will match the destination FBO's dimensions.
        /// </summary>
        public bool MatchDestinationRenderArea { get; set; }
        /// <summary>
        /// Treats this draw as an intentional one-layer mono control rendered while
        /// the surrounding pipeline is stereo. This is reserved for validation
        /// oracles that compare a mono shader invocation with the SPS result.
        /// </summary>
        public bool IsolatedMonoReference { get; set; }
        /// <summary>
        /// The variant of the render graph pass to use.
        /// </summary>
        public string? RenderGraphPassVariant { get; set; }
        /// <summary>
        /// The resources used by the render graph pass.
        /// </summary>
        public RenderGraphResourceDescriptor? RenderGraphResources { get; set; }

        /// <summary>
        /// The GPU profiling name for this command, which includes the source and destination FBO names and the shader label if available.
        /// </summary>
        public override string GpuProfilingName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(SourceQuadFBOName))
                    return base.GpuProfilingName;

                XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
                string destination = ResolveDestinationLabel(activeInstance);
                string shaderLabel = ResolveShaderLabel(activeInstance);

                return string.IsNullOrWhiteSpace(shaderLabel)
                    ? $"{base.GpuProfilingName}[{SourceQuadFBOName}->{destination}]"
                    : $"{base.GpuProfilingName}[{SourceQuadFBOName}->{destination}; {shaderLabel}]";
            }
        }

        /// <summary>
        /// Sets the source and destination FBO names for this command.
        /// </summary>
        /// <param name="sourceQuadFBOName">The name of the source FBO.</param>
        /// <param name="destinationFBOName">The name of the destination FBO.</param>
        /// <param name="matchDestinationRenderArea">If true, the render area will match the destination FBO's dimensions.</param>
        /// <returns>The current instance of <see cref="VPRC_RenderQuadToFBO"/>.</returns>
        public VPRC_RenderQuadToFBO SetTargets(string sourceQuadFBOName, string? destinationFBOName = null, bool matchDestinationRenderArea = false)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
            RenderToSourceFrameBuffer = false;
            MatchDestinationRenderArea = matchDestinationRenderArea;
            return this;
        }

        /// <summary>
        /// Sets the render graph pass variant for this command.
        /// </summary>
        /// <param name="variant">The variant of the render graph pass.</param>
        /// <returns>The current instance of <see cref="VPRC_RenderQuadToFBO"/>.</returns>
        public VPRC_RenderQuadToFBO SetRenderGraphPassVariant(string? variant)
        {
            RenderGraphPassVariant = variant;
            return this;
        }

        public VPRC_RenderQuadToFBO SetIsolatedMonoReference(bool enabled = true)
        {
            IsolatedMonoReference = enabled;
            return this;
        }

        /// <summary>
        /// Sets the render graph resources for this command.
        /// </summary>
        /// <param name="resources">The render graph resources.</param>
        /// <returns>The current instance of <see cref="VPRC_RenderQuadToFBO"/>.</returns>
        public VPRC_RenderQuadToFBO SetRenderGraphResources(RenderGraphResourceDescriptor? resources)
        {
            RenderGraphResources = resources;
            return this;
        }

        /// <summary>
        /// Configures the render graph resources for this command.
        /// </summary>
        /// <param name="configure">An action to configure the render graph resources.</param>
        /// <returns>The current instance of <see cref="VPRC_RenderQuadToFBO"/>.</returns>
        public VPRC_RenderQuadToFBO ConfigureRenderGraphResources(Action<RenderGraphResourceDescriptor> configure)
        {
            RenderGraphResourceDescriptor resources = RenderGraphResources ??= new();
            configure(resources);
            return this;
        }

        /// <summary>
        /// Sets the options for this command.
        /// </summary>
        /// <param name="frameBufferName">The name of the source FBO.</param>
        /// <param name="targetFrameBufferName">The name of the destination FBO.</param>
        /// <param name="renderToSourceFrameBuffer">If true, the command will render to the source FBO.</param>
        /// <returns>The current instance of <see cref="VPRC_RenderQuadToFBO"/>.</returns>
        public VPRC_RenderQuadToFBO SetOptions(
            string frameBufferName,
            string? targetFrameBufferName = null,
            bool renderToSourceFrameBuffer = false)
        {
            SourceQuadFBOName = frameBufferName;
            DestinationFBOName = targetFrameBufferName;
            RenderToSourceFrameBuffer = renderToSourceFrameBuffer;
            MatchDestinationRenderArea = false;
            return this;
        }
    }
}
