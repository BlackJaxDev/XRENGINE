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
    public class VPRC_RenderQuadToFBO : ViewportRenderCommand
    {
        private const string LightProbeIrradianceArrayTextureName = "LightProbeIrradianceArray";
        private const string LightProbePrefilterArrayTextureName = "LightProbePrefilterArray";
        private const string LightProbePositionBufferName = "LightProbePositions";
        private const string LightProbeTetraBufferName = "LightProbeTetrahedra";
        private const string LightProbeParamBufferName = "LightProbeParameters";
        private const string LightProbeGridCellBufferName = "LightProbeGridCells";
        private const string LightProbeGridIndexBufferName = "LightProbeGridIndices";

        private static readonly string[] DeferredLightCombineTextureInputs =
        [
            DefaultRenderPipeline.AlbedoOpacityTextureName,
            DefaultRenderPipeline.NormalTextureName,
            DefaultRenderPipeline.RMSETextureName,
            DefaultRenderPipeline.AmbientOcclusionIntensityTextureName,
            DefaultRenderPipeline.DepthViewTextureName,
            DefaultRenderPipeline.LightingAccumTextureName,
            DefaultRenderPipeline.BRDFTextureName,
            LightProbeIrradianceArrayTextureName,
            LightProbePrefilterArrayTextureName,
        ];

        private static readonly string[] DeferredLightCombineBufferInputs =
        [
            LightProbePositionBufferName,
            LightProbeTetraBufferName,
            LightProbeParamBufferName,
            LightProbeGridCellBufferName,
            LightProbeGridIndexBufferName,
        ];

        private static readonly string[] PostProcessTextureInputs =
        [
            DefaultRenderPipeline.HDRSceneTextureName,
            DefaultRenderPipeline.BloomBlurTextureName,
            DefaultRenderPipeline.DepthViewTextureName,
            DefaultRenderPipeline.StencilViewTextureName,
            DefaultRenderPipeline.AutoExposureTextureName,
            DefaultRenderPipeline.AtmosphereColorTextureName,
            DefaultRenderPipeline.VolumetricFogColorTextureName,
        ];

        private static readonly string[] FinalPostProcessTextureInputs =
        [
            DefaultRenderPipeline.PostProcessOutputTextureName,
        ];

        private static readonly string[] TsrUpscaleTextureInputs =
        [
            DefaultRenderPipeline.FinalPostProcessOutputTextureName,
            DefaultRenderPipeline.VelocityTextureName,
            DefaultRenderPipeline.DepthViewTextureName,
            DefaultRenderPipeline.HistoryDepthViewTextureName,
            DefaultRenderPipeline.TsrHistoryColorTextureName,
            DefaultRenderPipeline.StencilViewTextureName,
        ];

        private static bool _diagEnabled => RenderDiagnosticsFlags.DiagQuadBlit;

        public string? SourceQuadFBOName { get; set; }
        public string? DestinationFBOName { get; set; } = null;
        public string? FrameBufferName
        {
            get => SourceQuadFBOName;
            set => SourceQuadFBOName = value;
        }
        public string? TargetFrameBufferName
        {
            get => DestinationFBOName;
            set => DestinationFBOName = value;
        }
        public bool RenderToSourceFrameBuffer { get; set; }
        public bool MatchDestinationRenderArea { get; set; }

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

        public VPRC_RenderQuadToFBO SetTargets(string sourceQuadFBOName, string? destinationFBOName = null, bool matchDestinationRenderArea = false)
        {
            SourceQuadFBOName = sourceQuadFBOName;
            DestinationFBOName = destinationFBOName;
            RenderToSourceFrameBuffer = false;
            MatchDestinationRenderArea = matchDestinationRenderArea;
            return this;
        }

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

        protected string ResolveDestinationLabel(XRRenderPipelineInstance? activeInstance)
            => DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? activeInstance?.RenderState.CurrentRenderTargetBinding?.Name
                ?? activeInstance?.RenderState.OutputFBO?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

        protected XRFrameBuffer? ResolveDestinationFbo(XRRenderPipelineInstance? activeInstance, XRQuadFrameBuffer? sourceFBO)
        {
            if (DestinationFBOName is not null)
                return activeInstance?.GetFBO<XRFrameBuffer>(DestinationFBOName);

            if (RenderToSourceFrameBuffer)
                return sourceFBO ?? (SourceQuadFBOName is not null
                    ? activeInstance?.GetFBO<XRFrameBuffer>(SourceQuadFBOName)
                    : null);

            // Null destination means "draw into the current render target".  Use
            // the logical pipeline target stack, not the backend's physical FBO
            // stack, because Vulkan records these operations after command
            // execution has already produced frame ops.
            return activeInstance?.RenderState.CurrentRenderTargetBinding?.FrameBuffer
                ?? activeInstance?.RenderState.OutputFBO;
        }

        protected static string BuildQuadBlitPassName(string sourceFboName, string destination)
            => $"QuadBlit_{sourceFboName}_to_{destination}";

        private string ResolveShaderLabel(XRRenderPipelineInstance? activeInstance)
        {
            XRQuadFrameBuffer? sourceFBO = SourceQuadFBOName is null
                ? null
                : activeInstance?.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);

            XRMaterial? material = sourceFBO?.Material;
            if (material is null)
                return string.Empty;

            IReadOnlyList<XRShader> fragmentShaders = material.FragmentShaders;
            XRShader? fragmentShader = fragmentShaders.Count > 0
                ? fragmentShaders[fragmentShaders.Count - 1]
                : null;

            string shaderName = GetShaderDisplayName(fragmentShader);
            if (string.IsNullOrWhiteSpace(material.Name))
                return shaderName;

            return string.IsNullOrWhiteSpace(shaderName)
                ? $"material={material.Name}"
                : $"material={material.Name}; shader={shaderName}";
        }

        private static string GetShaderDisplayName(XRShader? shader)
        {
            if (shader is null)
                return string.Empty;

            string? path = shader.Source?.FilePath ?? shader.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFileName(path);

            return shader.Name ?? string.Empty;
        }

        protected override void Execute()
        {
            if (SourceQuadFBOName is null)
                return;

            var activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
            {
                Debug.RenderingWarningEvery(
                    $"QuadBlit.MissingPipeline.{SourceQuadFBOName}.{DestinationFBOName}",
                    TimeSpan.FromSeconds(5),
                    "[QuadBlitDiag] Skipping quad blit from '{0}' to '{1}': no active render pipeline instance.",
                    SourceQuadFBOName,
                    DestinationFBOName ?? "<current>");
                return;
            }

            string destination = ResolveDestinationLabel(activeInstance);

            string passName = BuildQuadBlitPassName(SourceQuadFBOName, destination);
            int passIndex = ResolvePassIndex(passName, out bool hasRenderGraphMetadata);
            if (passIndex == int.MinValue && hasRenderGraphMetadata)
            {
                Debug.RenderingWarningEvery(
                    $"QuadBlit.MissingRenderGraphPass.{passName}",
                    TimeSpan.FromSeconds(2),
                    "[QuadBlitDiag] Skipping quad blit '{0}': no matching render-graph pass metadata was generated.",
                    passName);
                return;
            }

            using var passScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;

            XRQuadFrameBuffer? sourceFBO = activeInstance.GetFBO<XRQuadFrameBuffer>(SourceQuadFBOName);
            if (sourceFBO is null)
            {
                if (_diagEnabled)
                    Debug.RenderingWarning($"[QuadBlitDiag] Source FBO '{SourceQuadFBOName}' not found as XRQuadFrameBuffer.");
                return;
            }

            XRFrameBuffer? destFBO = ResolveDestinationFbo(activeInstance, sourceFBO);
            if (_diagEnabled && DestinationFBOName is not null && destFBO is null)
                Debug.RenderingWarning($"[QuadBlitDiag] Dest FBO '{DestinationFBOName}' not found.");

            if (DeferredLightingDiagnostics.Enabled &&
                (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.LightCombineFBOName, StringComparison.Ordinal) ||
                 string.Equals(SourceQuadFBOName, DefaultRenderPipeline.MsaaLightCombineFBOName, StringComparison.Ordinal)))
            {
                Debug.VulkanEvery(
                    $"DeferredLighting.RenderQuadToFBO.{SourceQuadFBOName}.{destination}",
                    TimeSpan.FromSeconds(1),
                    "[DeferredLightingDiag][RenderQuadToFBO] passName='{0}' pass={1} hasMetadata={2} source='{3}' destination='{4}' destFbo='{5}' sourceTargets={6} destTargets={7}",
                    passName,
                    passIndex,
                    hasRenderGraphMetadata,
                    SourceQuadFBOName,
                    destination,
                    destFBO?.Name ?? "<current/null>",
                    sourceFBO.Targets?.Length ?? 0,
                    destFBO?.Targets?.Length ?? 0);
            }

            if ((string.Equals(SourceQuadFBOName, DefaultRenderPipeline.PostProcessFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, "PostProcessOutputFBO", StringComparison.Ordinal))
                || (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.FxaaFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, DefaultRenderPipeline.FxaaFBOName, StringComparison.Ordinal))
                || (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal)
                    && string.Equals(DestinationFBOName, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal))
                || string.Equals(SourceQuadFBOName, DefaultRenderPipeline.MsaaLightCombineFBOName, StringComparison.Ordinal))
            {
                /*
                Debug.RenderingEvery(
                    $"QuadBlit.{ActivePipelineInstance.GetHashCode()}.{SourceQuadFBOName}.{DestinationFBOName}",
                    TimeSpan.FromSeconds(1),
                    "[RenderDiag] QuadBlit Source={0} Dest={1} SourceTargets={2} DestTargets={3} DestType={4} CurrentOutput={5}",
                    SourceQuadFBOName,
                    DestinationFBOName ?? "<current>",
                    sourceFBO.Targets?.Length ?? 0,
                    destFBO?.Targets?.Length ?? 0,
                    destFBO?.GetType().Name ?? "<null>",
                    ActivePipelineInstance.RenderState.OutputFBO?.Name ?? "<backbuffer>");
                */
            }

            if (_diagEnabled)
            {
                bool hasTargets = destFBO?.Targets is { Length: > 0 };
                Debug.Log(ELogCategory.Rendering, $"[QuadBlitDiag] Rendering '{SourceQuadFBOName}' → '{DestinationFBOName ?? "<current>"}' (dest has targets: {hasTargets}, dest type: {destFBO?.GetType().Name ?? "null"})");
            }

            using var renderAreaScope = MatchDestinationRenderArea && destFBO is { Width: > 0, Height: > 0 }
                ? activeInstance.RenderState.PushRenderArea((int)destFBO.Width, (int)destFBO.Height)
                : default;

            sourceFBO.Render(destFBO);
        }

        protected int ResolvePassIndex(string passName, out bool hasRenderGraphMetadata)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is not { Count: > 0 } renderPasses)
            {
                hasRenderGraphMetadata = false;
                return int.MinValue;
            }

            hasRenderGraphMetadata = true;

            foreach (var match in renderPasses)
            {
                if (string.Equals(match.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return match.PassIndex;
            }

            return int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (SourceQuadFBOName is null)
                return;

            string destination = DestinationFBOName
                ?? (RenderToSourceFrameBuffer ? SourceQuadFBOName : null)
                ?? context.CurrentRenderTarget?.Name
                ?? RenderGraphResourceNames.OutputRenderTarget;

            var builder = context.GetOrCreateSyntheticPass(BuildQuadBlitPassName(SourceQuadFBOName, destination));
            builder.WithStage(ERenderGraphPassStage.Graphics);
            DescribeQuadMaterialInputs(builder, SourceQuadFBOName, destination);

            if (string.Equals(SourceQuadFBOName, DefaultRenderPipeline.LightCombineFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.ForwardPassFBOName, StringComparison.Ordinal))
            {
                builder.DependsOn((int)EDefaultRenderPass.DeferredDecals);
                context.Metadata
                    .ForPass((int)EDefaultRenderPass.Background, EDefaultRenderPass.Background.ToString(), ERenderGraphPassStage.Graphics)
                    .DependsOn(builder.PassIndex);
            }

            ERenderPassLoadOp colorLoad = ERenderPassLoadOp.Load;
            ERenderPassStoreOp colorStore = ERenderPassStoreOp.Store;
            ERenderGraphAccess access = ERenderGraphAccess.ReadWrite;

            if (context.CurrentRenderTarget is { } bound &&
                string.Equals(bound.Name, destination, StringComparison.OrdinalIgnoreCase))
            {
                colorLoad = bound.ConsumeColorLoadOp();
                colorStore = bound.GetColorStoreOp();
                access = bound.ColorAccess;
            }

            DescribeColorOutput(builder, SourceQuadFBOName, destination, access, colorLoad, colorStore);

            if (SamplesSharedDepthView(SourceQuadFBOName, destination))
            {
                builder.UseDepthAttachment(
                    MakeFboDepthResource(destination),
                    ERenderGraphAccess.Read,
                    ERenderPassLoadOp.Load,
                    ERenderPassStoreOp.Store);
            }
        }

        private static void DescribeQuadMaterialInputs(RenderPassBuilder builder, string sourceFboName, string destination)
        {
            if (DescribeAmbientOcclusionInputs(builder, sourceFboName, destination))
                return;

            if (string.Equals(sourceFboName, DefaultRenderPipeline.LightCombineFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.ForwardPassFBOName, StringComparison.Ordinal))
            {
                DescribeDeferredLightCombineInputs(builder);
                return;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.PostProcessFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.PostProcessOutputFBOName, StringComparison.Ordinal))
            {
                DescribePostProcessInputs(builder);
                return;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.FinalPostProcessFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.FinalPostProcessOutputFBOName, StringComparison.Ordinal))
            {
                DescribeTextureInputs(builder, FinalPostProcessTextureInputs);
                return;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.TsrUpscaleFBOName, StringComparison.Ordinal))
            {
                DescribeTextureInputs(builder, TsrUpscaleTextureInputs);
                return;
            }

            if (!string.Equals(sourceFboName, destination, StringComparison.Ordinal))
                builder.SampleTexture(MakeFboColorResource(sourceFboName));
        }

        private static void DescribeColorOutput(
            RenderPassBuilder builder,
            string sourceFboName,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            if (TryDescribeActualColorOutputs(builder, destination, access, colorLoad, colorStore))
                return;

            if (TryDescribeAmbientOcclusionColorOutput(builder, sourceFboName, destination, access, colorLoad, colorStore))
                return;

            builder.UseColorAttachment(MakeColorTargetResource(destination), access, colorLoad, colorStore);
        }

        private static bool TryDescribeAmbientOcclusionColorOutput(
            RenderPassBuilder builder,
            string sourceFboName,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal))
            {
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.AmbientOcclusionRawTextureName), access, colorLoad, colorStore);
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.HBAOPlusRawTextureName), access, colorLoad, colorStore);
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.GTAORawTextureName), access, colorLoad, colorStore);
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal))
            {
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.HBAOPlusBlurIntermediateTextureName), access, colorLoad, colorStore);
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal))
            {
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.GTAOBlurIntermediateTextureName), access, colorLoad, colorStore);
                return true;
            }

            if ((string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) ||
                 string.Equals(sourceFboName, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal) ||
                 string.Equals(sourceFboName, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal)) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            {
                builder.UseColorAttachment(MakeTextureResource(DefaultRenderPipeline.AmbientOcclusionIntensityTextureName), access, colorLoad, colorStore);
                return true;
            }

            return false;
        }

        private static bool TryDescribeActualColorOutputs(
            RenderPassBuilder builder,
            string destination,
            ERenderGraphAccess access,
            ERenderPassLoadOp colorLoad,
            ERenderPassStoreOp colorStore)
        {
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null || !instance.TryGetFBO(destination, out XRFrameBuffer? fbo) || fbo?.Targets is not { Length: > 0 } targets)
                return false;

            bool described = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, attachment, mipLevel, _) = targets[i];
                if (!IsColorAttachment(attachment) || target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                uint mip = mipLevel > 0 ? (uint)mipLevel : 0u;
                if (mip == 0u)
                    builder.UseColorAttachment(MakeTextureResource(texture.Name), access, colorLoad, colorStore);
                else
                    builder.UseColorAttachmentMip(MakeTextureResource(texture.Name), mip, access, colorLoad, colorStore);

                described = true;
            }

            return described;
        }

        private static bool DescribeAmbientOcclusionInputs(RenderPassBuilder builder, string sourceFboName, string destination)
        {
            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.NormalTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.AmbientOcclusionNoiseTextureName));
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.HBAOPlusRawTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.NormalTextureName));
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.GTAORawTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.NormalTextureName));
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.HBAOPlusBlurIntermediateTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.NormalTextureName));
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.GTAOBlurIntermediateTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.DepthViewTextureName));
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.NormalTextureName));
                return true;
            }

            if (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            {
                builder.SampleTexture(MakeTextureResource(DefaultRenderPipeline.AmbientOcclusionRawTextureName));
                return true;
            }

            return IsAmbientOcclusionQuad(sourceFboName, destination) &&
                TryDescribeActualMaterialTextures(builder, sourceFboName);
        }

        private static bool TryDescribeActualMaterialTextures(RenderPassBuilder builder, string sourceFboName)
        {
            XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (instance is null ||
                !instance.TryGetFBO(sourceFboName, out XRFrameBuffer? fbo) ||
                fbo is not XRQuadFrameBuffer quadFbo ||
                quadFbo.Material is not { } material)
                return false;

            bool described = false;
            var textures = material.Textures;
            for (int i = 0; i < textures.Count; i++)
            {
                XRTexture? texture = textures[i];
                if (texture is null || string.IsNullOrWhiteSpace(texture.Name))
                    continue;

                builder.SampleTexture(MakeTextureResource(texture.Name));
                described = true;
            }

            return described;
        }

        private static bool IsAmbientOcclusionQuad(string sourceFboName, string destination)
            => (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.HBAOPlusBlurIntermediateFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.GTAOBlurIntermediateFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.AmbientOcclusionBlurFBOName, StringComparison.Ordinal) &&
                string.Equals(destination, DefaultRenderPipeline.GBufferFBOName, StringComparison.Ordinal));

        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 &&
               attachment <= EFrameBufferAttachment.ColorAttachment31;

        private static void DescribeTextureInputs(RenderPassBuilder builder, IReadOnlyList<string> textureNames)
        {
            foreach (string textureName in textureNames)
                builder.SampleTexture(MakeTextureResource(textureName));
        }

        private static void DescribePostProcessInputs(RenderPassBuilder builder)
        {
            foreach (string textureName in PostProcessTextureInputs)
            {
                if (string.Equals(textureName, DefaultRenderPipeline.BloomBlurTextureName, StringComparison.Ordinal))
                    builder.SampleTextureMips(MakeTextureResource(textureName), 0u, 5u);
                else
                    builder.SampleTexture(MakeTextureResource(textureName));
            }
        }

        private static void DescribeDeferredLightCombineInputs(RenderPassBuilder builder)
        {
            foreach (string textureName in DeferredLightCombineTextureInputs)
                builder.SampleTexture(MakeTextureResource(textureName));

            foreach (string bufferName in DeferredLightCombineBufferInputs)
                builder.ReadBuffer(bufferName);
        }

        private static bool SamplesSharedDepthView(string sourceFboName, string destinationFboName)
            => (string.Equals(sourceFboName, DefaultRenderPipeline.LightCombineFBOName, StringComparison.Ordinal) &&
                string.Equals(destinationFboName, DefaultRenderPipeline.ForwardPassFBOName, StringComparison.Ordinal))
            || (string.Equals(sourceFboName, DefaultRenderPipeline.PostProcessFBOName, StringComparison.Ordinal) &&
                string.Equals(destinationFboName, DefaultRenderPipeline.PostProcessOutputFBOName, StringComparison.Ordinal));
    }
}
