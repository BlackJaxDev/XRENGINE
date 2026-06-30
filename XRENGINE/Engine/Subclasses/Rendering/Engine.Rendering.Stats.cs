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
                public readonly record struct RenderPassCounters(int DrawCalls, int MultiDrawCalls, int TrianglesRendered)
                {
                    public bool HasAny => DrawCalls != 0 || MultiDrawCalls != 0 || TrianglesRendered != 0;

                    public static RenderPassCounters Delta(RenderPassCounters before, RenderPassCounters after)
                        => new(
                            Math.Max(0, after.DrawCalls - before.DrawCalls),
                            Math.Max(0, after.MultiDrawCalls - before.MultiDrawCalls),
                            Math.Max(0, after.TrianglesRendered - before.TrianglesRendered));

                    public static RenderPassCounters SubtractClamped(RenderPassCounters total, RenderPassCounters subtract)
                        => new(
                            Math.Max(0, total.DrawCalls - subtract.DrawCalls),
                            Math.Max(0, total.MultiDrawCalls - subtract.MultiDrawCalls),
                            Math.Max(0, total.TrianglesRendered - subtract.TrianglesRendered));
                }

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
                    bool gpuPipelineProfilingEnabled = EnableTracking && Engine.EditorPreferences.Diagnostics.Profiler.EnableGpuRenderPipelineProfiling;
                    bool gpuTimestampsDenseMode = gpuPipelineProfilingEnabled && IsGpuTimestampDenseModeEnabled();
                    string activeStrategy = CaptureActiveSubmissionStrategy();
                    VrViewRenderModeResolution activeVrViewMode = CaptureActiveVrViewRenderModeResolution();
                    RendererState.UpdateFrameContext(
                        activeStrategy,
                        CaptureActiveTextureBindingRung(),
                        CaptureActiveStereoMode(),
                        activeVrViewMode.RequestedMode.ToString(),
                        activeVrViewMode.EffectiveMode.ToString(),
                        activeVrViewMode.EffectiveImplementationPath.ToString(),
                        activeVrViewMode.TemporalHistoryPolicy.ToString(),
                        CaptureActiveRenderBackend(),
                        RuntimeEngine.Rendering.State.IsVulkan && RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled,
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
                    ResourceChurn.SnapshotAndReset();

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

                    if (Engine.Rendering.Settings.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo)
                    {
                        if (Engine.VRState.IsOpenXRActive && RuntimeEngine.Rendering.State.IsVulkan)
                            return Engine.VRState.OpenXRApi?.CanUseTrueSinglePassStereo == true
                                ? "openxr-true-single-pass-stereo"
                                : "openxr-single-pass-compat-per-eye-swapchains";
                        if (RuntimeEngine.Rendering.State.IsStereoPass && RuntimeEngine.Rendering.State.HasVulkanMultiView)
                            return "single-pass-vulkan-multiview";
                        if (RuntimeEngine.Rendering.State.IsStereoPass && RuntimeEngine.Rendering.State.HasOvrMultiViewExtension)
                            return "single-pass-opengl-multiview";
                        return "single-pass-requested";
                    }

                    return RuntimeEngine.Rendering.State.IsStereoPass ? "two-pass-stereo" : "vr-desktop-mirror";
                }

                private static VrViewRenderModeResolution CaptureActiveVrViewRenderModeResolution()
                {
                    EVrViewRenderMode requestedMode = Engine.Rendering.Settings.VrViewRenderMode;
                    if (!Engine.VRState.IsInVR && !RuntimeEngine.Rendering.State.IsStereoPass)
                    {
                        return new(
                            requestedMode,
                            requestedMode,
                            EVrViewRenderImplementationPath.SequentialViews,
                            EVrTemporalHistoryPolicy.Disabled,
                            true,
                            null);
                    }

                    ERenderLibrary backend = RuntimeEngine.Rendering.State.IsVulkan
                        ? ERenderLibrary.Vulkan
                        : ERenderLibrary.OpenGL;
                    bool trueSinglePassStereoAvailable =
                        requestedMode == EVrViewRenderMode.SinglePassStereo &&
                        (Engine.VRState.IsOpenXRActive
                            ? Engine.VRState.OpenXRApi?.CanUseTrueSinglePassStereo == true
                            : RuntimeEngine.Rendering.State.IsStereoPass &&
                              (RuntimeEngine.Rendering.State.HasVulkanMultiView ||
                               RuntimeEngine.Rendering.State.HasOvrMultiViewExtension));

                    return VrViewRenderModeResolver.Resolve(
                        backend,
                        requestedMode,
                        RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering,
                        trueSinglePassStereoAvailable,
                        rendersExternalSwapchainTargets: Engine.VRState.IsOpenXRActive && !trueSinglePassStereoAvailable);
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
                       Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

                private static bool IsGpuTimestampDenseModeEnabled()
                {
                    string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.GpuTimestampDense);
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

                    public static RenderPassCounters CurrentCounters
                        => new(
                            Volatile.Read(ref _drawCalls),
                            Volatile.Read(ref _multiDrawCalls),
                            Volatile.Read(ref _trianglesRendered));

                    public static RenderPassCounters LastCounters
                        => new(_lastFrameDrawCalls, _lastFrameMultiDrawCalls, _lastFrameTrianglesRendered);

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
