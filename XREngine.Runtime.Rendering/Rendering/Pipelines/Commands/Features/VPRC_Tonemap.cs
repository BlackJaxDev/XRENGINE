using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Applies a standalone tonemapping pass from a named input texture into the current or specified output framebuffer.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_Tonemap : ViewportRenderCommand
    {
        private XRMaterial? _material;
        private XRQuadFrameBuffer? _quad;

        public string SourceTextureName { get; set; } = string.Empty;
        public string? BloomTextureName { get; set; }
        public string? DestinationFBOName { get; set; }
        public ETonemappingType TonemapType { get; set; } = ETonemappingType.Mobius;
        public float MobiusTransition { get; set; } = TonemappingSettings.DefaultMobiusTransition;
        public float Exposure { get; set; } = 1.0f;
        public float Gamma { get; set; } = 2.2f;
        public float BloomStrength { get; set; } = 0.0f;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(SourceTextureName)
                ? nameof(VPRC_Tonemap)
                : $"{nameof(VPRC_Tonemap)}:{SourceTextureName}";

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad is not null)
                return;

            _material = new(Array.Empty<XRTexture?>(), ShaderHelper.LoadEngineShader("Scene3D/TonemapStandalone.fs", EShaderType.Fragment))
            {
                RenderOptions = CreateRenderOptions()
            };

            _quad = new XRQuadFrameBuffer(_material);
            _quad.SettingUniforms += Tonemap_SettingUniforms;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (_quad is not null)
            {
                _quad.SettingUniforms -= Tonemap_SettingUniforms;
                _quad.Destroy();
                _quad = null;
            }

            _material?.Destroy();
            _material = null;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            if (_quad is null ||
                string.IsNullOrWhiteSpace(SourceTextureName) ||
                !instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) ||
                sourceTexture is null)
                return;

            XRFrameBuffer? destination = null;
            if (!string.IsNullOrWhiteSpace(DestinationFBOName))
            {
                destination = instance.GetFBO<XRFrameBuffer>(DestinationFBOName!);
                if (destination is null)
                    return;
            }

            _quad.Render(destination);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (string.IsNullOrWhiteSpace(SourceTextureName))
                return;

            string destination = DestinationFBOName
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass($"Tonemap_{SourceTextureName}_to_{destination}");
            builder.WithStage(ERenderGraphPassStage.Graphics);
            builder.SampleTexture(MakeTextureResource(SourceTextureName));
            if (!string.IsNullOrWhiteSpace(BloomTextureName))
                builder.SampleTexture(MakeTextureResource(BloomTextureName!));
            builder.UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.DontCare, ERenderPassStoreOp.Store);
        }

        private static RenderingParameters CreateRenderOptions() => new()
        {
            DepthTest =
            {
                Enabled = ERenderParamUsage.Disabled,
                UpdateDepth = false,
                Function = EComparison.Always,
            }
        };

        private void Tonemap_SettingUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (!instance.TryGetTexture(SourceTextureName, out XRTexture? sourceTexture) || sourceTexture is null)
                return;

            XRTexture? bloomTexture = null;
            bool useBloom = !string.IsNullOrWhiteSpace(BloomTextureName) &&
                instance.TryGetTexture(BloomTextureName!, out bloomTexture) &&
                bloomTexture is not null;

            program.Sampler("SourceTexture", sourceTexture, 0);
            if (useBloom)
                program.Sampler("BloomTexture", bloomTexture!, 1);

            program.Uniform("UseBloom", useBloom);
            program.Uniform("BloomStrength", BloomStrength);
            program.Uniform("Exposure", Exposure);
            program.Uniform("Gamma", Gamma);
            program.Uniform("TonemapType", (int)TonemapType);
            program.Uniform("MobiusTransition", Math.Clamp(MobiusTransition, TonemappingSettings.MinMobiusTransition, TonemappingSettings.MaxMobiusTransition));
        }
    }
}
