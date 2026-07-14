using XREngine.Extensions;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Generates the necessary textures and framebuffers for SSAO in the render pipeline depending on the current render area.
    /// </summary>
    /// <param name="pipeline"></param>
    [RenderPipelineScriptCommand]
    public class VPRC_SSAOPass : ViewportRenderCommand, IDeclaredAoResourceProvider
    {
        //private static void Log(string message)
        //    => Debug.Out(EOutputVerbosity.Normal, false, "[AO][SSAO] {0}", message);

        private static void LogGuardFailure(string location, string reason)
            => Debug.RenderingEvery(
                $"ResilienceGuard.SSAO.{location}",
                TimeSpan.FromSeconds(1),
                "[AO][SSAO][RESILIENCE GUARD TRIGGERED] {0}: {1}",
                location,
                reason);

        private string SSAOBlurShaderName() => 
            Stereo ? "SSAOBlurStereo.fs" : 
            "SSAOBlur.fs";

        private string SSAOGenShaderName() =>
            Stereo ? "SSAOGenStereo.fs" : 
            "SSAOGen.fs";

        public string SSAONoiseTextureName { get; set; } = "AmbientOcclusionNoiseTexture";
        public string SSAORawTextureName { get; set; } = "AmbientOcclusionRawTexture";
        public string SSAOIntensityTextureName { get; set; } = "AmbientOcclusionTexture";
        public string SSAOFBOName { get; set; } = "AmbientOcclusionFBO";
        public string SSAOBlurFBOName { get; set; } = "AmbientOcclusionBlurFBO";
        public string GBufferFBOFBOName { get; set; } = "GBufferFBO";

        public const int DefaultSamples = 128;
        public const uint DefaultNoiseWidth = 4u, DefaultNoiseHeight = 4u;
        public const float DefaultMinSampleDist = 0.1f, DefaultMaxSampleDist = 1.0f;

        public Vector2[]? Noise { get; private set; }
        public Vector3[]? Kernel { get; private set; }

        public int Samples { get; set; } = DefaultSamples;
        public uint NoiseWidth { get; set; } = DefaultNoiseWidth;
        public uint NoiseHeight { get; set; } = DefaultNoiseHeight;
        public float MinSampleDist { get; set; } = DefaultMinSampleDist;
        public float MaxSampleDist { get; set; } = DefaultMaxSampleDist;
        public bool Stereo { get; set; } = false;

        // Per-instance state tracking
        private sealed class InstanceState
        {
            public bool ResourcesDirty = true;
            public int LastWidth;
            public int LastHeight;
            public XRTexture? RawAoTexture;
            public XRTexture? FinalAoTexture;
            public Vector2 NoiseScale;
            public XRTexture? NormalTexture;
            public XRTexture? DepthViewTexture;
            public XRTexture? AlbedoTexture;
            public XRTexture? RmseTexture;
            public XRTexture? TransformIdTexture;
            public XRTexture? DepthStencilTexture;
        }

        private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();

        private InstanceState GetInstanceState(XRRenderPipelineInstance instance)
            => _instanceStates.GetValue(instance, _ => new InstanceState());

        public void GenerateNoiseKernel()
        {
            Random r = new();

            Kernel = new Vector3[Samples];
            Noise = new Vector2[NoiseWidth * NoiseHeight];

            float scale;
            Vector3 sample;

            for (int i = 0; i < Samples; ++i)
            {
                sample = new Vector3(
                    (float)r.NextDouble() * 2.0f - 1.0f,
                    (float)r.NextDouble() * 2.0f - 1.0f,
                    (float)r.NextDouble() + 0.1f).Normalized();
                scale = i / (float)Samples;
                sample *= Interp.Lerp(MinSampleDist, MaxSampleDist, scale * scale);
                Kernel[i] = sample;
            }

            for (int i = 0; i < Noise.Length; ++i)
                Noise[i] = Vector2.Normalize(new Vector2((float)r.NextDouble(), (float)r.NextDouble()));
        }

        public string NormalTextureName { get; set; } = "Normal";
        public string DepthViewTextureName { get; set; } = "DepthView";
        public string AlbedoTextureName { get; set; } = "AlbedoOpacity";
        public string RMSETextureName { get; set; } = "RMSE";
        public string TransformIdTextureName { get; set; } = "TransformId";
        public string DepthStencilTextureName { get; set; } = "DepthStencil";
        public string[] DependentFboNames { get; set; } = Array.Empty<string>();

        public void SetOptions(int samples, uint noiseWidth, uint noiseHeight, float minSampleDist, float maxSampleDist, bool stereo)
        {
            Samples = samples;
            NoiseWidth = noiseWidth;
            NoiseHeight = noiseHeight;
            MinSampleDist = minSampleDist;
            MaxSampleDist = maxSampleDist;
            Stereo = stereo;
        }
        public void SetGBufferInputTextureNames(string normal, string depthView, string albedo, string rmse, string depthStencil, string transformId = "TransformId")
        {
            NormalTextureName = normal;
            DepthViewTextureName = depthView;
            AlbedoTextureName = albedo;
            RMSETextureName = rmse;
            DepthStencilTextureName = depthStencil;
            TransformIdTextureName = transformId;
        }
        public void SetOutputNames(string noise, string intensityTexture, string generationFbo, string blurFbo, string gBufferFBO)
        {
            SSAONoiseTextureName = noise;
            SSAORawTextureName = "AmbientOcclusionRawTexture";
            SSAOIntensityTextureName = intensityTexture;
            SSAOFBOName = generationFbo;
            SSAOBlurFBOName = blurFbo;
            GBufferFBOFBOName = gBufferFBO;
        }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(Execute), "No active pipeline instance; SSAO pass skipped.");
                return;
            }

            var state = GetInstanceState(instance);

            XRTexture? normalTex = instance.GetTexture<XRTexture>(NormalTextureName);
            XRTexture? depthViewTex = instance.GetTexture<XRTexture>(DepthViewTextureName);
            XRTexture? albedoTex = instance.GetTexture<XRTexture>(AlbedoTextureName);
            XRTexture? rmseTex = instance.GetTexture<XRTexture>(RMSETextureName);
            XRTexture? transformIdTex = instance.GetTexture<XRTexture>(TransformIdTextureName);
            XRTexture? depthStencilTex = instance.GetTexture<XRTexture>(DepthStencilTextureName);

            if (normalTex is null ||
                depthViewTex is null ||
                albedoTex is null ||
                rmseTex is null ||
                transformIdTex is null ||
                depthStencilTex is null)
            {
                LogGuardFailure(
                    nameof(Execute),
                    $"Required textures missing (Normal={normalTex is not null}, DepthView={depthViewTex is not null}, Albedo={albedoTex is not null}, RMSE={rmseTex is not null}, TransformId={transformIdTex is not null}, DepthStencil={depthStencilTex is not null}); SSAO pass skipped.");
                return;
            }

            var area = RuntimeEngine.Rendering.State.RenderArea;
            int width = area.Width;
            int height = area.Height;
            bool forceRebuild = state.ResourcesDirty;
            state.ResourcesDirty = false;

            //Log($"Execute start: forceRebuild={forceRebuild}, last={state.LastWidth}x{state.LastHeight}, current={width}x{height}");

            if (!forceRebuild)
            {
                XRTexture? registeredRawAo = instance.GetTexture<XRTexture>(SSAORawTextureName);
                XRTexture? registeredFinalAo = instance.GetTexture<XRTexture>(SSAOIntensityTextureName);
                forceRebuild = state.RawAoTexture is null
                    || state.FinalAoTexture is null
                    || registeredRawAo is null
                    || registeredFinalAo is null
                    || !ReferenceEquals(state.RawAoTexture, registeredRawAo)
                    || !ReferenceEquals(state.FinalAoTexture, registeredFinalAo)
                    || !ReferenceEquals(state.NormalTexture, normalTex)
                    || !ReferenceEquals(state.DepthViewTexture, depthViewTex)
                    || !ReferenceEquals(state.AlbedoTexture, albedoTex)
                    || !ReferenceEquals(state.RmseTexture, rmseTex)
                    || !ReferenceEquals(state.TransformIdTexture, transformIdTex)
                    || !ReferenceEquals(state.DepthStencilTexture, depthStencilTex);
            }

            if (!forceRebuild)
                forceRebuild = !instance.TryGetFBO(SSAOFBOName, out _);

            if (!forceRebuild &&
                width == state.LastWidth && 
                height == state.LastHeight)
            {
                //Log("Skipping regenerate; dimensions unchanged and not forced");
                return;
            }

            /*
            Debug.RenderingEvery(
                $"AO.SSAO.Execute.{RuntimeHelpers.GetHashCode(instance)}",
                TimeSpan.FromSeconds(1),
                "[AO][SSAO] Execute forceRebuild={0} size={1}x{2} stereo={3} normal={4} depth={5} output={6}",
                forceRebuild,
                width,
                height,
                Stereo,
                normalTex.Name ?? "null",
                depthViewTex.Name ?? "null",
                SSAOIntensityTextureName);
            */

            RefreshDeclaredResources(
                instance,
                state,
                normalTex,
                depthViewTex,
                albedoTex,
                rmseTex,
                transformIdTex,
                depthStencilTex,
                width,
                height);
        }

        private void RefreshDeclaredResources(
            XRRenderPipelineInstance instance,
            InstanceState state,
            XRTexture normalTex,
            XRTexture depthViewTex,
            XRTexture albedoTex,
            XRTexture rmseTex,
            XRTexture transformIdTex,
            XRTexture depthStencilTex,
            int width,
            int height)
        {
            //Debug.Out($"SSAO: Regenerating FBOs for {width}x{height}");
            state.LastWidth = width;
            state.LastHeight = height;
            state.NormalTexture = normalTex;
            state.DepthViewTexture = depthViewTex;
            state.AlbedoTexture = albedoTex;
            state.RmseTexture = rmseTex;
            state.TransformIdTexture = transformIdTex;
            state.DepthStencilTexture = depthStencilTex;

            if (Kernel is null || Noise is null)
                GenerateNoiseKernel();

            state.NoiseScale = new Vector2(
                (float)width / NoiseWidth,
                (float)height / NoiseHeight);

            state.RawAoTexture = ResolveDeclaredAoTexture(instance, width, height, SSAORawTextureName, SSAOIntensityTextureName);
            state.FinalAoTexture = ResolveDeclaredAoTexture(instance, width, height, SSAOIntensityTextureName, SSAOIntensityTextureName);

            if (!instance.TryGetFBO(SSAOFBOName, out _) || !instance.TryGetFBO(SSAOBlurFBOName, out _))
                throw new InvalidOperationException("SSAO command requires its declared generation and blur framebuffers.");
        }

        public XRFrameBuffer CreateDeclaredFrameBuffer(XRRenderPipelineInstance instance, string name)
        {
            XRTexture rawAoTexture = RequireTexture(instance, SSAORawTextureName);
            XRTexture finalAoTexture = RequireTexture(instance, SSAOIntensityTextureName);

            if (string.Equals(name, GBufferFBOFBOName, StringComparison.Ordinal))
            {
                return new XRFrameBuffer((RequireAttachment(finalAoTexture, SSAOIntensityTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = GBufferFBOFBOName
                };
            }

            RenderingParameters renderParams = CreateRenderParameters();

            var ssaoGenShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, SSAOGenShaderName()), EShaderType.Fragment);
            var ssaoBlurShader = XRShader.EngineShader(Path.Combine(SceneShaderPath, SSAOBlurShaderName()), EShaderType.Fragment);

            if (string.Equals(name, SSAOFBOName, StringComparison.Ordinal))
            {
                XRMaterial ssaoGenMaterial = new(
                    [
                        RequireTexture(instance, NormalTextureName),
                        CreateDeclaredTexture(instance, SSAONoiseTextureName)
                            ?? throw new InvalidOperationException($"Unable to create SSAO noise texture '{SSAONoiseTextureName}'."),
                        RequireTexture(instance, DepthViewTextureName),
                    ],
                    ssaoGenShader)
                {
                    RenderOptions = renderParams
                };

                XRQuadFrameBuffer ssaoGenFbo = new(ssaoGenMaterial, true,
                    (RequireAttachment(instance, AlbedoTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1),
                    (RequireAttachment(instance, NormalTextureName), EFrameBufferAttachment.ColorAttachment1, 0, -1),
                    (RequireAttachment(instance, RMSETextureName), EFrameBufferAttachment.ColorAttachment2, 0, -1),
                    (RequireAttachment(instance, TransformIdTextureName), EFrameBufferAttachment.ColorAttachment3, 0, -1),
                    (RequireAttachment(instance, DepthStencilTextureName), EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
                {
                    Name = SSAOFBOName
                };
                ssaoGenFbo.SettingUniforms += SSAOGen_SetUniforms;
                return ssaoGenFbo;
            }

            if (string.Equals(name, SSAOBlurFBOName, StringComparison.Ordinal))
            {
                XRMaterial ssaoBlurMaterial = new([rawAoTexture], ssaoBlurShader) { RenderOptions = renderParams };
                XRQuadFrameBuffer ssaoBlurFbo = new(
                    ssaoBlurMaterial,
                    true,
                    (RequireAttachment(rawAoTexture, SSAORawTextureName), EFrameBufferAttachment.ColorAttachment0, 0, -1))
                {
                    Name = SSAOBlurFBOName
                };
                ssaoBlurFbo.SettingUniforms += SSAOBlur_SetUniforms;
                return ssaoBlurFbo;
            }

            throw new InvalidOperationException($"Unsupported SSAO framebuffer '{name}'.");
        }

        public XRTexture? CreateDeclaredTexture(XRRenderPipelineInstance instance, string name)
        {
            if (!string.Equals(name, SSAONoiseTextureName, StringComparison.Ordinal))
                return null;

            if (Kernel is null || Noise is null)
                GenerateNoiseKernel();

            return CreateNoiseTexture();
        }

        private XRTexture ResolveDeclaredAoTexture(
            XRRenderPipelineInstance instance,
            int width,
            int height,
            string textureName,
            string samplerName)
        {
            XRTexture? registeredTexture = instance.GetTexture<XRTexture>(textureName);
            if (registeredTexture is not null && TextureMatchesSize(registeredTexture, width, height))
            {
                ConfigureAoSampler(registeredTexture, samplerName);
                return registeredTexture;
            }

            throw new InvalidOperationException(
                $"Declared SSAO texture '{textureName}' is missing or does not match {width}x{height}.");
        }

        private static RenderingParameters CreateRenderParameters()
            => new()
            {
                DepthTest =
                {
                    Enabled = ERenderParamUsage.Unchanged,
                    UpdateDepth = false,
                    Function = EComparison.Always,
                }
            };

        private static XRTexture RequireTexture(XRRenderPipelineInstance instance, string textureName)
            => instance.GetTexture<XRTexture>(textureName)
                ?? throw new InvalidOperationException($"Missing declared SSAO texture '{textureName}'.");

        private static IFrameBufferAttachement RequireAttachment(XRRenderPipelineInstance instance, string textureName)
            => RequireAttachment(RequireTexture(instance, textureName), textureName);

        private static IFrameBufferAttachement RequireAttachment(XRTexture texture, string textureName)
            => texture as IFrameBufferAttachement
                ?? throw new InvalidOperationException($"Declared SSAO texture '{textureName}' is not framebuffer-attachable.");

        private static bool TextureMatchesSize(XRTexture texture, int width, int height)
        {
            Vector3 dims = texture.WidthHeightDepth;
            return (int)MathF.Round(dims.X) == Math.Max(width, 1) &&
                   (int)MathF.Round(dims.Y) == Math.Max(height, 1);
        }

        private static void ConfigureAoSampler(XRTexture texture, string samplerName)
        {
            texture.SamplerName = samplerName;
        }

        private void SSAOGen_SetUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            if (instance is null)
            {
                LogGuardFailure(nameof(SSAOGen_SetUniforms), "No active pipeline instance while setting uniforms; keeping previous/default uniforms.");
                return;
            }

            var state = GetInstanceState(instance);
            program.Uniform("NoiseScale", state.NoiseScale);
            program.Uniform("Samples", Kernel!);

            var rc = instance.RenderState.SceneCamera
                ?? instance.RenderState.RenderingCamera
                ?? instance.LastSceneCamera
                ?? instance.LastRenderingCamera;
            if (rc is null)
            {
                LogGuardFailure(nameof(SSAOGen_SetUniforms), "No camera available while setting uniforms (Scene/Rendering/Last all null); SSAO camera uniforms not updated.");
                return;
            }
            
            rc.SetUniforms(program);

            if (RuntimeEngine.Rendering.State.IsStereoPass)
                instance.RenderState.StereoRightEyeCamera?.SetUniforms(program, false);

                rc.SetAmbientOcclusionUniforms(
                    program,
                    AmbientOcclusionSettings.EType.ScreenSpace,
                    instance.AssignedPipeline ?? ParentPipeline);

            var region = instance.RenderState.CurrentRenderRegion;
            /*
            Debug.RenderingEvery(
                $"AO.SSAO.GenUniforms.{RuntimeHelpers.GetHashCode(instance)}",
                TimeSpan.FromSeconds(1),
                "[AO][SSAO] Gen depthMode={0} region={1}x{2} stereoPass={3} samples={4}",
                rc.DepthMode,
                region.Width,
                region.Height,
                RuntimeEngine.Rendering.State.IsStereoPass,
                Samples);
            */
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private void SSAOBlur_SetUniforms(XRRenderProgram program)
        {
            var instance = ActivePipelineInstance;
            var region = instance.RenderState.CurrentRenderRegion;
            program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), region.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), region.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToStringFast(), Vector2.Zero);
        }

        private XRTexture CreateAoTexture(int width, int height, string textureName, string samplerName)
        {
            if (Stereo)
            {
                var texture = XRTexture2DArray.CreateFrameBufferTexture(
                    2,
                    (uint)width,
                    (uint)height,
                    EPixelInternalFormat.R16f,
                    EPixelFormat.Red,
                    EPixelType.HalfFloat,
                    EFrameBufferAttachment.ColorAttachment0);
                texture.Resizable = false;
                texture.SizedInternalFormat = ESizedInternalFormat.R16f;
                texture.OVRMultiViewParameters = new(0, 2u);
                texture.Name = textureName;
                texture.SamplerName = samplerName;
                texture.MinFilter = ETexMinFilter.Nearest;
                texture.MagFilter = ETexMagFilter.Nearest;
                texture.UWrap = ETexWrapMode.ClampToEdge;
                texture.VWrap = ETexWrapMode.ClampToEdge;
                return texture;
            }

            var aoTexture = XRTexture2D.CreateFrameBufferTexture(
                (uint)width,
                (uint)height,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                EFrameBufferAttachment.ColorAttachment0);
            aoTexture.Resizable = false;
            aoTexture.SizedInternalFormat = ESizedInternalFormat.R16f;
            aoTexture.Name = textureName;
            aoTexture.SamplerName = samplerName;
            aoTexture.MinFilter = ETexMinFilter.Nearest;
            aoTexture.MagFilter = ETexMagFilter.Nearest;
            aoTexture.UWrap = ETexWrapMode.ClampToEdge;
            aoTexture.VWrap = ETexWrapMode.ClampToEdge;
            return aoTexture;
        }

        private XRTexture2D CreateNoiseTexture()
        {
            if (Kernel is null || Noise is null)
                GenerateNoiseKernel();

            XRTexture2D noiseTexture = new();
            noiseTexture.Name = SSAONoiseTextureName;
            noiseTexture.SamplerName = "AONoiseTexture";
            noiseTexture.MinFilter = ETexMinFilter.Nearest;
            noiseTexture.MagFilter = ETexMagFilter.Nearest;
            noiseTexture.UWrap = ETexWrapMode.Repeat;
            noiseTexture.VWrap = ETexWrapMode.Repeat;
            noiseTexture.Resizable = false;
            noiseTexture.SizedInternalFormat = ESizedInternalFormat.Rg16f;
            noiseTexture.Mipmaps =
            [
                new()
                {
                    Data = DataSource.FromArray(Noise!.SelectMany(v => new float[] { v.X, v.Y }).ToArray()),
                    PixelFormat = EPixelFormat.Rg,
                    PixelType = EPixelType.Float,
                    InternalFormat = EPixelInternalFormat.RG,
                    Width = NoiseWidth,
                    Height = NoiseHeight
                }
            ];
            noiseTexture.PushData();
            return noiseTexture;
        }

        internal override void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            if (instance is null)
            {
                LogGuardFailure(nameof(AllocateContainerResources), "Pipeline instance is null; resources not marked dirty.");
                return;
            }

            GetInstanceState(instance).ResourcesDirty = true;
        }

        internal override void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            if (instance is null)
            {
                LogGuardFailure(nameof(ReleaseContainerResources), "Pipeline instance is null; resources not released.");
                return;
            }

            if (_instanceStates.TryGetValue(instance, out var state))
            {
                state.ResourcesDirty = true;
                state.RawAoTexture = null;
                state.FinalAoTexture = null;
                state.LastWidth = 0;
                state.LastHeight = 0;
            }
        }
    }
}
