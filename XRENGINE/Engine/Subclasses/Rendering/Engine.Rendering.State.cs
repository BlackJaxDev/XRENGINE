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
            /// Provides access to the current rendering context including cameras, viewports, pipelines,
            /// and various rendering pass states (shadow, stereo, mirror, etc.).
            /// Also exposes GPU capabilities and low-level rendering operations.
            /// </summary>
            public static partial class State
            {
                /// <summary>
                /// A monotonically increasing counter that increments each render frame.
                /// Used to track frame boundaries, cache invalidation, and temporal effects.
                /// Wraps around on overflow (unchecked increment).
                /// </summary>
                public static ulong RenderFrameId { get; private set; }

                /// <summary>
                /// Thread-local storage for the current render graph pass index.
                /// Each rendering thread can have its own pass index to support parallel rendering.
                /// Initialized to int.MinValue to indicate "no pass active".
                /// </summary>
                [ThreadStatic]
                private static int _currentRenderGraphPassIndex;

                /// <summary>
                /// Static constructor that initializes thread-static fields.
                /// Sets the initial render graph pass index to int.MinValue.
                /// </summary>
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
                /// Uses a StateObject pattern for automatic cleanup via using statements.
                /// When disposed, restores the previous pass index value.
                /// </summary>
                /// <param name="passIndex">The render graph pass index to set for this scope.</param>
                /// <returns>A StateObject that restores the previous pass index when disposed.</returns>
                public static StateObject PushRenderGraphPassIndex(int passIndex)
                {
                    int previous = _currentRenderGraphPassIndex;
                    _currentRenderGraphPassIndex = passIndex;
                    CurrentRenderingPipeline?.RegisterExecutedRenderGraphPass(passIndex);
                    return StateObject.New(() => _currentRenderGraphPassIndex = previous);
                }

                /// <summary>
                /// Called at the start of each render frame to increment the frame counter.
                /// Should be invoked once per frame before any rendering operations begin.
                /// The increment is unchecked to allow graceful wraparound on overflow.
                /// </summary>
                internal static void BeginRenderFrame()
                {
                    unchecked
                    {
                        RenderFrameId++;
                    }
                }

                /// <summary>
                /// Optional override for the rendering camera.
                /// When set, RenderingCamera returns this instead of the pipeline's camera.
                /// Useful for temporarily rendering from a different viewpoint (e.g., debug cameras, mirrors).
                /// </summary>
                public static XRCamera? RenderingCameraOverride { get; set; }

                /// <summary>
                /// The current render area rectangle in pixels.
                /// Returns the pipeline's current render region, or empty if no pipeline is active.
                /// Used to set viewport dimensions and scissor rectangles.
                /// </summary>
                public static BoundingRectangle RenderArea => RenderingPipelineState?.CurrentRenderRegion ?? BoundingRectangle.Empty;

                /// <summary>
                /// The world instance currently being rendered.
                /// Retrieved from the current rendering viewport's World property.
                /// Returns null if no viewport is currently rendering.
                /// </summary>
                public static XRWorldInstance? RenderingWorld => RenderingViewport?.World;

                /// <summary>
                /// The viewport currently being rendered to.
                /// Retrieved from the pipeline state's WindowViewport.
                /// Returns null if no pipeline is currently rendering.
                /// </summary>
                public static XRViewport? RenderingViewport => RenderingPipelineState?.WindowViewport;

                /// <summary>
                /// The visual scene currently being rendered.
                /// Contains the spatial data structures and renderables for the current frame.
                /// Returns null if no pipeline is currently rendering.
                /// </summary>
                public static VisualScene? RenderingScene => RenderingPipelineState?.Scene;

                /// <summary>
                /// The camera currently being used for rendering.
                /// Returns RenderingCameraOverride if set, otherwise the pipeline's rendering camera.
                /// This determines view and projection matrices for the current render pass.
                /// </summary>
                public static XRCamera? RenderingCamera => RenderingCameraOverride ?? RenderingPipelineState?.RenderingCamera;

                /// <summary>
                /// The right eye camera for stereo/VR rendering.
                /// Only valid during stereo render passes (IsStereoPass == true).
                /// Returns null for mono rendering or if no pipeline is active.
                /// </summary>
                public static XRCamera? RenderingStereoRightEyeCamera => RenderingPipelineState?.StereoRightEyeCamera;

                /// <summary>
                /// The target framebuffer for the current render pass output.
                /// This is where the final rendered image will be written.
                /// Returns null if rendering to the default framebuffer (screen) or no pipeline is active.
                /// </summary>
                public static XRFrameBuffer? RenderingTargetOutputFBO => RenderingPipelineState?.OutputFBO;

                /// <summary>
                /// Optional material override for all rendered objects.
                /// When set, all meshes render with this material instead of their assigned materials.
                /// Useful for debug visualization, shadow mapping, or depth-only passes.
                /// </summary>
                public static XRMaterial? OverrideMaterial => RenderingPipelineState?.OverrideMaterial;

                /// <summary>
                /// Stack of active render pipeline instances.
                /// Supports nested rendering scenarios (e.g., main pass rendering a mirror that renders a reflection).
                /// The topmost pipeline is the currently active one.
                /// </summary>
                private static Stack<XRRenderPipelineInstance> RenderingPipelineStack { get; } = new();
                //private static Stack<XRRenderPipelineInstance> CollectingVisiblePipelineStack { get; } = new();

                /// <summary>
                /// Pushes a render pipeline onto the stack, making it the current active pipeline.
                /// Uses a StateObject pattern for automatic cleanup via using statements.
                /// Call this before beginning a render pass with the given pipeline.
                /// </summary>
                /// <param name="pipeline">The render pipeline instance to make active.</param>
                /// <returns>A StateObject that pops the pipeline when disposed.</returns>
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

                /// <summary>
                /// Pops the topmost render pipeline from the stack.
                /// Called automatically when a StateObject from PushRenderingPipeline is disposed.
                /// Safe to call when the stack is empty (no-op).
                /// </summary>
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

                /// <summary>
                /// If true, the OVR_multiview2 extension is available for efficient stereo rendering.
                /// This extension allows rendering both eye views in a single draw call using instancing.
                /// Significantly improves VR performance when available.
                /// </summary>
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
                /// True when Vulkan memory decompression is available and enabled
                /// (e.g., VK_NV_memory_decompression).
                /// </summary>
                public static bool HasVulkanMemoryDecompression { get; internal set; }

                /// <summary>
                /// True when Vulkan indirect GPU copy is available and enabled
                /// (e.g., VK_NV_copy_memory_indirect).
                /// </summary>
                public static bool HasVulkanCopyMemoryIndirect { get; internal set; }

                /// <summary>
                /// True when the Vulkan RTX IO-style path is available for GPU-side decompression.
                /// This currently maps to Vulkan memory decompression availability.
                /// </summary>
                public static bool HasVulkanRtxIo { get; internal set; }

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
                /// <summary>
                /// If true, front/back face winding order is reversed.
                /// Used during mirror/reflection passes where the scene is flipped.
                /// Affects which triangles are considered front-facing.
                /// </summary>
                public static bool ReverseWinding { get; internal set; } = false;

                /// <summary>
                /// If true, culling direction is reversed (cull front instead of back, or vice versa).
                /// Set automatically during mirror passes based on reflection depth.
                /// Works with ReverseWinding to maintain correct visibility in reflected scenes.
                /// </summary>
                public static bool ReverseCulling { get; internal set; } = false;

                /// <summary>
                /// If true, this is the main rendering pass (not a special pass like mirror, capture, or probe).
                /// Main passes render the primary view that the player sees.
                /// Returns true only when IsMirrorPass, IsSceneCapturePass, and IsLightProbePass are all false.
                /// </summary>
                public static bool IsMainPass => !IsMirrorPass && !IsSceneCapturePass && !IsLightProbePass;

                /// <summary>
                /// Stack of transform identifiers for the CPU render path.
                /// Allows nested transforms with automatic restoration via push/pop pattern.
                /// Used when GPU instancing is not available or not suitable.
                /// </summary>
                private static Stack<uint> TransformIdStack { get; } = new();

                /// <summary>
                /// Per-draw transform identifier for the CPU render path.
                /// When present, vertex shaders can use it as a replacement for gl_BaseInstance.
                /// </summary>
                public static uint CurrentTransformId => TransformIdStack.TryPeek(out var id) ? id : 0u;

                /// <summary>
                /// Pushes a transform identifier onto the stack for the current draw call.
                /// The transform ID can be used in shaders as an alternative to gl_BaseInstance.
                /// Uses StateObject pattern for automatic cleanup via using statements.
                /// </summary>
                /// <param name="transformId">The transform identifier to push (typically an index into a transform buffer).</param>
                /// <returns>A StateObject that pops the transform ID when disposed.</returns>
                public static StateObject PushTransformId(uint transformId)
                {
                    TransformIdStack.Push(transformId);
                    return StateObject.New(PopTransformId);
                }

                /// <summary>
                /// Pops the topmost transform identifier from the stack.
                /// Called automatically when a StateObject from PushTransformId is disposed.
                /// Safe to call when the stack is empty (no-op).
                /// </summary>
                public static void PopTransformId()
                {
                    if (TransformIdStack.Count > 0)
                        TransformIdStack.Pop();
                }

                //TODO: Combine forward+ lighting buckets with mirrored render system to know what pixels are displaying mirrored content.

                // ==================== Forward+ (Tiled Light Culling) State ====================
                // These are populated by VPRC_ForwardPlusLightCullingPass each frame when available.
                // Forward+ is a rendering technique that divides the screen into tiles and culls lights
                // per-tile, allowing efficient rendering of scenes with many local lights.

                /// <summary>
                /// GPU buffer containing local light data (position, color, radius, etc.) for Forward+ rendering.
                /// Populated by the Forward+ light culling pass. Null when Forward+ is not active.
                /// </summary>
                public static XRDataBuffer? ForwardPlusLocalLightsBuffer { get; internal set; }

                /// <summary>
                /// GPU buffer containing per-tile visible light indices for Forward+ rendering.
                /// Each tile has a list of indices into ForwardPlusLocalLightsBuffer for lights affecting that tile.
                /// Null when Forward+ is not active.
                /// </summary>
                public static XRDataBuffer? ForwardPlusVisibleIndicesBuffer { get; internal set; }

                /// <summary>
                /// Screen dimensions used for Forward+ tile calculations.
                /// Determines how many tiles exist horizontally and vertically.
                /// </summary>
                public static Vector2 ForwardPlusScreenSize { get; internal set; }

                /// <summary>
                /// The pixel size of each Forward+ tile (e.g., 16x16 or 32x32 pixels).
                /// Smaller tiles = more granular culling but higher overhead.
                /// </summary>
                public static int ForwardPlusTileSize { get; internal set; }

                /// <summary>
                /// Maximum number of lights that can affect a single Forward+ tile.
                /// Lights beyond this limit are ignored for tiles that exceed this count.
                /// </summary>
                public static int ForwardPlusMaxLightsPerTile { get; internal set; }

                /// <summary>
                /// Total number of local (point/spot) lights in the Forward+ system this frame.
                /// Does not include directional lights, which are handled separately.
                /// </summary>
                public static int ForwardPlusLocalLightCount { get; internal set; }

                /// <summary>
                /// Returns true if Forward+ tiled light culling is active and ready for use.
                /// Requires valid light and index buffers and at least one local light.
                /// When true, shaders can use the Forward+ buffers for efficient light iteration.
                /// </summary>
                public static bool ForwardPlusEnabled
                    => ForwardPlusLocalLightsBuffer is not null &&
                       ForwardPlusVisibleIndicesBuffer is not null &&
                       ForwardPlusLocalLightCount > 0;

                //public static XRRenderPipelineInstance? CurrentCollectingVisiblePipeline => CollectingVisiblePipelineStack.Count > 0 ? CollectingVisiblePipelineStack.Peek() : null;
                //public static XRRenderPipelineInstance.RenderingState? CollectingVisiblePipelineState => CurrentCollectingVisiblePipeline?.RenderState;

                /// <summary>
                /// Sets the clear color for subsequent clear operations.
                /// This color is used when clearing color buffers.
                /// </summary>
                /// <param name="color">The RGBA color to clear to.</param>
                public static void ClearColor(ColorF4 color)
                    => AbstractRenderer.Current?.ClearColor(color);

                /// <summary>
                /// Sets the clear value for subsequent stencil buffer clear operations.
                /// </summary>
                /// <param name="v">The stencil value to clear to (typically 0).</param>
                public static void ClearStencil(int v)
                    => AbstractRenderer.Current?.ClearStencil(v);

                /// <summary>
                /// Sets the clear value for subsequent depth buffer clear operations.
                /// </summary>
                /// <param name="v">The depth value to clear to (typically 1.0 for far plane).</param>
                public static void ClearDepth(float v)
                    => AbstractRenderer.Current?.ClearDepth(v);

                /// <summary>
                /// Clears the specified buffers of the currently bound framebuffer.
                /// Combines color, depth, and stencil clearing into a single GPU operation.
                /// </summary>
                /// <param name="color">If true, clears the color buffer(s).</param>
                /// <param name="depth">If true, clears the depth buffer.</param>
                /// <param name="stencil">If true, clears the stencil buffer.</param>
                public static void Clear(bool color, bool depth, bool stencil)
                    => AbstractRenderer.Current?.Clear(color, depth, stencil);

                /// <summary>
                /// Clears buffers based on what the currently bound framebuffer actually has attached.
                /// Automatically determines which buffers exist and clears only those.
                /// Falls back to clearing all specified buffers if no FBO is bound.
                /// </summary>
                /// <param name="color">If true and color attachments exist, clears them.</param>
                /// <param name="depth">If true and depth attachment exists, clears it.</param>
                /// <param name="stencil">If true and stencil attachment exists, clears it.</param>
                public static void ClearByBoundFBO(bool color = true, bool depth = true, bool stencil = true)
                {
                    if (depth)
                        ClearDepth(GetDefaultDepthClearValue());

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

                /// <summary>
                /// Unbinds framebuffers from the specified target, reverting to the default framebuffer.
                /// </summary>
                /// <param name="target">The framebuffer target to unbind (Read, Write, or Both).</param>
                public static void UnbindFrameBuffers(EFramebufferTarget target)
                    => BindFrameBuffer(target, null);

                /// <summary>
                /// Binds a framebuffer to the specified target.
                /// Internal method used by framebuffer management. Pass null to unbind.
                /// </summary>
                /// <param name="fboTarget">The target to bind to (Read, Write, or Both).</param>
                /// <param name="fbo">The framebuffer to bind, or null to unbind.</param>
                private static void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
                    => AbstractRenderer.Current?.BindFrameBuffer(fboTarget, fbo);

                /// <summary>
                /// Sets the read buffer mode for pixel read operations on the default framebuffer.
                /// Determines which color buffer is the source for ReadPixels and similar operations.
                /// </summary>
                /// <param name="mode">The read buffer mode (e.g., Front, Back, None).</param>
                public static void SetReadBuffer(EReadBufferMode mode)
                    => AbstractRenderer.Current?.SetReadBuffer(mode);

                /// <summary>
                /// Sets the read buffer mode for a specific framebuffer.
                /// Allows reading from different color attachments of an FBO.
                /// </summary>
                /// <param name="fbo">The framebuffer to configure, or null for default.</param>
                /// <param name="mode">The read buffer mode specifying which attachment to read from.</param>
                public static void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
                    => AbstractRenderer.Current?.SetReadBuffer(fbo, mode);

                /// <summary>
                /// Synchronously reads the depth buffer value at the specified pixel coordinates.
                /// Causes a GPU pipeline stall - use GetDepthAsync for performance-critical code.
                /// </summary>
                /// <param name="x">The X coordinate in pixels.</param>
                /// <param name="y">The Y coordinate in pixels.</param>
                /// <returns>The depth value at the specified position (0.0 = near, 1.0 = far typically).</returns>
                public static float GetDepth(int x, int y)
                    => AbstractRenderer.Current?.GetDepth(x, y) ?? 0.0f;

                /// <summary>
                /// Asynchronously reads the depth buffer value from a framebuffer at the specified coordinates.
                /// Uses pixel buffer objects to avoid blocking the CPU while waiting for GPU data.
                /// Preferred over GetDepth for performance-sensitive scenarios.
                /// </summary>
                /// <param name="fbo">The framebuffer to read depth from.</param>
                /// <param name="x">The X coordinate in pixels.</param>
                /// <param name="y">The Y coordinate in pixels.</param>
                /// <returns>A task that completes with the depth value.</returns>
                public static unsafe Task<float> GetDepthAsync(XRFrameBuffer fbo, int x, int y)
                {
                    var tcs = new TaskCompletionSource<float>();
                    void callback(float depth)
                        => tcs.SetResult(depth);
                    AbstractRenderer.Current?.GetDepthAsync(fbo, x, y, callback);
                    return tcs.Task;
                }

                /// <summary>
                /// Asynchronously reads a pixel color from the current framebuffer.
                /// Uses pixel buffer objects to avoid blocking the CPU.
                /// </summary>
                /// <param name="x">The X coordinate in pixels.</param>
                /// <param name="y">The Y coordinate in pixels.</param>
                /// <param name="withTransparency">If true, includes alpha channel; if false, alpha is set to 1.</param>
                /// <returns>A task that completes with the pixel color.</returns>
                public static async Task<ColorF4> GetPixelAsync(int x, int y, bool withTransparency)
                {
                    var tcs = new TaskCompletionSource<ColorF4>();
                    void callback(ColorF4 pixel)
                        => tcs.SetResult(pixel);
                    AbstractRenderer.Current?.GetPixelAsync(x, y, withTransparency, callback);
                    return await tcs.Task;
                }

                /// <summary>
                /// Synchronously reads the stencil buffer value at the specified coordinates.
                /// Causes a GPU pipeline stall - consider if stencil reads are truly necessary.
                /// </summary>
                /// <param name="x">The X coordinate in pixels.</param>
                /// <param name="y">The Y coordinate in pixels.</param>
                /// <returns>The stencil value at the specified position (0-255).</returns>
                public static byte GetStencilIndex(float x, float y)
                    => AbstractRenderer.Current?.GetStencilIndex(x, y) ?? 0;

                /// <summary>
                /// Enables or disables depth testing.
                /// When enabled, fragments are discarded if they fail the depth comparison.
                /// </summary>
                /// <param name="enable">True to enable depth testing, false to disable.</param>
                public static void EnableDepthTest(bool enable)
                    => AbstractRenderer.Current?.EnableDepthTest(enable);

                /// <summary>
                /// Sets the stencil buffer write mask.
                /// Controls which bits of the stencil buffer can be written.
                /// </summary>
                /// <param name="mask">Bit mask where 1 = writable, 0 = protected.</param>
                public static void StencilMask(uint mask)
                    => AbstractRenderer.Current?.StencilMask(mask);

                /// <summary>
                /// Enables or disables writing to the depth buffer.
                /// When disabled, depth testing can still occur but values aren't written.
                /// Useful for transparent objects that should test against but not update depth.
                /// </summary>
                /// <param name="allow">True to allow depth writes, false to make depth buffer read-only.</param>
                public static void AllowDepthWrite(bool allow)
                    => AbstractRenderer.Current?.AllowDepthWrite(allow);

                /// <summary>
                /// Sets the comparison function used for depth testing.
                /// Determines how fragment depth is compared against the depth buffer.
                /// </summary>
                /// <param name="always">The comparison function (Less, LessEqual, Greater, Always, etc.).</param>
                public static void DepthFunc(EComparison always)
                    => AbstractRenderer.Current?.DepthFunc(MapDepthComparison(always));

                /// <summary>
                /// Returns the active depth mode for the current render pipeline camera.
                /// Defaults to Normal if no camera is active.
                /// </summary>
                public static XRCamera.EDepthMode GetDepthMode()
                    => RenderingPipelineState?.SceneCamera?.DepthMode ?? XRCamera.EDepthMode.Normal;

                /// <summary>
                /// Returns the default depth clear value for the active camera.
                /// </summary>
                public static float GetDefaultDepthClearValue()
                    => RenderingPipelineState?.SceneCamera?.GetDepthClearValue() ?? 1.0f;

                /// <summary>
                /// Maps a depth comparison function based on the current depth mode.
                /// </summary>
                public static EComparison MapDepthComparison(EComparison comparison)
                {
                    if (GetDepthMode() != XRCamera.EDepthMode.Reversed)
                        return comparison;

                    return comparison switch
                    {
                        EComparison.Less => EComparison.Greater,
                        EComparison.Lequal => EComparison.Gequal,
                        EComparison.Greater => EComparison.Less,
                        EComparison.Gequal => EComparison.Lequal,
                        _ => comparison
                    };
                }

                /// <summary>
                /// Attempts to calculate the dot luminance (average brightness) of a 2D texture.
                /// Dot luminance is computed as dot(rgb, luminanceWeights) and averaged across all pixels.
                /// Used for auto-exposure and tonemapping calculations.
                /// </summary>
                /// <param name="texture">The texture to analyze.</param>
                /// <param name="dotLuminance">Output parameter receiving the calculated luminance.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling for faster averaging.</param>
                /// <returns>True if calculation succeeded, false otherwise.</returns>
                public static bool TryCalculateDotLuminance(XRTexture2D texture, out float dotLuminance, bool generateMipmapsNow)
                {
                    dotLuminance = 1.0f;
                    return AbstractRenderer.Current?.CalcDotLuminance(texture, out dotLuminance, generateMipmapsNow) ?? false;
                }

                /// <summary>
                /// Calculates the dot luminance of a 2D texture, returning a default value on failure.
                /// Convenience wrapper around TryCalculateDotLuminance.
                /// </summary>
                /// <param name="texture">The texture to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <returns>The calculated luminance, or 1.0 if calculation failed.</returns>
                public static float CalculateDotLuminance(XRTexture2D texture, bool generateMipmapsNow)
                    => TryCalculateDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

                /// <summary>
                /// Attempts to calculate the dot luminance of a 2D texture array.
                /// Analyzes all layers and returns an aggregate luminance value.
                /// </summary>
                /// <param name="texture">The texture array to analyze.</param>
                /// <param name="dotLuminance">Output parameter receiving the calculated luminance.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <returns>True if calculation succeeded, false otherwise.</returns>
                public static bool TryCalculateDotLuminance(XRTexture2DArray texture, out float dotLuminance, bool generateMipmapsNow)
                {
                    dotLuminance = 1.0f;
                    return AbstractRenderer.Current?.CalcDotLuminance(texture, out dotLuminance, generateMipmapsNow) ?? false;
                }

                /// <summary>
                /// Calculates the dot luminance of a 2D texture array, returning a default value on failure.
                /// </summary>
                /// <param name="texture">The texture array to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <returns>The calculated luminance, or 1.0 if calculation failed.</returns>
                public static float CalculateDotLuminance(XRTexture2DArray texture, bool generateMipmapsNow)
                    => TryCalculateDotLuminance(texture, out float dotLum, generateMipmapsNow) ? dotLum : 1.0f;

                /// <summary>
                /// Asynchronously calculates the dot luminance of a 2D texture using default luminance weights.
                /// Non-blocking operation - result is returned via callback.
                /// </summary>
                /// <param name="texture">The texture to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);

                /// <summary>
                /// Asynchronously calculates the dot luminance of a 2D texture array using default luminance weights.
                /// Non-blocking operation - result is returned via callback.
                /// </summary>
                /// <param name="texture">The texture array to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, Settings.DefaultLuminance, generateMipmapsNow);

                /// <summary>
                /// Asynchronously calculates the dot luminance of a 2D texture using custom luminance weights.
                /// Allows specifying different RGB weights for luminance calculation.
                /// </summary>
                /// <param name="texture">The texture to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <param name="luminance">RGB weights for luminance calculation (e.g., Rec.709: 0.2126, 0.7152, 0.0722).</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateDotLuminanceAsync(XRTexture2D texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);

                /// <summary>
                /// Asynchronously calculates the dot luminance of a 2D texture array using custom luminance weights.
                /// </summary>
                /// <param name="texture">The texture array to analyze.</param>
                /// <param name="generateMipmapsNow">If true, generates mipmaps before sampling.</param>
                /// <param name="luminance">RGB weights for luminance calculation.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateDotLuminanceAsync(XRTexture2DArray texture, bool generateMipmapsNow, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceAsync(texture, callback, luminance, generateMipmapsNow);

                /// <summary>
                /// Asynchronously calculates the dot luminance of the front buffer (displayed image) using default weights.
                /// Useful for screen-space auto-exposure based on what the player actually sees.
                /// </summary>
                /// <param name="region">The screen region to analyze.</param>
                /// <param name="withTransparency">If true, includes alpha in calculations.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, callback);

                /// <summary>
                /// Asynchronously calculates the dot luminance of the front buffer using custom luminance weights.
                /// </summary>
                /// <param name="region">The screen region to analyze.</param>
                /// <param name="withTransparency">If true, includes alpha in calculations.</param>
                /// <param name="luminance">RGB weights for luminance calculation.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalculateFrontBufferDotLuminanceAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);

                /// <summary>
                /// Calculates front buffer luminance using async compute shaders for maximum performance.
                /// Uses GPU compute for parallel reduction instead of CPU readback.
                /// </summary>
                /// <param name="region">The screen region to analyze.</param>
                /// <param name="withTransparency">If true, includes alpha in calculations.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsyncCompute(region, withTransparency, callback);

                /// <summary>
                /// Calculates front buffer luminance using async compute with custom luminance weights.
                /// </summary>
                /// <param name="region">The screen region to analyze.</param>
                /// <param name="withTransparency">If true, includes alpha in calculations.</param>
                /// <param name="luminance">RGB weights for luminance calculation.</param>
                /// <param name="callback">Callback receiving (success, luminance) when complete.</param>
                public static void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
                    => AbstractRenderer.Current?.CalcDotLuminanceFrontAsyncCompute(region, withTransparency, luminance, callback);

                /// <summary>
                /// Sets the color write mask, controlling which color channels can be written.
                /// Useful for rendering to specific channels only (e.g., alpha-only passes).
                /// </summary>
                /// <param name="red">If true, red channel can be written.</param>
                /// <param name="green">If true, green channel can be written.</param>
                /// <param name="blue">If true, blue channel can be written.</param>
                /// <param name="alpha">If true, alpha channel can be written.</param>
                public static void ColorMask(bool red, bool green, bool blue, bool alpha)
                    => AbstractRenderer.Current?.ColorMask(red, green, blue, alpha);

                /// <summary>
                /// Begins a mirror/reflection render pass.
                /// Increments MirrorPassIndex and sets up culling reversal for odd-numbered passes.
                /// Also enables IsSceneCapturePass since mirrors are a form of scene capture.
                /// Must be paired with PopMirrorPass when the pass completes.
                /// </summary>
                public static void PushMirrorPass()
                {
                    IsSceneCapturePass = true;
                    MirrorPassIndex++;
                    ReverseCulling = IsReflectedMirrorPass;
                }

                /// <summary>
                /// Ends a mirror/reflection render pass.
                /// Decrements MirrorPassIndex and restores culling state.
                /// IsSceneCapturePass is only disabled when exiting all mirror passes.
                /// </summary>
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
