using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Resolves an MSAA GBuffer or forward color FBO to a single-sample FBO.
    /// Uses a 2x2 tent fragment resolve when resolving a single multisample HDR color texture
    /// under <see cref="EAntiAliasingFilterMode.Tent2x2"/>; otherwise blits each color attachment
    /// individually and resolves depth-stencil via shader.
    /// </summary>
    [RenderPipelineScriptCommand]
    [RenderCommandInfo(ERenderCommandCategory.Transfer, DoesGpuWork = true)]
    public class VPRC_ResolveMsaaGBuffer : ViewportRenderCommand
    {
        private XRMaterial? _depthResolveMaterial;
        private XRQuadFrameBuffer? _depthResolveQuad;
        private XRMaterial? _colorTentResolveMaterial;
        private XRQuadFrameBuffer? _colorTentResolveQuad;

        [RenderResourceName(ERenderResourceNameKind.FrameBuffer)]
        public string? SourceMsaaFBOName { get; set; }
        [RenderResourceName(ERenderResourceNameKind.FrameBuffer)]
        public string? DestinationFBOName { get; set; }
        [RenderResourceName(ERenderResourceNameKind.Texture)]
        public string DepthViewTextureName { get; set; } = DefaultRenderPipeline.MsaaDepthViewTextureName;
        public string? ColorViewTextureName { get; set; }

        /// <summary>
        /// Number of color attachments to resolve (CA0 through CA(N-1)).
        /// </summary>
        public int ColorAttachmentCount { get; set; } = 5;

        public bool ResolveDepthStencil { get; set; } = true;

        public VPRC_ResolveMsaaGBuffer SetOptions(
            string sourceMsaaFBO,
            string destinationFBO,
            int colorAttachmentCount = 5,
            bool resolveDepthStencil = true,
            string? depthViewTextureName = null,
            string? colorViewTextureName = null)
        {
            SourceMsaaFBOName = sourceMsaaFBO;
            DestinationFBOName = destinationFBO;
            ColorAttachmentCount = colorAttachmentCount;
            ResolveDepthStencil = resolveDepthStencil;
            if (!string.IsNullOrWhiteSpace(depthViewTextureName))
                DepthViewTextureName = depthViewTextureName!;
            if (!string.IsNullOrWhiteSpace(colorViewTextureName))
                ColorViewTextureName = colorViewTextureName;
            return this;
        }

        private static readonly EReadBufferMode[] ColorAttachments =
        [
            EReadBufferMode.ColorAttachment0,
            EReadBufferMode.ColorAttachment1,
            EReadBufferMode.ColorAttachment2,
            EReadBufferMode.ColorAttachment3,
            EReadBufferMode.ColorAttachment4,
        ];

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_depthResolveQuad is null)
            {
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
                        RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
                        WriteRed = false,
                        WriteGreen = false,
                        WriteBlue = false,
                        WriteAlpha = false,
                    }
                };
                _depthResolveMaterial.SettingUniforms += DepthResolveMaterial_SettingUniforms;

                _depthResolveQuad = new XRQuadFrameBuffer(_depthResolveMaterial, false);
            }

            if (_colorTentResolveQuad is null && !string.IsNullOrWhiteSpace(ColorViewTextureName))
            {
                _colorTentResolveMaterial = new(
                    Array.Empty<ShaderVar>(),
                    Array.Empty<XRTexture?>(),
                    XRShader.EngineShader(Path.Combine(SceneShaderPath, "ResolveColorFromTextureMS.fs"), EShaderType.Fragment))
                {
                    RenderOptions = new RenderingParameters()
                    {
                        DepthTest = new()
                        {
                            Enabled = ERenderParamUsage.Disabled,
                            Function = EComparison.Always,
                            UpdateDepth = false,
                        },
                        RequiredEngineUniforms = EUniformRequirements.ClipSpacePolicy,
                        BlendModeAllDrawBuffers = BlendMode.Disabled(),
                    }
                };
                _colorTentResolveMaterial.SettingUniforms += ColorTentResolveMaterial_SettingUniforms;

                _colorTentResolveQuad = new XRQuadFrameBuffer(_colorTentResolveMaterial, false);
            }
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

            if (_colorTentResolveQuad is not null)
            {
                _colorTentResolveQuad.Destroy();
                _colorTentResolveQuad = null;
            }

            if (_colorTentResolveMaterial is not null)
                _colorTentResolveMaterial.SettingUniforms -= ColorTentResolveMaterial_SettingUniforms;
            _colorTentResolveMaterial?.Destroy();
            _colorTentResolveMaterial = null;
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

            // Resolve each color attachment individually, or run the 2x2 tent resolve
            // when resolving a single multisample HDR color target.
            int count = Math.Min(ColorAttachmentCount, ColorAttachments.Length);
            bool useTent2x2ColorResolve = count == 1
                && _colorTentResolveQuad is not null
                && !string.IsNullOrWhiteSpace(ColorViewTextureName)
                && RenderPipeline.ResolveEffectiveAntiAliasingFilterModeForFrame() == EAntiAliasingFilterMode.Tent2x2
                && ActivePipelineInstance.GetTexture<XRTexture>(ColorViewTextureName!) is not null;

            if (useTent2x2ColorResolve)
            {
                ResolveColorTent2x2(destination);
            }
            else
            {
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
            }

            // Resolve depth-stencil (not affected by read/draw buffer selection)
            if (ResolveDepthStencil)
                ResolveDepth(source, destination);
        }

        private void ResolveColorTent2x2(XRFrameBuffer destination)
        {
            if (_colorTentResolveQuad is null)
                return;

            using var areaScope = ActivePipelineInstance.RenderState.PushRenderArea((int)destination.Width, (int)destination.Height);
            _colorTentResolveQuad.Render(destination);
        }

        private void ColorTentResolveMaterial_SettingUniforms(XRMaterialBase _, XRRenderProgram program)
        {
            if (string.IsNullOrWhiteSpace(ColorViewTextureName))
                return;

            var msaaColor = ActivePipelineInstance.GetTexture<XRTexture>(ColorViewTextureName!);
            if (msaaColor is null)
                return;

            program.Sampler("ColorView", msaaColor, 0);
        }

        private void ResolveDepth(XRFrameBuffer source, XRFrameBuffer destination)
        {
            if (_depthResolveQuad is null)
                return;

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
            if (!string.IsNullOrWhiteSpace(ColorViewTextureName))
                builder.SampleTexture(ColorViewTextureName!);
            builder.UseColorAttachment(MakeFboColorResource(DestinationFBOName));
        }
    }
}
