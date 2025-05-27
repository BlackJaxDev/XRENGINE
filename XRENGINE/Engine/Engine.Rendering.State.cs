using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
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
                /// If true, the current render is a light probe pass - only what's needed for light probes is rendered.
                /// All light probe passes contain a scene capture pass.
                /// </summary>
                public static bool IsLightProbePass { get; internal set; }
                /// <summary>
                /// If true, the current render is a scene capture pass - only what's needed for scene captures is rendered.
                /// </summary>
                public static bool IsSceneCapturePass { get; internal set; }
                public static bool ReverseWinding { get; internal set; } = false;
                public static bool ReverseCulling { get; internal set; } = false;

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

                public static void ColorMask(bool red, bool green, bool blue, bool alpha)
                    => AbstractRenderer.Current?.ColorMask(red, green, blue, alpha);
            }
        }
    }
}