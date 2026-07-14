using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Forward+ tiled light culling pass.
    /// Builds an SSBO of local (point/spot) lights and writes per-tile visible light indices.
    /// OpenGL-first; skipped for stereo and shadow passes.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_ForwardPlusLightCullingPass : ViewportRenderCommand
    {
        private const string MonoShaderPath = "ForwardPlus/LightCulling.comp";
        private const string StereoShaderPath = "ForwardPlus/LightCullingStereo.comp";

        public const int TileSize = 16;
        public const int MaxLightsPerTile = 256;
        public const uint MaxLocalLights = 4096u;
        public const uint LocalLightStride = 96u;

        public const string LocalLightsBufferName = "ForwardPlusLocalLights";
        public const string VisibleIndicesBufferName = "ForwardPlusVisibleIndices";
        public const string TileLightCountsBufferName = "ForwardPlusTileLightCounts";

        // Chosen to avoid conflicts with other SSBO users.
        public const uint LocalLightsBinding = 20;
        public const uint VisibleIndicesBinding = 21;
        // Per-tile light count buffer used by debug visualization.
        public const uint TileLightCountsBinding = 29;

        public string DepthViewTexture { get; set; } = "DepthView";

        private XRRenderProgram? _computeProgram;
        private XRRenderProgram? _computeProgramStereo;

        private XRDataBuffer? _localLightsBuffer;
        private XRDataBuffer? _visibleIndicesBuffer;
        private XRDataBuffer? _tileLightCountsBuffer;

        private struct ForwardPlusLocalLight
        {
            public Vector4 PositionWS;
            public Vector4 DirectionWS_Exponent;
            public Vector4 Color_Type;
            public Vector4 Params;      // x=radius, y=brightness, z=diffuseIntensity, w=legacy source index
            public Vector4 SpotAngles;  // x=innerCutoff, y=outerCutoff
            public IVector4 Indices;    // x=source index, y=shadow record index, z=casts shadows, w=reserved
        }

        private void EnsureComputeProgram()
        {
            if (_computeProgram is not null)
                return;

            XRShader compute = XRShader.EngineShader(Path.Combine(SceneShaderPath, MonoShaderPath), EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, compute);
        }

        private void EnsureStereoComputeProgram()
        {
            if (_computeProgramStereo is not null)
                return;

            XRShader compute = XRShader.EngineShader(Path.Combine(SceneShaderPath, StereoShaderPath), EShaderType.Compute);
            _computeProgramStereo = new XRRenderProgram(true, false, compute);
        }

        protected override void Execute()
        {
            if (RuntimeEngine.Rendering.State.IsShadowPass ||
                DefaultRenderPipeline.UseOpenXrVulkanDesktopStartupSafePath)
            {
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = 0;
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = null;
                return;
            }

            var world = ActivePipelineInstance.RenderState.WindowViewport?.World;
            if (world?.Lights is null)
            {
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = 0;
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = null;
                return;
            }

            var depthTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture);
            if (depthTex is null)
            {
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = 0;
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = null;
                return;
            }

            bool stereo = RuntimeEngine.Rendering.State.IsStereoPass;
            if (stereo)
                EnsureStereoComputeProgram();
            else
                EnsureComputeProgram();

            // Build local light list (point + spot).
            List<ForwardPlusLocalLight> lights = BuildLocalLights(world.Lights);
            int lightCount = Math.Min(lights.Count, checked((int)MaxLocalLights));
            if (lights.Count > lightCount)
            {
                Debug.RenderingEvery(
                    "ForwardPlus.LocalLightOverflow",
                    TimeSpan.FromSeconds(1),
                    "Forward+ local light count {0} exceeds declared capacity {1}; deterministically truncating the uploaded point/spot list.",
                    lights.Count,
                    MaxLocalLights);
            }

            // Compute tile counts from the active render area.
            // (Texture sizes can be backend-specific due to views/arrays.)
            var area = RuntimeEngine.Rendering.State.RenderArea;
            int width = Math.Max(1, area.Width);
            int height = Math.Max(1, area.Height);

            int tileCountX = (width + TileSize - 1) / TileSize;
            int tileCountY = (height + TileSize - 1) / TileSize;

            int passIndex = ResolvePassIndex(nameof(VPRC_ForwardPlusLightCullingPass));
            using IDisposable? renderGraphPassScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : null;

            int viewCount = stereo ? 2 : 1;
            if (!RefreshDeclaredBuffers(tileCountX, tileCountY, viewCount))
            {
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = 0;
                RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = null;
                return;
            }

            // Upload local lights (CPU->GPU). Visible-indices buffer is written by compute.
            UploadLocalLights(lights, lightCount);

            // Bind SSBOs + uniforms and dispatch.
            var cam = RuntimeEngine.Rendering.State.RenderingCamera;
            Matrix4x4 view = cam?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 proj = cam?.ProjectionMatrix ?? Matrix4x4.Identity;
            float cameraNear = cam?.Parameters.NearZ ?? 0.1f;
            float cameraFar = cam?.Parameters.FarZ ?? 10000f;
            if (stereo)
            {
                if (depthTex is not XRTexture2DArray depthArray)
                {
                    RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = 0;
                    RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                    RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                    RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = null;
                    return;
                }

                var rightEyeCamera = ActivePipelineInstance.RenderState.StereoRightEyeCamera;
                Matrix4x4 rightView = rightEyeCamera?.Transform.InverseRenderMatrix ?? view;
                Matrix4x4 rightProj = rightEyeCamera?.ProjectionMatrix ?? proj;

                _localLightsBuffer!.BindTo(_computeProgramStereo!, LocalLightsBinding);
                _visibleIndicesBuffer!.BindTo(_computeProgramStereo!, VisibleIndicesBinding);
                _tileLightCountsBuffer!.BindTo(_computeProgramStereo!, TileLightCountsBinding);
                _computeProgramStereo!.Sampler("depthMap", depthArray, 0);
                _computeProgramStereo!.Uniform("screenSize", new IVector2(width, height));
                _computeProgramStereo!.Uniform("lightCount", lightCount);
                _computeProgramStereo!.Uniform("DepthMode", (int)(cam?.DepthMode ?? XRCamera.EDepthMode.Normal));
                _computeProgramStereo!.Uniform("cameraNear", cameraNear);
                _computeProgramStereo!.Uniform("cameraFar", cameraFar);

                _computeProgramStereo!.Uniform("view", view);
                _computeProgramStereo!.Uniform("projection", proj);
                _computeProgramStereo!.Uniform("EyeIndex", 0);
                _computeProgramStereo!.Uniform("EyeTileOffset", 0);
                _computeProgramStereo!.DispatchCompute((uint)tileCountX, (uint)tileCountY, 1, EMemoryBarrierMask.ShaderStorage);

                _computeProgramStereo!.Uniform("view", rightView);
                _computeProgramStereo!.Uniform("projection", rightProj);
                _computeProgramStereo!.Uniform("EyeIndex", 1);
                _computeProgramStereo!.Uniform("EyeTileOffset", tileCountX * tileCountY);
                _computeProgramStereo!.DispatchCompute((uint)tileCountX, (uint)tileCountY, 1, EMemoryBarrierMask.ShaderStorage);
            }
            else
            {
                _localLightsBuffer!.BindTo(_computeProgram!, LocalLightsBinding);
                _visibleIndicesBuffer!.BindTo(_computeProgram!, VisibleIndicesBinding);
                _tileLightCountsBuffer!.BindTo(_computeProgram!, TileLightCountsBinding);
                _computeProgram!.Sampler("depthMap", depthTex, 0);
                _computeProgram!.Uniform("view", view);
                _computeProgram!.Uniform("projection", proj);
                _computeProgram!.Uniform("screenSize", new IVector2(width, height));
                _computeProgram!.Uniform("lightCount", lightCount);
                _computeProgram!.Uniform("DepthMode", (int)(cam?.DepthMode ?? XRCamera.EDepthMode.Normal));
                _computeProgram!.Uniform("cameraNear", cameraNear);
                _computeProgram!.Uniform("cameraFar", cameraFar);
                _computeProgram!.DispatchCompute((uint)tileCountX, (uint)tileCountY, 1, EMemoryBarrierMask.ShaderStorage);
            }

            // Publish state so forward materials can bind buffers for shading.
            RuntimeEngine.Rendering.State.ForwardPlusLocalLightsBuffer = _localLightsBuffer;
            RuntimeEngine.Rendering.State.ForwardPlusVisibleIndicesBuffer = _visibleIndicesBuffer;
            RuntimeEngine.Rendering.State.ForwardPlusTileLightCountsBuffer = _tileLightCountsBuffer;
            RuntimeEngine.Rendering.State.ForwardPlusScreenSize = new Vector2(width, height);
            RuntimeEngine.Rendering.State.ForwardPlusTileSize = TileSize;
            RuntimeEngine.Rendering.State.ForwardPlusTileCountX = tileCountX;
            RuntimeEngine.Rendering.State.ForwardPlusTileCountY = tileCountY;
            RuntimeEngine.Rendering.State.ForwardPlusMaxLightsPerTile = MaxLightsPerTile;
            RuntimeEngine.Rendering.State.ForwardPlusLocalLightCount = lightCount;

        }

        private static List<ForwardPlusLocalLight> BuildLocalLights(Lights3DCollection lights)
        {
            List<ForwardPlusLocalLight> result = new(lights.DynamicPointLights.Count + lights.DynamicSpotLights.Count);

            for (int pointIndex = 0; pointIndex < lights.DynamicPointLights.Count; ++pointIndex)
            {
                PointLightComponent p = lights.DynamicPointLights[pointIndex];
                if (!p.IsActiveInHierarchy)
                    continue;

                int shadowRecordIndex = -1;
                if (p.UsesPointShadowAtlasForCurrentEncoding)
                    lights.TryGetPointShadowAtlasFaceAllocation(p, 0, out _, out shadowRecordIndex);

                result.Add(new ForwardPlusLocalLight
                {
                    PositionWS = new Vector4(p.Transform.RenderTranslation, 1.0f),
                    DirectionWS_Exponent = new Vector4(0, 0, 0, 0),
                    Color_Type = new Vector4(p.Color, 0.0f),
                    Params = new Vector4(p.Radius, p.Brightness, p.DiffuseIntensity, pointIndex),
                    SpotAngles = Vector4.Zero,
                    Indices = new IVector4(pointIndex, shadowRecordIndex, p.CastsShadows ? 1 : 0, 0),
                });
            }

            for (int spotIndex = 0; spotIndex < lights.DynamicSpotLights.Count; ++spotIndex)
            {
                SpotLightComponent s = lights.DynamicSpotLights[spotIndex];
                if (!s.IsActiveInHierarchy)
                    continue;

                int shadowRecordIndex = -1;
                if (RuntimeEngine.Rendering.Settings.UseSpotShadowAtlas && s.UsesSpotShadowAtlasForCurrentEncoding)
                    lights.TryGetSpotShadowAtlasAllocation(s, out _, out shadowRecordIndex);

                result.Add(new ForwardPlusLocalLight
                {
                    PositionWS = new Vector4(s.Transform.RenderTranslation, 1.0f),
                    DirectionWS_Exponent = new Vector4(Vector3.Normalize(s.Transform.RenderForward), s.Exponent),
                    Color_Type = new Vector4(s.Color, 1.0f),
                    Params = new Vector4(s.Distance, s.Brightness, s.DiffuseIntensity, spotIndex),
                    SpotAngles = new Vector4(s.InnerCutoff, s.OuterCutoff, 0.0f, 0.0f),
                    Indices = new IVector4(spotIndex, shadowRecordIndex, s.CastsShadows ? 1 : 0, 0),
                });
            }

            return result;
        }

        private bool RefreshDeclaredBuffers(int tileCountX, int tileCountY, int viewCount)
        {
            _localLightsBuffer = ActivePipelineInstance.GetBuffer(LocalLightsBufferName);
            _visibleIndicesBuffer = ActivePipelineInstance.GetBuffer(VisibleIndicesBufferName);
            _tileLightCountsBuffer = ActivePipelineInstance.GetBuffer(TileLightCountsBufferName);

            uint requiredVisibleCount = ComputeForwardPlusElementCount(tileCountX, tileCountY, viewCount, MaxLightsPerTile);
            uint requiredTileCount = ComputeForwardPlusElementCount(tileCountX, tileCountY, viewCount, 1);
            if (_localLightsBuffer is null ||
                _visibleIndicesBuffer is null ||
                _tileLightCountsBuffer is null ||
                _localLightsBuffer.ElementCount < MaxLocalLights ||
                _visibleIndicesBuffer.ElementCount < requiredVisibleCount ||
                _tileLightCountsBuffer.ElementCount < requiredTileCount)
            {
                Debug.RenderingEvery(
                    "ForwardPlus.DeclaredBufferMismatch",
                    TimeSpan.FromSeconds(1),
                    "Forward+ declared buffers are missing or undersized for tiles={0}x{1}, views={2}; culling skipped.",
                    tileCountX,
                    tileCountY,
                    viewCount);
                return false;
            }

            return true;
        }

        public static uint ComputeForwardPlusElementCount(int tileCountX, int tileCountY, int viewCount, int elementsPerTile)
        {
            ulong x = (ulong)Math.Max(tileCountX, 1);
            ulong y = (ulong)Math.Max(tileCountY, 1);
            ulong views = (ulong)Math.Max(viewCount, 1);
            ulong perTile = (ulong)Math.Max(elementsPerTile, 1);
            ulong count = x * y * views * perTile;
            if (count > uint.MaxValue)
                throw new InvalidOperationException($"Forward+ buffer capacity exceeds uint range: tiles={tileCountX}x{tileCountY}, views={viewCount}, elementsPerTile={elementsPerTile}.");

            return Math.Max((uint)count, 1u);
        }

        public static XRDataBuffer CreateDeclaredLocalLightsBuffer()
            => CreateDeclaredBuffer(LocalLightsBufferName, MaxLocalLights, EComponentType.Struct, LocalLightStride, EBufferUsage.StreamDraw, integral: false);

        public static XRDataBuffer CreateDeclaredVisibleIndicesBuffer(uint elementCount)
            => CreateDeclaredBuffer(VisibleIndicesBufferName, elementCount, EComponentType.Int, 1u, EBufferUsage.StaticCopy, integral: true);

        public static XRDataBuffer CreateDeclaredTileLightCountsBuffer(uint elementCount)
            => CreateDeclaredBuffer(TileLightCountsBufferName, elementCount, EComponentType.UInt, 1u, EBufferUsage.StaticCopy, integral: true);

        private static XRDataBuffer CreateDeclaredBuffer(string name, uint elementCount, EComponentType componentType, uint componentCount, EBufferUsage usage, bool integral)
        {
            var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, componentType, componentCount, normalize: false, integral: integral)
            {
                Usage = usage,
                PadEndingToVec4 = true,
            };
            buffer.PushData();
            return buffer;
        }

        private void UploadLocalLights(List<ForwardPlusLocalLight> lights, int lightCount)
        {
            if (_localLightsBuffer is null)
                return;

            // Ensure at least one element exists (some drivers dislike zero-sized SSBOs).
            if (lightCount == 0)
            {
                _localLightsBuffer.Set(0, default(ForwardPlusLocalLight));
                _localLightsBuffer.PushSubData(0, _localLightsBuffer.ElementSize);
                return;
            }

            for (uint i = 0; i < (uint)lightCount; ++i)
                _localLightsBuffer.Set(i, lights[(int)i]);

            uint uploadBytes = (uint)lightCount * _localLightsBuffer.ElementSize;
            _localLightsBuffer.PushSubData(0, uploadBytes);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_ForwardPlusLightCullingPass), ERenderGraphPassStage.Compute);
            builder.SampleTexture(MakeTextureResource(DepthViewTexture));
            builder.ReadWriteBuffer(LocalLightsBufferName);
            builder.ReadWriteBuffer(VisibleIndicesBufferName);
            builder.ReadWriteBuffer(TileLightCountsBufferName);
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            foreach (RenderPassMetadata pass in metadata)
            {
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return pass.PassIndex;
            }

            return int.MinValue;
        }
    }
}
