using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Resolves an MSAA GBuffer FBO to a non-MSAA GBuffer FBO by blitting each color attachment
    /// and depth-stencil individually. Required because glBlitFramebuffer only operates on
    /// one color read/draw buffer pair at a time.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_ResolveMsaaGBuffer : ViewportRenderCommand
    {
        private XRMaterial? _depthResolveMaterial;
        private XRQuadFrameBuffer? _depthResolveQuad;

        public string? SourceMsaaFBOName { get; set; }
        public string? DestinationFBOName { get; set; }
        public string DepthViewTextureName { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;

        /// <summary>
        /// Number of color attachments to resolve (CA0 through CA(N-1)).
        /// </summary>
        public int ColorAttachmentCount { get; set; } = 4;

        public bool ResolveDepthStencil { get; set; } = true;

        public VPRC_ResolveMsaaGBuffer SetOptions(
            string sourceMsaaFBO,
            string destinationFBO,
            int colorAttachmentCount = 4,
            bool resolveDepthStencil = true,
            string? depthViewTextureName = null)
        {
            SourceMsaaFBOName = sourceMsaaFBO;
            DestinationFBOName = destinationFBO;
            ColorAttachmentCount = colorAttachmentCount;
            ResolveDepthStencil = resolveDepthStencil;
            if (!string.IsNullOrWhiteSpace(depthViewTextureName))
                DepthViewTextureName = depthViewTextureName!;
            return this;
        }

        private static readonly EReadBufferMode[] ColorAttachments =
        [
            EReadBufferMode.ColorAttachment0,
            EReadBufferMode.ColorAttachment1,
            EReadBufferMode.ColorAttachment2,
            EReadBufferMode.ColorAttachment3,
        ];

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_depthResolveQuad is not null)
                return;

            _depthResolveMaterial = new(
                [new ShaderBool(false, "IsReversedDepth")],
                Array.Empty<XRTexture?>(),
                XRShader.EngineShader(Path.Combine(SceneShaderPath, "CopyDepthFromTextureMS.fs"), EShaderType.Fragment))
            {
                RenderOptions = new RenderingParameters()
                {
                    DepthTest = new()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Always,
                        UpdateDepth = true,
                    },
                    WriteRed = false,
                    WriteGreen = false,
                    WriteBlue = false,
                    WriteAlpha = false,
                }
            };
            _depthResolveMaterial.SettingUniforms += DepthResolveMaterial_SettingUniforms;

            _depthResolveQuad = new XRQuadFrameBuffer(_depthResolveMaterial, false);
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_depthResolveQuad is not null)
            {
                _depthResolveQuad.Destroy();
                _depthResolveQuad = null;
            }

            if (_depthResolveMaterial is not null)
                _depthResolveMaterial.SettingUniforms -= DepthResolveMaterial_SettingUniforms;
            _depthResolveMaterial?.Destroy();
            _depthResolveMaterial = null;
        }

        protected override void Execute()
        {
            if (SourceMsaaFBOName is null || DestinationFBOName is null)
                return;

            var source = ActivePipelineInstance.GetFBO<XRFrameBuffer>(SourceMsaaFBOName);
            var destination = ActivePipelineInstance.GetFBO<XRFrameBuffer>(DestinationFBOName);
            if (source is null || destination is null)
                return;

            var renderer = AbstractRenderer.Current;
            if (renderer is null)
                return;

/*
            Debug.RenderingEvery(
                $"ResolveMsaaGBuffer.{ActivePipelineInstance.GetHashCode()}.{SourceMsaaFBOName}.{DestinationFBOName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] ResolveMsaaGBuffer Source={0} Dest={1} Colors={2} ResolveDepth={3} SrcSamples={4} DstSamples={5} SrcSize={6}x{7} DstSize={8}x{9}",
                SourceMsaaFBOName,
                DestinationFBOName,
                ColorAttachmentCount,
                ResolveDepthStencil,
                source.EffectiveSampleCount,
                destination.EffectiveSampleCount,
                source.Width,
                source.Height,
                destination.Width,
                destination.Height);
*/

            // Resolve each color attachment individually
            int count = Math.Min(ColorAttachmentCount, ColorAttachments.Length);
            for (int i = 0; i < count; i++)
            {
                renderer.BlitFBOToFBOSingleAttachment(
                    source, destination,
                    ColorAttachments[i], ColorAttachments[i],
                    colorBit: true, depthBit: false, stencilBit: false,
                    linearFilter: false);
            }

            // BlitWithDrawBuffer (DSA) mutates the destination FBO's draw buffer state
            // behind the GLFrameBuffer wrapper. Restore the full MRT configuration so
            // subsequent passes that write to this FBO see all color attachments.
            destination.RestoreDrawBuffers();

            // Resolve depth-stencil (not affected by read/draw buffer selection)
            if (ResolveDepthStencil)
            {
                ResolveDepth(source, destination);
            }
        }

        private void ResolveDepth(XRFrameBuffer source, XRFrameBuffer destination)
        {
            if (_depthResolveQuad is null)
                return;

/*
            Debug.RenderingEvery(
                $"ResolveMsaaGBuffer.Depth.{ActivePipelineInstance.GetHashCode()}.{SourceMsaaFBOName}.{DestinationFBOName}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] ResolveMsaaDepth Source={0} Dest={1} DepthView={2} SrcSamples={3} DstSamples={4}",
                SourceMsaaFBOName ?? "<null>",
                DestinationFBOName ?? "<null>",
                DepthViewTextureName,
                source.EffectiveSampleCount,
                destination.EffectiveSampleCount);
*/

            using var areaScope = ActivePipelineInstance.RenderState.PushRenderArea((int)destination.Width, (int)destination.Height);
            _depthResolveQuad.Render(destination);
        }

        private void DepthResolveMaterial_SettingUniforms(XRMaterialBase _, XRRenderProgram program)
        {
            var msaaDepthView = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTextureName);
            if (msaaDepthView is null)
                return;

            bool isReversedDepth = ActivePipelineInstance.RenderState.RenderingCamera?.IsReversedDepth
                ?? ActivePipelineInstance.RenderState.SceneCamera?.IsReversedDepth
                ?? false;

            program.Sampler("DepthView", msaaDepthView, 0);
            program.Uniform("IsReversedDepth", isReversedDepth);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceMsaaFBOName is null || DestinationFBOName is null)
                return;

            var builder = context.GetOrCreateSyntheticPass($"ResolveMsaaGBuffer_{SourceMsaaFBOName}_to_{DestinationFBOName}")
                .WithStage(ERenderGraphPassStage.Transfer);

            builder.SampleTexture(MakeFboColorResource(SourceMsaaFBOName));
            builder.UseColorAttachment(MakeFboColorResource(DestinationFBOName));
        }
    }
}
