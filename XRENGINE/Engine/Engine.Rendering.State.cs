using System;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;
using XREngine.Scene;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// This static class contains all rendering-related functionality.
        /// </summary>
        public static partial class Rendering
        {
            /// <summary>
            /// This static class dictates the current state of rendering.
            /// </summary>
            public static partial class State
            {
                public static ulong RenderFrameId { get; private set; }

                [ThreadStatic]
                private static int _currentRenderGraphPassIndex;

                static State()
                {
                    _currentRenderGraphPassIndex = int.MinValue;
                }

                /// <summary>
                /// The render-graph pass index currently being executed by the active pipeline.
                /// Vulkan uses this to align per-pass layout transitions and barriers with the pipeline DAG.
                /// </summary>
                public static int CurrentRenderGraphPassIndex => _currentRenderGraphPassIndex;

                /// <summary>
                /// Temporarily sets <see cref="CurrentRenderGraphPassIndex"/> for the duration of a scope.
                /// </summary>
                public static StateObject PushRenderGraphPassIndex(int passIndex)
                {
                    int previous = _currentRenderGraphPassIndex;
                    _currentRenderGraphPassIndex = passIndex;
                    return StateObject.New(() => _currentRenderGraphPassIndex = previous);
                }

                internal static void BeginRenderFrame()
                {
                    unchecked
                    {
                        RenderFrameId++;
                    }
                }

                public static XRCamera? RenderingCameraOverride { get; set; }

                public static BoundingRectangle RenderArea => RenderingPipelineState?.CurrentRenderRegion ?? BoundingRectangle.Empty;
                public static XRWorldInstance? RenderingWorld => RenderingViewport?.World;
                public static XRViewport? RenderingViewport => RenderingPipelineState?.WindowViewport;
                public static VisualScene? RenderingScene => RenderingPipelineState?.Scene;
                public static XRCamera? RenderingCamera => RenderingCameraOverride ?? RenderingPipelineState?.RenderingCamera;
                public static XRCamera? RenderingStereoRightEyeCamera => RenderingPipelineState?.StereoRightEyeCamera;
                public static XRFrameBuffer? RenderingTargetOutputFBO => RenderingPipelineState?.OutputFBO;

                public static XRMaterial? OverrideMaterial => RenderingPipelineState?.OverrideMaterial;

                private static Stack<XRRenderPipelineInstance> RenderingPipelineStack { get; } = new();
                //private static Stack<XRRenderPipelineInstance> CollectingVisiblePipelineStack { get; } = new();

                public static StateObject PushRenderingPipeline(XRRenderPipelineInstance pipeline)
                {
                    RenderingPipelineStack.Push(pipeline);
                    return StateObject.New(PopRenderingPipeline);
                }
                //public static StateObject PushCollectingVisiblePipeline(XRRenderPipelineInstance pipeline)
                //{
                //    CollectingVisiblePipelineStack.Push(pipeline);
                //    return StateObject.New(PopCollectingVisiblePipeline);
                //}

                public static void PopRenderingPipeline()
                {
                    if (RenderingPipelineStack.Count > 0)
                        RenderingPipelineStack.Pop();
                }
                //public static void PopCollectingVisiblePipeline()
                //{
                //    if (CollectingVisiblePipelineStack.Count > 0)
                //        CollectingVisiblePipelineStack.Pop();
                //}

                /// <summary>
                /// This is the render pipeline that's currently rendering a scene.
                /// Use this to retrieve FBOs and textures from the render pipeline.
                /// </summary>
                public static XRRenderPipelineInstance? CurrentRenderingPipeline => RenderingPipelineStack.TryPeek(out var result) ? result : null;

                /// <summary>
                /// Logical resource registry describing textures/FBOs for the active pipeline.
                /// Returns null if no pipeline is currently rendering.
                /// </summary>
                public static RenderResourceRegistry? CurrentResourceRegistry => CurrentRenderingPipeline?.Resources;

                /// <summary>
                /// This is the state of the render pipeline that's currently rendering a scene.
                /// The state contains core information about the rendering process, such as the scene, camera, and viewport.
                /// </summary>
                public static XRRenderPipelineInstance.RenderingState? RenderingPipelineState => CurrentRenderingPipeline?.RenderState;

                /// <summary>
                /// If true, the current render is a shadow pass - only what's needed for shadows is rendered.
                /// </summary>
                public static bool IsShadowPass => RenderingPipelineState?.ShadowPass ?? false;
                /// <summary>
                /// If true, the current render is a stereo pass - all meshes are rendered twice to layers 0 and 1 with either OVR_MultiView2 or a geometry shader.
                /// </summary>
                public static bool IsStereoPass => RenderingPipelineState?.StereoPass ?? false;

                public static bool HasOvrMultiViewExtension { get; internal set; } = false;
                /// <summary>
                /// If true, the shaders required for instanced debug rendering are available.
                /// </summary>
                public static bool DebugInstanceRenderingAvailable { get; internal set; } = true;
                /// <summary>
                /// If true, the current GPU is an NVIDIA GPU.
                /// </summary>
                public static bool IsNVIDIA { get; internal set; }
                /// <summary>
                /// If true, the current GPU is an Intel GPU.
                /// </summary>
                public static bool IsIntel { get; internal set; }
                /// <summary>
                /// If true, the active renderer is using Vulkan. XeSS and DLSS integrations rely on this.
                /// </summary>
                public static bool IsVulkan { get; internal set; }
                /// <summary>
                /// Legacy OpenGL NV ray tracing availability (GL_NV_ray_tracing).
                /// Note: engine ray tracing / DLSS / XeSS paths are Vulkan-focused; this is typically false.
                /// </summary>
                public static bool HasNvRayTracing { get; internal set; }

                /// <summary>
                /// True when the selected Vulkan physical device reports ray tracing pipeline extensions.
                /// </summary>
                public static bool HasVulkanRayTracing { get; internal set; }

                /// <summary>
                /// All OpenGL extensions reported by the current OpenGL context (via GL_NUM_EXTENSIONS + glGetStringi).
                /// Empty when not using OpenGL or if enumeration failed.
                /// </summary>
                public static string[] OpenGLExtensions { get; internal set; } = Array.Empty<string>();
                /// <summary>
                /// If true, the current render is a light probe pass - only what's needed for light probes is rendered.
                /// All light probe passes contain a scene capture pass.
                /// </summary>
                public static bool IsLightProbePass { get; internal set; }
                /// <summary>
                /// If true, the current render is a scene capture pass - only what's needed for scene captures is rendered.
                /// </summary>
                public static bool IsSceneCapturePass { get; internal set; }
                /// <summary>
                /// If this is greater than 0, the current render is a mirror pass.
                /// </summary>
                public static int MirrorPassIndex { get; internal set; } = 0;
                /// <summary>
                /// If true, the current render is a mirror pass - similar to a main pass, but the scene is reflected (or unreflected, depending on the mirror pass index).
                /// </summary>
                public static bool IsMirrorPass => MirrorPassIndex > 0;
                /// <summary>
                /// If the mirror pass is odd, the scene is reflected.
                /// If the mirror pass is even, the scene is a normal unreflected pass.
                /// </summary>
                public static bool IsReflectedMirrorPass => (MirrorPassIndex & 1) == 1;
                public static bool ReverseWinding { get; internal set; } = false;
                public static bool ReverseCulling { get; internal set; } = false;
                public static bool IsMainPass => !IsMirrorPass && !IsSceneCapturePass && !IsLightProbePass;

                private static Stack<uint> TransformIdStack { get; } = new();

                /// <summary>
                /// Per-draw transform identifier for the CPU render path.
                /// When present, vertex shaders can use it as a replacement for gl_BaseInstance.
                /// </summary>
                public static uint CurrentTransformId => TransformIdStack.TryPeek(out var id) ? id : 0u;

                public static StateObject PushTransformId(uint transformId)
                {
                    TransformIdStack.Push(transformId);
                    return StateObject.New(PopTransformId);
                }

                public static void PopTransformId()
                {
                    if (TransformIdStack.Count > 0)
                        TransformIdStack.Pop();
                }

                // Forward+ (tiled light culling) published state.
                // These are populated by VPRC_ForwardPlusLightCullingPass each frame when available.
                public static XRDataBuffer? ForwardPlusLocalLightsBuffer { get; internal set; }
                public static XRDataBuffer? ForwardPlusVisibleIndicesBuffer { get; internal set; }
                public static Vector2 ForwardPlusScreenSize { get; internal set; }
                public static int ForwardPlusTileSize { get; internal set; }
                public static int ForwardPlusMaxLightsPerTile { get; internal set; }
                public static int ForwardPlusLocalLightCount { get; internal set; }
                public static bool ForwardPlusEnabled
                    => ForwardPlusLocalLightsBuffer is not null &&
                       ForwardPlusVisibleIndicesBuffer is not null &&
                       ForwardPlusLocalLightCount > 0;

                //public static XRRenderPipelineInstance? CurrentCollectingVisiblePipeline => CollectingVisiblePipelineStack.Count > 0 ? CollectingVisiblePipelineStack.Peek() : null;
                //public static XRRenderPipelineInstance.RenderingState? CollectingVisiblePipelineState => CurrentCollectingVisiblePipeline?.RenderState;

                public static void ClearColor(ColorF4 color)
                    => AbstractRenderer.Current?.ClearColor(color);
                public static void ClearStencil(int v)
                    => AbstractRenderer.Current?.ClearStencil(v);
                public static void ClearDepth(float v)
                    => AbstractRenderer.Current?.ClearDepth(v);

                public static void Clear(bool color, bool depth, bool stencil)
                    => AbstractRenderer.Current?.Clear(color, depth, stencil);
                public static void ClearByBoundFBO(bool color = true, bool depth = true, bool stencil = true)
                {
                    var boundFBO = XRFrameBuffer.BoundForWriting;
                    if (boundFBO is not null)
                    {
                        var textureTypes = boundFBO.TextureTypes;
                        Clear(
                            textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Color) && color,
                            textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Depth) && depth,
                            textureTypes.HasFlag(EFrameBufferTextureTypeFlags.Stencil) && stencil);
                    }
                    else
                        Clear(color, depth, stencil);
                }

                public static void UnbindFrameBuffers(EFramebufferTarget target)
                    => BindFrameBuffer(target, null);
                private static void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
                    => AbstractRenderer.Current?.BindFrameBuffer(fboTarget, fbo);
                public static void SetReadBuffer(EReadBufferMode mode)
                    => AbstractRenderer.Current?.SetReadBuffer(mode);
                
                public static void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
                    => AbstractRenderer.Current?.SetReadBuffer(fbo, mode);

                public static float GetDepth(int x, int y)
                    => AbstractRenderer.Current?.GetDepth(x, y) ?? 0.0f;
                public static unsafe Task<float> GetDepthAsync(XRFrameBuffer fbo, int x, int y)
                {
                    var tcs = new TaskCompletionSource<float>();
                    void callback(float depth)
                        => tcs.SetResult(depth);
                    AbstractRenderer.Current?.GetDepthAsync(fbo, x, y, callback);
                    return tcs.Task;
                }
                public static unsafe Task<ColorF4> GetPixelAsync(int x, int y, bool withTransparency)
                {
                    var tcs = new TaskCompletionSource<ColorF4>();
                    void callback(ColorF4 pixel)
                        => tcs.SetResult(pixel);
                    AbstractRenderer.Current?.GetPixelAsync(x, y, withTransparency, callback);
                    return tcs.Task;
                }

                public static byte GetStencilIndex(float x, float y)
                    => AbstractRenderer.Current?.GetStencilIndex(x, y) ?? 0;

                public static void EnableDepthTest(bool enable)
                    => AbstractRenderer.Current?.EnableDepthTest(enable);

                public static void StencilMask(uint mask)
                    => AbstractRenderer.Current?.StencilMask(mask);

                public static void AllowDepthWrite(bool allow)
                    => AbstractRenderer.Current?.AllowDepthWrite(allow);

                public static void DepthFunc(EComparison always)
                    => AbstractRenderer.Current?.DepthFunc(always);

                public static bool TryCalculateDotLuminance(XRTexture2D texture, out float dotLuminance, bool generateMipmapsNow)
                {
                    dotLuminance = 1.0f;
                    return AbstractRenderer.Current?.CalcDotLuminance(texture, out dotLuminance, generateMipmapsNow) ?? false;
                }
                public static float CalculateDotLuminance(XRTexture2D texture, bool generateMipmapsNow)
                    => TryCalculateDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

                public static bool TryCalculateDotLuminance(XRTexture2DArray texture, out float dotLuminance, bool generateMipmapsNow)
                {
                    dotLuminance = 1.0f;
                    return AbstractRenderer.Current?.CalcDotLuminance(texture, out dotLuminance, generateMipmapsNow) ?? false;
                }
                public static float CalculateDotLuminance(XRTexture2DArray texture, bool generateMipmapsNow)
                    => TryCalculateDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

                public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);
                public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);

                public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);
                public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);

                public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, callback);
                public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);

                public static void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsyncCompute(region, withTransparency, callback);
                public static void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsyncCompute(region, withTransparency, luminance, callback);

                public static void ColorMask(bool red, bool green, bool blue, bool alpha)
                    => AbstractRenderer.Current?.ColorMask(red, green, blue, alpha);

                public static void PushMirrorPass()
                {
                    IsSceneCapturePass = true;
                    MirrorPassIndex++;
                    ReverseCulling = IsReflectedMirrorPass;
                }

                public static void PopMirrorPass()
                {
                    MirrorPassIndex--;
                    ReverseCulling = IsReflectedMirrorPass;
                    IsSceneCapturePass = IsMirrorPass;
                }
            }
        }
    }
}
