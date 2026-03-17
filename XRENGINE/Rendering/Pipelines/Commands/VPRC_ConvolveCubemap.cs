using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_ConvolveCubemap : ViewportRenderCommand
    {
        private static XRShader? s_fullscreenCubeVertex;
        private static XRShader? s_irradianceFragment;
        private static XRShader? s_prefilterFragment;

        private XRTextureCube? _irradianceTexture;
        private XRTextureCube? _prefilterTexture;
        private XRCubeFrameBuffer? _irradianceFbo;
        private XRCubeFrameBuffer? _prefilterFbo;

        public string SourceCubemapTextureName { get; set; } = "EnvironmentCubemap";
        public string IrradianceTextureName { get; set; } = "IrradianceCubemap";
        public string PrefilterTextureName { get; set; } = "PrefilterCubemap";
        public uint IrradianceResolution { get; set; } = 32;
        public uint PrefilterResolution { get; set; }

        protected override void Execute()
        {
            var instance = ActivePipelineInstance;
            var sourceCubemap = instance.GetTexture<XRTextureCube>(SourceCubemapTextureName)
                ?? throw new InvalidOperationException($"Cubemap texture '{SourceCubemapTextureName}' was not found.");

            EnsureResources(instance, sourceCubemap);

            RenderIrradiance();
            RenderPrefilter(sourceCubemap);

            instance.SetTexture(_irradianceTexture!);
            instance.SetTexture(_prefilterTexture!);
        }

        public void SetOptions(string sourceCubemapTextureName, string irradianceTextureName, string prefilterTextureName)
        {
            SourceCubemapTextureName = sourceCubemapTextureName;
            IrradianceTextureName = irradianceTextureName;
            PrefilterTextureName = prefilterTextureName;
        }

        private void EnsureResources(XRRenderPipelineInstance instance, XRTextureCube sourceCubemap)
        {
            uint irradianceExtent = Math.Max(1u, IrradianceResolution);
            uint prefilterExtent = Math.Max(1u, PrefilterResolution == 0u ? sourceCubemap.Extent : PrefilterResolution);
            int prefilterMipCount = Math.Max(1, XRTexture.GetSmallestMipmapLevel(prefilterExtent, prefilterExtent) + 1);

            bool recreateIrradiance =
                _irradianceTexture is null ||
                _irradianceTexture.Extent != irradianceExtent ||
                _irradianceTexture.Name != IrradianceTextureName;

            if (recreateIrradiance)
            {
                _irradianceFbo?.Destroy();
                _irradianceTexture?.Destroy();

                _irradianceTexture = new XRTextureCube(irradianceExtent, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, false)
                {
                    Name = IrradianceTextureName,
                    MinFilter = ETexMinFilter.Linear,
                    MagFilter = ETexMagFilter.Linear,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Rgb8,
                    AutoGenerateMipmaps = false,
                };

                XRMaterial irradianceMaterial = new([], [sourceCubemap], GetFullscreenCubeVertexShader(), GetIrradianceFragmentShader())
                {
                    RenderOptions = CreateRenderParams(),
                };
                _irradianceFbo = new XRCubeFrameBuffer(irradianceMaterial);
            }
            else if (_irradianceFbo?.Material?.Textures is { Count: > 0 })
            {
                _irradianceFbo.Material.Textures[0] = sourceCubemap;
            }

            bool recreatePrefilter =
                _prefilterTexture is null ||
                _prefilterTexture.Extent != prefilterExtent ||
                _prefilterTexture.Name != PrefilterTextureName ||
                _prefilterTexture.Mipmaps.Length != prefilterMipCount;

            if (recreatePrefilter)
            {
                _prefilterFbo?.Destroy();
                _prefilterTexture?.Destroy();

                _prefilterTexture = new XRTextureCube(prefilterExtent, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false, prefilterMipCount)
                {
                    Name = PrefilterTextureName,
                    MinFilter = ETexMinFilter.LinearMipmapLinear,
                    MagFilter = ETexMagFilter.Linear,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                    AutoGenerateMipmaps = false,
                };

                XRMaterial prefilterMaterial = new(
                    [new ShaderFloat(0.0f, "Roughness"), new ShaderInt((int)Math.Max(1u, sourceCubemap.Extent), "CubemapDim")],
                    [sourceCubemap],
                    GetFullscreenCubeVertexShader(),
                    GetPrefilterFragmentShader())
                {
                    RenderOptions = CreateRenderParams(),
                };
                _prefilterFbo = new XRCubeFrameBuffer(prefilterMaterial);
            }
            else if (_prefilterFbo?.Material?.Textures is { Count: > 0 })
            {
                _prefilterFbo.Material.Textures[0] = sourceCubemap;
                _prefilterFbo.Material.SetInt(1, (int)Math.Max(1u, sourceCubemap.Extent));
            }

            instance.SetTexture(_irradianceTexture!);
            instance.SetTexture(_prefilterTexture!);
        }

        private void RenderIrradiance()
        {
            if (_irradianceTexture is null || _irradianceFbo is null)
                return;

            int extent = Math.Max(1, (int)_irradianceTexture.Extent);
            using var areaScope = ActivePipelineInstance.RenderState.PushRenderArea(extent, extent);

            for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
            {
                _irradianceFbo.SetRenderTargets((_irradianceTexture, EFrameBufferAttachment.ColorAttachment0, 0, faceIndex));
                using var bindScope = _irradianceFbo.BindForWritingState();
                Engine.Rendering.State.ClearByBoundFBO(true, false, false);
                _irradianceFbo.RenderFullscreen((ECubemapFace)faceIndex);
            }
        }

        private void RenderPrefilter(XRTextureCube sourceCubemap)
        {
            if (_prefilterTexture is null || _prefilterFbo is null)
                return;

            int maxMipLevels = _prefilterTexture.Mipmaps.Length;
            int baseExtent = Math.Max(1, (int)_prefilterTexture.Extent);
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipExtent = Math.Max(1, baseExtent >> mip);
                float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);

                _prefilterFbo.Material?.SetFloat(0, roughness);
                _prefilterFbo.Material?.SetInt(1, (int)Math.Max(1u, sourceCubemap.Extent));

                using var areaScope = ActivePipelineInstance.RenderState.PushRenderArea(mipExtent, mipExtent);
                for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
                {
                    _prefilterFbo.SetRenderTargets((_prefilterTexture, EFrameBufferAttachment.ColorAttachment0, mip, faceIndex));
                    using var bindScope = _prefilterFbo.BindForWritingState();
                    Engine.Rendering.State.ClearByBoundFBO(true, false, false);
                    _prefilterFbo.RenderFullscreen((ECubemapFace)faceIndex);
                }
            }
        }

        private static RenderingParameters CreateRenderParams()
        {
            RenderingParameters renderParams = new();
            renderParams.DepthTest.Enabled = ERenderParamUsage.Disabled;
            renderParams.WriteRed = true;
            renderParams.WriteGreen = true;
            renderParams.WriteBlue = true;
            renderParams.WriteAlpha = false;
            return renderParams;
        }

        private static XRShader GetFullscreenCubeVertexShader()
            => s_fullscreenCubeVertex ??= ShaderHelper.LoadEngineShader("Scene3D\\Cubemap.vs", EShaderType.Vertex);

        private static XRShader GetIrradianceFragmentShader()
            => s_irradianceFragment ??= ShaderHelper.LoadEngineShader("Scene3D\\IrradianceConvolution.fs", EShaderType.Fragment);

        private static XRShader GetPrefilterFragmentShader()
            => s_prefilterFragment ??= ShaderHelper.LoadEngineShader("Scene3D\\Prefilter.fs", EShaderType.Fragment);
    }
}