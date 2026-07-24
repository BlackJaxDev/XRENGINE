using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Frequently read render-frame state and timing. Implementations must make property access allocation-free.
/// </summary>
public interface IRuntimeRenderFrameTimingServices
{

    /// <summary>
    /// Gets whether the current caller is executing on the host render thread.
    /// </summary>
    bool IsRenderThread { get; }

    /// <summary>
    /// Gets whether a backend renderer is currently active and able to accept GPU work.
    /// </summary>
    bool IsRendererActive { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside a shadow-map pass.
    /// </summary>
    bool IsShadowPass { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside a stereo render pass.
    /// </summary>
    bool IsStereoPass { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside an off-screen scene capture pass.
    /// </summary>
    bool IsSceneCapturePass { get; }

    /// <summary>
    /// Gets whether debug culling volumes should be rendered for visible render-info objects.
    /// </summary>
    bool RenderCullingVolumesEnabled { get; }

    /// <summary>
    /// Gets whether the active renderer or GPU state is known to be NVIDIA-specific.
    /// </summary>
    bool IsNvidia { get; }

    /// <summary>
    /// Gets the luminance weighting vector used by post-processing and texture luminance calculations.
    /// </summary>
    Vector3 DefaultLuminance { get; }

    /// <summary>
    /// Gets host elapsed time in stopwatch ticks.
    /// </summary>
    long ElapsedTicks { get; }

    /// <summary>
    /// Gets host elapsed time in seconds.
    /// </summary>
    float ElapsedTime { get; }

    /// <summary>
    /// Gets whether the caller is currently running on the host application thread.
    /// </summary>
    bool IsAppThread { get; }

    /// <summary>
    /// Gets whether the host is still executing startup work.
    /// </summary>
    bool IsStartingUp { get; }

    /// <summary>
    /// Gets the most recent update-frame delta in seconds.
    /// </summary>
    double UpdateDeltaSeconds { get; }

    /// <summary>
    /// Gets the smoothed, time-dilated update delta in seconds.
    /// </summary>
    double SmoothedUpdateDeltaSeconds { get; }

    /// <summary>
    /// Gets the host timestamp for the most recent update frame.
    /// </summary>
    long LastUpdateTimestampTicks { get; }

    /// <summary>
    /// Gets the most recent render-frame delta in seconds.
    /// </summary>
    double RenderDeltaSeconds { get; }

    /// <summary>
    /// Gets the host timestamp for the most recent render frame.
    /// </summary>
    long LastRenderTimestampTicks { get; }

    string DefaultFontFolder { get; }
    string DefaultFontFileName { get; }
    bool RenderMesh2DBounds { get; }
    bool RenderUITransformCoordinate { get; }
    ColorF4 Bounds2DColor { get; }

    /// <summary>
    /// Gets the renderer's currently tracked VRAM allocation total in bytes.
    /// </summary>
    long TrackedVramBytes { get; }

    /// <summary>
    /// Gets the renderer's current VRAM allocation budget in bytes.
    /// </summary>
    long TrackedVramBudgetBytes { get; }

    /// <summary>
    /// Gets whether GPU indirect rendering debug logging should be emitted.
    /// </summary>
    bool EnableGpuIndirectDebugLogging { get; }

    /// <summary>
    /// Gets the active occlusion culling mode resolved by the host.
    /// </summary>
    EOcclusionCullingMode GpuOcclusionCullingMode { get; }

    /// <summary>
    /// Gets the frame period for periodically retesting meshes culled by CPU hardware queries.
    /// </summary>
    int CpuQueryOcclusionRetestPeriodFrames { get; }

    /// <summary>
    /// Gets the per-pass/view cap on unresolved CPU hardware queries plus current-frame reservations.
    /// </summary>
    int CpuQueryOcclusionMaxQueriesPerFrame { get; }

    /// <summary>
    /// Gets the fraction of the CPU query budget reserved for visible-demotion probes.
    /// </summary>
    float CpuQueryOcclusionVisibleDemotionBudgetFraction { get; }

    /// <summary>
    /// Gets the minimum frame cadence for recovery probes on predicted-occluded commands.
    /// </summary>
    int CpuQueryOcclusionRecoveryMinCadenceFrames { get; }

    float CpuQueryOcclusionSmallMotionMeters { get; }
    float CpuQueryOcclusionMediumMotionMeters { get; }
    float CpuQueryOcclusionLargeMotionMeters { get; }
    float CpuQueryOcclusionCameraCutMeters { get; }
    float CpuQueryOcclusionSmallRotationDegrees { get; }
    float CpuQueryOcclusionMediumRotationDegrees { get; }
    float CpuQueryOcclusionLargeRotationDegrees { get; }
    float CpuQueryOcclusionCameraCutRotationDegrees { get; }
    float CpuQueryOcclusionVrHeadMotionMeters { get; }
    float CpuQueryOcclusionVrHeadRotationDegrees { get; }
    ECpuQueryStereoMode CpuQueryOcclusionStereoMode { get; }
    int CpuQueryOcclusionMaxPendingFrames { get; }

    /// <summary>
    /// Gets whether the legacy CPU software occlusion culling side toggle is enabled.
    /// </summary>
    bool EnableCpuSoftwareOcclusionCulling { get; }

    /// <summary>
    /// Gets the CPU software occlusion buffer width in pixels.
    /// </summary>
    int CpuSocBufferWidth { get; }

    /// <summary>
    /// Gets the CPU software occlusion buffer height in pixels.
    /// </summary>
    int CpuSocBufferHeight { get; }

    /// <summary>
    /// Gets the triangle budget for CPU software occlusion rasterization.
    /// </summary>
    int CpuSocOccluderTriangleBudget { get; }

    /// <summary>
    /// Gets the maximum number of CPU software occlusion occluders.
    /// </summary>
    int CpuSocMaxOccluders { get; }

    /// <summary>
    /// Gets the minimum screen-area fraction for CPU software occlusion occluders.
    /// </summary>
    float CpuSocMinOccluderScreenArea { get; }

    /// <summary>
    /// Gets whether AVX2 acceleration is allowed for CPU software occlusion.
    /// </summary>
    bool CpuSocUseAvx2 { get; }

    /// <summary>
    /// Gets whether CPU software occlusion debug visualization is enabled.
    /// </summary>
    bool CpuSocDebugVisualization { get; }

    /// <summary>
    /// Gets whether CPU software occlusion should force every test visible for diagnostics.
    /// </summary>
    bool CpuSocDebugForceVisible { get; }

    /// <summary>
    /// Gets the CPU spatial structure used for render visibility when GPU dispatch is disabled.
    /// </summary>
    ECpuSceneCullingStructure CpuSceneCullingStructure { get; }

    /// <summary>
    /// Gets the host preference for splitting a window between two local players.
    /// </summary>
    ETwoPlayerPreference TwoPlayerViewportPreference { get; }

    /// <summary>
    /// Gets the host preference for splitting a window between three local players.
    /// </summary>
    EThreePlayerPreference ThreePlayerViewportPreference { get; }

    /// <summary>
    /// Gets the graphics API used by the current or primary renderer.
    /// </summary>
    RuntimeGraphicsApiKind CurrentRenderBackend { get; }

    /// <summary>
    /// Gets the renderer currently executing commands, or the primary window renderer when no command scope owns the static current renderer.
    /// </summary>
    IRuntimeRendererHost? CurrentRenderer { get; }

    /// <summary>
    /// Gets the render-command state currently active on the host, when any command pass is executing.
    /// </summary>
    IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState { get; }

    /// <summary>
    /// Gets the render pipeline context currently active on the host, when any pipeline frame is executing.
    /// </summary>
    IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext { get; }

    /// <summary>
    /// Gets whether the host is entering or leaving play mode and rendering should avoid transient state changes.
    /// </summary>
    bool IsPlayModeTransitioning { get; }

    /// <summary>
    /// Gets the host play-mode state name for diagnostics and frame labels.
    /// </summary>
    string PlayModeStateName { get; }

    /// <summary>
    /// Gets the default anti-aliasing mode used when no camera or pipeline override is active.
    /// </summary>
    EAntiAliasingMode DefaultAntiAliasingMode { get; }

    /// <summary>
    /// Gets whether NVIDIA DLSS super resolution is enabled by the effective host settings.
    /// </summary>
    bool EnableNvidiaDlss { get; }

    /// <summary>
    /// Gets the effective NVIDIA DLSS quality mode.
    /// </summary>
    EDlssQualityMode DlssQuality { get; }

    /// <summary>
    /// Gets the effective custom NVIDIA DLSS input-resolution scale.
    /// </summary>
    float DlssCustomScale { get; }

    /// <summary>
    /// Gets the effective NVIDIA DLSS sharpening amount.
    /// </summary>
    float DlssSharpness { get; }

    /// <summary>
    /// Gets whether NVIDIA DLSS frame generation is enabled by the effective host settings.
    /// </summary>
    bool EnableNvidiaDlssFrameGeneration { get; }

    /// <summary>
    /// Gets the effective NVIDIA DLSS frame-generation multiplier.
    /// </summary>
    ENvidiaDlssFrameGenerationMode NvidiaDlssFrameGenerationMode { get; }

    /// <summary>
    /// Gets the default MSAA sample count used when no camera or pipeline override is active.
    /// </summary>
    uint DefaultMsaaSampleCount { get; }

    /// <summary>
    /// Gets whether render outputs should default to HDR when no camera or pipeline override is active.
    /// </summary>
    bool DefaultOutputHDR { get; }

    /// <summary>
    /// Gets the default temporal super-resolution render scale used when no camera override is active.
    /// </summary>
    float DefaultTsrRenderScale { get; }

    /// <summary>
    /// Gets whether the host has per-frame render statistics tracking enabled.
    /// When false, render-stats sampling and the GPU pipeline profiler are skipped.
    /// </summary>
    bool EnableRenderStatisticsTracking { get; }

    /// <summary>
    /// Gets whether the host has GPU render-pipeline command timing enabled.
    /// When false, the render-pipeline GPU profiler short-circuits scope creation.
    /// </summary>
    bool EnableGpuRenderPipelineProfiling { get; }

    /// <summary>
    /// Gets whether a resolved GPU render-pipeline frame timing is available.
    /// </summary>
    bool GpuRenderPipelineTimingsReady => RuntimeRenderingHostServiceDefaults.GpuRenderPipelineTimingsReady;

    /// <summary>
    /// Gets the latest resolved GPU render-pipeline frame time in milliseconds.
    /// </summary>
    double GpuRenderPipelineFrameMs => RuntimeRenderingHostServiceDefaults.GpuRenderPipelineFrameMs;

    /// <summary>
    /// Gets the host's current render frame id. Used by runtime rendering code that
    /// needs to correlate work to the same frame counter the host increments per frame.
    /// </summary>
    ulong CurrentRenderFrameId { get; }
}
