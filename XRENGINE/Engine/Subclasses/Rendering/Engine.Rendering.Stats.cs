using System;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Coordinates rendering statistics tracked by feature-specific modules.
            /// </summary>
            public static partial class Stats
            {
                /// <summary>
                /// When false, disables all per-frame statistics tracking to reduce overhead.
                /// VRAM tracking remains enabled as it's not per-frame.
                /// </summary>
                public static bool EnableTracking { get; set; } =
#if XRE_PUBLISHED
                    false;
#else
                    true;
#endif

                /// <summary>
                /// Call this at the start of each frame to publish the previous frame's counters.
                /// </summary>
                public static void BeginFrame()
                {
                    bool gpuPipelineProfilingEnabled = EnableTracking && Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
                    bool gpuTimestampsDenseMode = gpuPipelineProfilingEnabled && IsGpuTimestampDenseModeEnabled();
                    string activeStrategy = CaptureActiveSubmissionStrategy();
                    RendererState.UpdateFrameContext(
                        activeStrategy,
                        CaptureActiveTextureBindingRung(),
                        CaptureActiveStereoMode(),
                        CaptureActiveRenderBackend(),
                        Engine.EffectiveSettings.EnableGpuIndirectValidationLogging || Vulkan.VulkanValidationMessageCountCurrentFrame > 0,
                        IsDebugOutputEnabled(),
                        gpuTimestampsDenseMode);
                    if (gpuTimestampsDenseMode)
                        RendererState.RecordCounter(ERendererProfilerCounter.TimestampDenseModeFrames);
                    RenderPipelineGpuProfiler.Instance.BeginFrame(State.RenderFrameId, gpuPipelineProfilingEnabled);

                    GpuDispatchLogger.BeginFrame();
                    XREngine.Rendering.Occlusion.OcclusionTelemetry.BeginFrame();

                    Frame.SnapshotAndReset();
                    RendererState.SnapshotAndReset();
                    SceneAssets.SnapshotAndReset();
                    GpuDriven.SnapshotAndReset();
                    GpuFallback.SnapshotAndReset();
                    GpuTransparency.SnapshotAndReset();
                    GpuMeshlets.SnapshotAndReset();
                    GpuReadback.SnapshotAndReset();
                    RtxIo.SnapshotAndReset();
                    Vulkan.SnapshotAndReset();
                    Vr.SnapshotAndReset();
                    Vram.SnapshotAndReset();

#if !XRE_PUBLISHED
                    Engine.ProfileCapture.RecordRenderStatsSnapshot();
#endif

                    // Render-matrix, skinned-bounds, and octree stats are swapped separately during SwapBuffers.
                }

                private static string CaptureActiveSubmissionStrategy()
                {
                    try
                    {
                        return Engine.Rendering.ResolveMeshSubmissionStrategy().ToString();
                    }
                    catch
                    {
                        return "unknown";
                    }
                }

                private static string CaptureActiveTextureBindingRung()
                {
                    try
                    {
                        return Engine.EffectiveSettings.ZeroReadbackMaterialDrawPath.ToString();
                    }
                    catch
                    {
                        return "unknown";
                    }
                }

                private static string CaptureActiveStereoMode()
                {
                    if (!Engine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
                        return "mono";

                    if (Engine.Rendering.Settings.RenderVRSinglePassStereo)
                    {
                        if (RuntimeEngine.Rendering.State.HasVulkanMultiView)
                            return "single-pass-vulkan-multiview";
                        if (RuntimeEngine.Rendering.State.HasOvrMultiViewExtension)
                            return "single-pass-opengl-multiview";
                        return "single-pass-requested";
                    }

                    return RuntimeEngine.Rendering.State.IsStereoPass ? "two-pass-stereo" : "vr-desktop-mirror";
                }

                private static string CaptureActiveRenderBackend()
                {
                    if (RuntimeEngine.Rendering.State.IsVulkan)
                        return "Vulkan";
                    if (!string.IsNullOrWhiteSpace(RuntimeEngine.Rendering.State.OpenGLRendererName))
                        return "OpenGL";
                    return "unknown";
                }

                private static bool IsDebugOutputEnabled()
                    => XREngine.Rendering.RenderDiagnosticsFlags.GLDebug ||
                       Engine.EffectiveSettings.EnableGpuIndirectDebugLogging ||
                       Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;

                private static bool IsGpuTimestampDenseModeEnabled()
                {
                    string? value = Environment.GetEnvironmentVariable("XRE_GPU_TIMESTAMP_DENSE");
                    if (string.IsNullOrWhiteSpace(value))
                        return false;

                    value = value.Trim();
                    return value == "1" ||
                           value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                           value.Equals("on", StringComparison.OrdinalIgnoreCase);
                }

                public static class Frame
                {
                    private static int _drawCalls;
                    private static int _trianglesRendered;
                    private static int _multiDrawCalls;
                    private static int _lastFrameDrawCalls;
                    private static int _lastFrameTrianglesRendered;
                    private static int _lastFrameMultiDrawCalls;

                    /// <summary>
                    /// The number of draw calls in the last completed frame.
                    /// </summary>
                    public static int DrawCalls => _lastFrameDrawCalls;

                    /// <summary>
                    /// The number of triangles rendered in the last completed frame.
                    /// </summary>
                    public static int TrianglesRendered => _lastFrameTrianglesRendered;

                    /// <summary>
                    /// The number of multi-draw indirect calls in the last completed frame.
                    /// </summary>
                    public static int MultiDrawCalls => _lastFrameMultiDrawCalls;

                    internal static void SnapshotAndReset()
                    {
                        _lastFrameDrawCalls = Interlocked.Exchange(ref _drawCalls, 0);
                        _lastFrameTrianglesRendered = Interlocked.Exchange(ref _trianglesRendered, 0);
                        _lastFrameMultiDrawCalls = Interlocked.Exchange(ref _multiDrawCalls, 0);
                    }

                    /// <summary>
                    /// Increment the draw call counter.
                    /// </summary>
                    public static void IncrementDrawCalls()
                    {
                        if (!EnableTracking) return;
                        Interlocked.Increment(ref _drawCalls);
                        RendererState.RecordDrawCallsForStrategy(1, RendererState.ActiveSubmissionStrategy);
                    }

                    /// <summary>
                    /// Increment the draw call counter by a specific amount.
                    /// </summary>
                    public static void IncrementDrawCalls(int count)
                    {
                        if (!EnableTracking) return;
                        Interlocked.Add(ref _drawCalls, count);
                        RendererState.RecordDrawCallsForStrategy(count, RendererState.ActiveSubmissionStrategy);
                    }

                    /// <summary>
                    /// Add to the triangles rendered counter.
                    /// </summary>
                    public static void AddTrianglesRendered(int count)
                    {
                        if (!EnableTracking) return;
                        Interlocked.Add(ref _trianglesRendered, count);
                    }

                    /// <summary>
                    /// Increment the multi-draw indirect call counter.
                    /// </summary>
                    public static void IncrementMultiDrawCalls()
                    {
                        if (!EnableTracking) return;
                        Interlocked.Increment(ref _multiDrawCalls);
                    }

                    /// <summary>
                    /// Increment the multi-draw indirect call counter by a specific amount.
                    /// </summary>
                    public static void IncrementMultiDrawCalls(int count)
                    {
                        if (!EnableTracking) return;
                        Interlocked.Add(ref _multiDrawCalls, count);
                    }
                }
            }
        }
    }
}
