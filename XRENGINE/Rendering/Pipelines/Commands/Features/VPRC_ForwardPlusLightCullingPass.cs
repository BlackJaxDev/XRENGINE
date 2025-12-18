using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Scene;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Forward+ tiled light culling pass.
    /// Builds an SSBO of local (point/spot) lights and writes per-tile visible light indices.
    /// OpenGL-first; skipped for stereo and shadow passes.
    /// </summary>
    public class VPRC_ForwardPlusLightCullingPass : ViewportRenderCommand
    {
        public const int TileSize = 16;
        public const int MaxLightsPerTile = 1024;

        // Chosen to avoid conflicts with other SSBO users.
        public const uint LocalLightsBinding = 20;
        public const uint VisibleIndicesBinding = 21;

        public string DepthViewTexture { get; set; } = "DepthView";

        private XRRenderProgram? _computeProgram;

        private XRDataBuffer? _localLightsBuffer;
        private XRDataBuffer? _visibleIndicesBuffer;

        private XRTexture? _depthTextureCache;
        private int _lastTileCountX;
        private int _lastTileCountY;

        private struct ForwardPlusLocalLight
        {
            public Vector4 PositionWS;
            public Vector4 DirectionWS_Exponent;
            public Vector4 Color_Type;
            public Vector4 Params;      // x=radius, y=brightness, z=diffuseIntensity, w=unused
            public Vector4 SpotAngles;  // x=innerCutoff, y=outerCutoff
        }

        private void EnsureComputeProgram()
        {
            if (_computeProgram is not null)
                return;

            XRShader compute = XRShader.EngineShader(Path.Combine(SceneShaderPath, "ForwardPlus", "LightCulling.comp"), EShaderType.Compute);
            _computeProgram = new XRRenderProgram(true, false, compute);
        }

        protected override void Execute()
        {
            if (Engine.Rendering.State.IsShadowPass)
            {
                Engine.Rendering.State.ForwardPlusLocalLightCount = 0;
                return;
            }

            // DepthView is a 2DArray view in stereo; this compute shader uses sampler2D.
            if (Engine.Rendering.State.IsStereoPass)
            {
                Engine.Rendering.State.ForwardPlusLocalLightCount = 0;
                Engine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                return;
            }

            var world = ActivePipelineInstance.RenderState.WindowViewport?.World;
            if (world?.Lights is null)
            {
                Engine.Rendering.State.ForwardPlusLocalLightCount = 0;
                Engine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                return;
            }

            var depthTex = ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture);
            if (depthTex is null)
            {
                Engine.Rendering.State.ForwardPlusLocalLightCount = 0;
                Engine.Rendering.State.ForwardPlusLocalLightsBuffer = null;
                Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer = null;
                return;
            }

            EnsureComputeProgram();

            // Build local light list (point + spot).
            List<ForwardPlusLocalLight> lights = BuildLocalLights(world.Lights);
            int lightCount = lights.Count;

            // Compute tile counts from the active render area.
            // (Texture sizes can be backend-specific due to views/arrays.)
            var area = Engine.Rendering.State.RenderArea;
            int width = Math.Max(1, area.Width);
            int height = Math.Max(1, area.Height);

            int tileCountX = (width + TileSize - 1) / TileSize;
            int tileCountY = (height + TileSize - 1) / TileSize;

            EnsureBuffers(lightCount, tileCountX, tileCountY);

            // Upload local lights (CPU->GPU). Visible-indices buffer is written by compute.
            UploadLocalLights(lights);

            // Bind SSBOs + uniforms and dispatch.
            _localLightsBuffer!.BindTo(_computeProgram!, LocalLightsBinding);
            _visibleIndicesBuffer!.BindTo(_computeProgram!, VisibleIndicesBinding);

            _computeProgram!.Sampler("depthMap", depthTex, 0);

            var cam = Engine.Rendering.State.RenderingCamera;
            Matrix4x4 view = cam?.Transform.InverseRenderMatrix ?? Matrix4x4.Identity;
            Matrix4x4 proj = cam?.ProjectionMatrix ?? Matrix4x4.Identity;

            _computeProgram!.Uniform("view", view);
            _computeProgram!.Uniform("projection", proj);
            _computeProgram!.Uniform("screenSize", new IVector2(width, height));
            _computeProgram!.Uniform("lightCount", lightCount);

            // Dispatch per-tile.
            // The forward pass reads these SSBOs immediately after, so we need a barrier.
            _computeProgram!.DispatchCompute((uint)tileCountX, (uint)tileCountY, 1, EMemoryBarrierMask.ShaderStorage);

            // Publish state so forward materials can bind buffers for shading.
            Engine.Rendering.State.ForwardPlusLocalLightsBuffer = _localLightsBuffer;
            Engine.Rendering.State.ForwardPlusVisibleIndicesBuffer = _visibleIndicesBuffer;
            Engine.Rendering.State.ForwardPlusScreenSize = new Vector2(width, height);
            Engine.Rendering.State.ForwardPlusTileSize = TileSize;
            Engine.Rendering.State.ForwardPlusMaxLightsPerTile = MaxLightsPerTile;
            Engine.Rendering.State.ForwardPlusLocalLightCount = lightCount;

            _depthTextureCache = depthTex;
            _lastTileCountX = tileCountX;
            _lastTileCountY = tileCountY;
        }

        private static List<ForwardPlusLocalLight> BuildLocalLights(Lights3DCollection lights)
        {
            List<ForwardPlusLocalLight> result = new(lights.DynamicPointLights.Count + lights.DynamicSpotLights.Count);

            foreach (PointLightComponent p in lights.DynamicPointLights)
            {
                if (!p.IsActiveInHierarchy)
                    continue;

                result.Add(new ForwardPlusLocalLight
                {
                    PositionWS = new Vector4(p.Transform.RenderTranslation, 1.0f),
                    DirectionWS_Exponent = new Vector4(0, 0, 0, 0),
                    Color_Type = new Vector4(p.Color, 0.0f),
                    Params = new Vector4(p.Radius, p.Brightness, p.DiffuseIntensity, 0.0f),
                    SpotAngles = Vector4.Zero,
                });
            }

            foreach (SpotLightComponent s in lights.DynamicSpotLights)
            {
                if (!s.IsActiveInHierarchy)
                    continue;

                result.Add(new ForwardPlusLocalLight
                {
                    PositionWS = new Vector4(s.Transform.RenderTranslation, 1.0f),
                    DirectionWS_Exponent = new Vector4(Vector3.Normalize(s.Transform.RenderForward), s.Exponent),
                    Color_Type = new Vector4(s.Color, 1.0f),
                    Params = new Vector4(s.Distance, s.Brightness, s.DiffuseIntensity, 0.0f),
                    SpotAngles = new Vector4(s.InnerCutoff, s.OuterCutoff, 0.0f, 0.0f),
                });
            }

            return result;
        }

        private void EnsureBuffers(int lightCount, int tileCountX, int tileCountY)
        {
            uint localLightStride = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ForwardPlusLocalLight>();

            if (_localLightsBuffer is null || _localLightsBuffer.ElementCount != (uint)Math.Max(lightCount, 1))
            {
                _localLightsBuffer = new XRDataBuffer(
                    "ForwardPlusLocalLights",
                    EBufferTarget.ShaderStorageBuffer,
                    (uint)Math.Max(lightCount, 1),
                    EComponentType.Struct,
                    localLightStride,
                    normalize: false,
                    integral: false)
                {
                    Usage = EBufferUsage.StreamDraw,
                    PadEndingToVec4 = true,
                };
                _localLightsBuffer.PushData();
            }

            uint visibleCount = (uint)(tileCountX * tileCountY * MaxLightsPerTile);
            if (_visibleIndicesBuffer is null ||
                _visibleIndicesBuffer.ElementCount != visibleCount ||
                tileCountX != _lastTileCountX ||
                tileCountY != _lastTileCountY)
            {
                _visibleIndicesBuffer = new XRDataBuffer(
                    "ForwardPlusVisibleIndices",
                    EBufferTarget.ShaderStorageBuffer,
                    Math.Max(visibleCount, 1u),
                    EComponentType.Int,
                    1u,
                    normalize: false,
                    integral: true)
                {
                    Usage = EBufferUsage.StreamDraw,
                    PadEndingToVec4 = true,
                };
                _visibleIndicesBuffer.PushData();
            }
        }

        private void UploadLocalLights(List<ForwardPlusLocalLight> lights)
        {
            if (_localLightsBuffer is null)
                return;

            // Ensure at least one element exists (some drivers dislike zero-sized SSBOs).
            if (lights.Count == 0)
            {
                _localLightsBuffer.Set(0, default(ForwardPlusLocalLight));
                _localLightsBuffer.PushSubData();
                return;
            }

            for (uint i = 0; i < (uint)lights.Count; ++i)
                _localLightsBuffer.Set(i, lights[(int)i]);

            _localLightsBuffer.PushSubData();
        }
    }
}
