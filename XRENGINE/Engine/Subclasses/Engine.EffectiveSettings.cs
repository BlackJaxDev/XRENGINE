using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.Vulkan;

namespace XREngine
{
    public static partial class Engine
    {
        /// <summary>
        /// Provides resolved effective settings values using the cascading override system.
        /// Resolution order: User Settings > Project Settings > Engine Defaults
        /// </summary>
        /// <remarks>
        /// This class resolves settings from three levels:
        /// <list type="number">
        ///     <item><description>User Settings - End-user preferences (highest priority)</description></item>
        ///     <item><description>Project Settings - Game/project configuration</description></item>
        ///     <item><description>Engine Settings - Global engine defaults (lowest priority)</description></item>
        /// </list>
        /// Each level can optionally override the next level down using <see cref="OverrideableSetting{T}"/>.
        /// </remarks>
        public static class EffectiveSettings
        {
            /// <summary>
            /// Fired when any effective setting value changes due to override changes at any level.
            /// </summary>
            public static event Action? EffectiveSettingsChanged;

            internal static void NotifyEffectiveSettingsChanged()
                => EffectiveSettingsChanged?.Invoke();

            #region Threading Settings

            /// <summary>
            /// Gets the effective number of job worker threads.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static int? JobWorkers
                => OverrideableSettingExtensions.ResolveCascadeNullable(
                    Rendering.Settings.JobWorkers,
                    GameSettings?.JobWorkersOverride,
                    UserSettings?.JobWorkersOverride);

            /// <summary>
            /// Gets the effective job worker thread cap.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static int? JobWorkerCap
                => OverrideableSettingExtensions.ResolveCascadeNullable(
                    Rendering.Settings.JobWorkerCap,
                    GameSettings?.JobWorkerCapOverride,
                    UserSettings?.JobWorkerCapOverride);

            /// <summary>
            /// Gets the effective job queue limit.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static int? JobQueueLimit
                => OverrideableSettingExtensions.ResolveCascadeNullable(
                    Rendering.Settings.JobQueueLimit,
                    GameSettings?.JobQueueLimitOverride,
                    UserSettings?.JobQueueLimitOverride);

            /// <summary>
            /// Gets the effective job queue warning threshold.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static int? JobQueueWarningThreshold
                => OverrideableSettingExtensions.ResolveCascadeNullable(
                    Rendering.Settings.JobQueueWarningThreshold,
                    GameSettings?.JobQueueWarningThresholdOverride,
                    UserSettings?.JobQueueWarningThresholdOverride);

            #endregion

            #region Rendering Settings

            /// <summary>
            /// Gets the effective Vulkan GPU-driven profile selection.
            /// Resolved from: Project Override > Engine Default.
            /// </summary>
            public static EVulkanGpuDrivenProfile VulkanGpuDrivenProfile
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.VulkanGpuDrivenProfile,
                    GameSettings?.VulkanGpuDrivenProfileOverride,
                    null);

            /// <summary>
            /// Gets whether Vulkan descriptor indexing should be enabled when supported.
            /// </summary>
            public static bool EnableVulkanDescriptorIndexing
                => Rendering.Settings.EnableVulkanDescriptorIndexing;

            /// <summary>
            /// Gets whether Vulkan material-table population is enabled.
            /// </summary>
            public static bool EnableVulkanBindlessMaterialTable
                => Rendering.Settings.EnableVulkanBindlessMaterialTable;

            /// <summary>
            /// Gets whether Vulkan descriptor contracts should be validated.
            /// </summary>
            public static bool ValidateVulkanDescriptorContracts
                => Rendering.Settings.ValidateVulkanDescriptorContracts;

            /// <summary>
            /// Gets optional Vulkan geometry fetch strategy.
            /// </summary>
            public static EVulkanGeometryFetchMode VulkanGeometryFetchMode
                => Rendering.Settings.VulkanGeometryFetchMode;

            /// <summary>
            /// Gets active Vulkan queue overlap mode selection.
            /// </summary>
            public static EVulkanQueueOverlapMode VulkanQueueOverlapMode
                => Rendering.Settings.VulkanQueueOverlapMode;

            /// <summary>
            /// Gets the effective GPU render dispatch setting.
            /// Resolved from: User Override > Project Setting (GPURenderDispatch is a project-level primary setting)
            /// </summary>
            public static bool GPURenderDispatch
            {
                get
                {
                    // User can override the project setting
                    if (UserSettings?.GPURenderDispatchOverride is { HasOverride: true } userOverride)
                        return userOverride.Value;

                    // Project setting is the primary source for this experimental feature
                    return GameSettings?.GPURenderDispatch ?? false;
                }
            }

            /// <summary>
            /// Gets the effective GPU BVH usage toggle.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool UseGpuBvh
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.UseGpuBvh,
                    GameSettings?.UseGpuBvhOverride,
                    null);

            /// <summary>
            /// Gets the active GPU culling data layout mode.
            /// </summary>
            public static EGpuCullingDataLayout GpuCullingDataLayout
                => Rendering.Settings.GpuCullingDataLayout;

            /// <summary>
            /// Gets the active occlusion culling mode for GPU indirect rendering.
            /// </summary>
            public static EOcclusionCullingMode GpuOcclusionCullingMode
                => Rendering.Settings.GpuOcclusionCullingMode;

            /// <summary>
            /// Gets the effective BVH leaf primitive budget for GPU builds.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static uint BvhLeafMaxPrims
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.BvhLeafMaxPrims,
                    GameSettings?.BvhLeafMaxPrimsOverride,
                    null);

            /// <summary>
            /// Gets the effective GPU BVH build mode.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static EBvhMode BvhMode
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.BvhMode,
                    GameSettings?.BvhModeOverride,
                    null);

            /// <summary>
            /// Gets whether GPU BVH updates should prefer refits when counts are stable.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool BvhRefitOnlyWhenStable
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.BvhRefitOnlyWhenStable,
                    GameSettings?.BvhRefitOnlyWhenStableOverride,
                    null);

            /// <summary>
            /// Gets the effective GPU BVH raycast buffer size in bytes.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static uint RaycastBufferSize
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.RaycastBufferSize,
                    GameSettings?.RaycastBufferSizeOverride,
                    null);

            /// <summary>
            /// Gets whether GPU BVH timestamp queries are enabled for profiling.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableGpuBvhTimingQueries
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableGpuBvhTimingQueries,
                    GameSettings?.EnableGpuBvhTimingQueriesOverride,
                    null);

            /// <summary>
            /// Gets the effective GPU indirect debug logging setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableGpuIndirectDebugLogging
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableGpuIndirectDebugLogging,
                    GameSettings?.EnableGpuIndirectDebugLoggingOverride,
                    UserSettings?.EnableGpuIndirectDebugLoggingOverride);

            /// <summary>
            /// Gets the effective GPU indirect validation logging setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableGpuIndirectValidationLogging
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableGpuIndirectValidationLogging,
                    GameSettings?.EnableGpuIndirectValidationLoggingOverride,
                    UserSettings?.EnableGpuIndirectValidationLoggingOverride);

            /// <summary>
            /// Gets the effective GPU indirect CPU fallback setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableGpuIndirectCpuFallback
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableGpuIndirectCpuFallback,
                    GameSettings?.EnableGpuIndirectCpuFallbackOverride,
                    UserSettings?.EnableGpuIndirectCpuFallbackOverride);

            /// <summary>
            /// Gets the effective anti-aliasing mode.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static EAntiAliasingMode AntiAliasingMode
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.AntiAliasingMode,
                    GameSettings?.AntiAliasingModeOverride,
                    UserSettings?.AntiAliasingModeOverride);

            /// <summary>
            /// Gets the effective MSAA sample count.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static uint MsaaSampleCount
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.MsaaSampleCount,
                    GameSettings?.MsaaSampleCountOverride,
                    UserSettings?.MsaaSampleCountOverride);

            /// <summary>
            /// Gets the effective VSync mode.
            /// Resolved from: User Override > Project Override > Engine Default (via UserSettings.VSync)
            /// </summary>
            public static EVSyncMode VSync
                => OverrideableSettingExtensions.ResolveCascade(
                    UserSettings?.VSync ?? EVSyncMode.Adaptive,
                    GameSettings?.VSyncOverride,
                    UserSettings?.VSyncOverride);

            /// <summary>
            /// Gets the effective global illumination mode.
            /// Resolved from: User Override > Project Override > Engine Default (via UserSettings.GlobalIlluminationMode)
            /// </summary>
            public static EGlobalIlluminationMode GlobalIlluminationMode
                => OverrideableSettingExtensions.ResolveCascade(
                    UserSettings?.GlobalIlluminationMode ?? EGlobalIlluminationMode.LightProbesAndIbl,
                    GameSettings?.GlobalIlluminationModeOverride,
                    UserSettings?.GlobalIlluminationModeOverride);

            /// <summary>
            /// Gets the effective NVIDIA DLSS setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableNvidiaDlss
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableNvidiaDlss,
                    GameSettings?.EnableNvidiaDlssOverride,
                    UserSettings?.EnableNvidiaDlssOverride);

            /// <summary>
            /// Gets the effective DLSS quality mode.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static EDlssQualityMode DlssQuality
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.DlssQuality,
                    GameSettings?.DlssQualityOverride,
                    UserSettings?.DlssQualityOverride);

            /// <summary>
            /// Gets the effective Intel XeSS setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool EnableIntelXess
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.EnableIntelXess,
                    GameSettings?.EnableIntelXessOverride,
                    UserSettings?.EnableIntelXessOverride);

            /// <summary>
            /// Gets the effective XeSS quality mode.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static EXessQualityMode XessQuality
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.XessQuality,
                    GameSettings?.XessQualityOverride,
                    UserSettings?.XessQualityOverride);

            #endregion

            #region Performance Settings

            /// <summary>
            /// Gets the effective parallel tick processing setting.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static bool TickGroupedItemsInParallel
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.TickGroupedItemsInParallel,
                    GameSettings?.TickGroupedItemsInParallelOverride,
                    UserSettings?.TickGroupedItemsInParallelOverride);

            /// <summary>
            /// Gets the effective target updates per second.
            /// Resolved from: User Override > Project Setting (no engine default)
            /// </summary>
            public static float? TargetUpdatesPerSecond
            {
                get
                {
                    if (UserSettings?.TargetUpdatesPerSecondOverride is { HasOverride: true } userOverride)
                        return userOverride.Value;
                    return GameSettings?.TargetUpdatesPerSecond;
                }
            }

            /// <summary>
            /// Gets the effective target render frames per second.
            /// Resolved from: User Override > Project Setting (no engine default)
            /// </summary>
            public static float? TargetFramesPerSecond
            {
                get
                {
                    if (UserSettings?.TargetFramesPerSecondOverride is { HasOverride: true } userOverride)
                        return userOverride.Value;
                    return GameSettings?.TargetFramesPerSecond;
                }
            }

            /// <summary>
            /// Gets the effective target FPS while unfocused.
            /// Resolved from: User Override > Project Setting; falls back to TargetFramesPerSecond.
            /// </summary>
            public static float? UnfocusedTargetFramesPerSecond
            {
                get
                {
                    if (UserSettings?.UnfocusedTargetFramesPerSecondOverride is { HasOverride: true } userOverride)
                        return userOverride.Value;

                    var projectValue = GameSettings?.UnfocusedTargetFramesPerSecond;
                    return projectValue ?? TargetFramesPerSecond;
                }
            }

            /// <summary>
            /// Gets the effective fixed frames per second (physics rate).
            /// Resolved from: User Override > Project Setting (no engine default)
            /// </summary>
            public static float FixedFramesPerSecond
            {
                get
                {
                    if (UserSettings?.FixedFramesPerSecondOverride is { HasOverride: true } userOverride)
                        return userOverride.Value;
                    return GameSettings?.FixedFramesPerSecond ?? 90.0f;
                }
            }

            #endregion

            #region Technical Settings (Project > Engine only)

            /// <summary>
            /// Gets the effective shader pipelines setting.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static bool AllowShaderPipelines
                => GameSettings?.AllowShaderPipelinesOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.AllowShaderPipelines;

            /// <summary>
            /// Gets the effective integer weighting IDs setting.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static bool UseIntegerWeightingIds
                => GameSettings?.UseIntegerWeightingIdsOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.UseIntegerWeightingIds;

            /// <summary>
            /// Gets the effective child matrix recalculation loop type.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static ELoopType RecalcChildMatricesLoopType
                => GameSettings?.RecalcChildMatricesLoopTypeOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.RecalcChildMatricesLoopType;

            /// <summary>
            /// Gets the effective compute shader skinning setting.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static bool CalculateSkinningInComputeShader
                => GameSettings?.CalculateSkinningInComputeShaderOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.CalculateSkinningInComputeShader;

            /// <summary>
            /// Gets the effective compute shader blendshapes setting.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static bool CalculateBlendshapesInComputeShader
                => GameSettings?.CalculateBlendshapesInComputeShaderOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.CalculateBlendshapesInComputeShader;

            #endregion

            #region Networking Settings (Project > Engine only)

            /// <summary>
            /// Gets the effective interval in seconds between full keyframes sent to the network for transforms.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static float TransformReplicationKeyframeIntervalSec
                => GameSettings?.TransformReplicationKeyframeIntervalSecOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.TransformReplicationKeyframeIntervalSec;

            /// <summary>
            /// Gets the effective minimum interval in seconds between replicated tick updates for world objects.
            /// Resolved from: Project Override > Engine Default (not user-overridable)
            /// </summary>
            public static float TimeBetweenReplications
                => GameSettings?.TimeBetweenReplicationsOverride is { HasOverride: true } projectOverride
                    ? projectOverride.Value
                    : Rendering.Settings.TimeBetweenReplications;

            #endregion

            #region Debug Settings

            /// <summary>
            /// Gets the effective output verbosity level.
            /// Resolved from: User Override > Project Override > Engine Default
            /// </summary>
            public static EOutputVerbosity OutputVerbosity
                => OverrideableSettingExtensions.ResolveCascade(
                    Rendering.Settings.OutputVerbosity,
                    GameSettings?.OutputVerbosityOverride,
                    UserSettings?.OutputVerbosityOverride);

            #endregion

            #region Helper Methods

            /// <summary>
            /// Gets the source level that is providing a particular setting value.
            /// Useful for debugging and UI display.
            /// </summary>
            public enum SettingSource
            {
                /// <summary>Value comes from engine defaults.</summary>
                Engine,
                /// <summary>Value comes from project settings.</summary>
                Project,
                /// <summary>Value comes from user preferences.</summary>
                User
            }

            /// <summary>
            /// Determines which level is providing the JobWorkers value.
            /// </summary>
            public static SettingSource GetJobWorkersSource()
            {
                if (UserSettings?.JobWorkersOverride is { HasOverride: true })
                    return SettingSource.User;
                if (GameSettings?.JobWorkersOverride is { HasOverride: true })
                    return SettingSource.Project;
                return SettingSource.Engine;
            }

            /// <summary>
            /// Determines which level is providing the GPURenderDispatch value.
            /// </summary>
            public static SettingSource GetGPURenderDispatchSource()
            {
                if (UserSettings?.GPURenderDispatchOverride is { HasOverride: true })
                    return SettingSource.User;
                // GPURenderDispatch is primarily a project setting, not engine default
                return SettingSource.Project;
            }

            /// <summary>
            /// Determines which level is providing the OutputVerbosity value.
            /// </summary>
            public static SettingSource GetOutputVerbositySource()
            {
                if (UserSettings?.OutputVerbosityOverride is { HasOverride: true })
                    return SettingSource.User;
                if (GameSettings?.OutputVerbosityOverride is { HasOverride: true })
                    return SettingSource.Project;
                return SettingSource.Engine;
            }

            #endregion
        }
    }
}
