using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Valve.VR;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Models;
using XREngine.Rendering.Pipelines;
using XREngine.Rendering.Vulkan;
using XREngine.Timers;

namespace XREngine;

internal sealed class RuntimeEffectiveSettings
{
    private RuntimeRenderSettings Settings => RuntimeEngine.Rendering.Settings;
    private bool _enableVulkanBindlessMaterialTable = RuntimeRenderingHostServiceDefaults.EnableVulkanBindlessMaterialTable;
    private bool _enableVulkanDescriptorIndexing = RuntimeRenderingHostServiceDefaults.EnableVulkanDescriptorIndexing;
    private bool _validateVulkanDescriptorContracts = RuntimeRenderingHostServiceDefaults.ValidateVulkanDescriptorContracts;
    private EVulkanBindlessMaterialMode _vulkanBindlessMaterialMode = RuntimeRenderingHostServiceDefaults.VulkanBindlessMaterialMode;
    private EVulkanGeometryFetchMode _vulkanGeometryFetchMode = RuntimeRenderingHostServiceDefaults.VulkanGeometryFetchMode;
    private EVulkanRenderTargetMode _vulkanRenderTargetMode = RuntimeRenderingHostServiceDefaults.VulkanRenderTargetMode;
    private EVulkanGpuDrivenProfile _vulkanGpuDrivenProfile = RuntimeRenderingHostServiceDefaults.VulkanGpuDrivenProfile;
    private EVulkanQueueOverlapMode _vulkanQueueOverlapMode = RuntimeRenderingHostServiceDefaults.VulkanQueueOverlapMode;
    private EVulkanDiagnosticPreset _vulkanDiagnosticPreset = RuntimeRenderingHostServiceDefaults.VulkanDiagnosticPreset;
    private EVulkanDiagnosticFlags _vulkanDiagnosticFlags = RuntimeRenderingHostServiceDefaults.VulkanDiagnosticFlags;

    public bool AllowInitialSkinnedBoundsBuildWhenNever { get; set; } = true;
    public EAntiAliasingMode AntiAliasingMode { get; set; } = EAntiAliasingMode.None;
    public EDlssQualityMode DlssQuality => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.DlssQuality
        : Settings.DlssQuality;
    public float DlssCustomScale => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.DlssCustomScale
        : Settings.DlssCustomScale;
    public float DlssSharpness => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.DlssSharpness
        : Settings.DlssSharpness;
    public bool EnableGpuBvhTimingQueries { get; set; }
    public bool EnableGpuIndirectCpuFallback { get; set; } = true;
    public bool EnableGpuIndirectDebugLogging { get; set; }
    public bool EnableGpuIndirectValidationLogging { get; set; }
    public bool EnableIntelXess { get; set; }
    public bool EnableNvidiaDlss => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.EnableNvidiaDlss
        : Settings.EnableNvidiaDlss;
    public bool EnableNvidiaDlssFrameGeneration => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.EnableNvidiaDlssFrameGeneration
        : Settings.EnableNvidiaDlssFrameGeneration;
    public ENvidiaDlssFrameGenerationMode NvidiaDlssFrameGenerationMode => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
        ? services.NvidiaDlssFrameGenerationMode
        : Settings.NvidiaDlssFrameGenerationMode;
    public bool EnableVulkanBindlessMaterialTable
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.EnableVulkanBindlessMaterialTable
            : _enableVulkanBindlessMaterialTable;
        set => _enableVulkanBindlessMaterialTable = value;
    }
    public bool EnableVulkanDescriptorIndexing
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.EnableVulkanDescriptorIndexing
            : _enableVulkanDescriptorIndexing;
        set => _enableVulkanDescriptorIndexing = value;
    }
    public bool EnableZeroReadbackMaterialScatter { get; set; }
    public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.ZeroReadbackMaterialDrawPath;
            return !string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out EZeroReadbackMaterialDrawPath parsed)
                ? parsed
                : _zeroReadbackMaterialDrawPath;
        }
        set => _zeroReadbackMaterialDrawPath = value;
    }
    public EGpuCullingDataLayout GpuCullingDataLayout { get; set; } = EGpuCullingDataLayout.AoSHot;
    private EOcclusionCullingMode _gpuOcclusionCullingMode = EOcclusionCullingMode.GpuHiZ;
    public EOcclusionCullingMode GpuOcclusionCullingMode
    {
        get
        {
            EOcclusionCullingMode resolved = TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
                ? services.GpuOcclusionCullingMode
                : _gpuOcclusionCullingMode;
            string? raw = EffectiveSettingsEnvOverrides.OcclusionCullingMode;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out EOcclusionCullingMode parsed))
            {
                resolved = parsed;
            }

            return resolved;
        }
        set => _gpuOcclusionCullingMode = value;
    }
    private int _cpuQueryOcclusionRetestPeriodFrames = 6;
    public int CpuQueryOcclusionRetestPeriodFrames
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuQueryOcclusionRetestPeriodFrames;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int parsed))
                return Math.Clamp(parsed, 1, 64);

            return TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
                ? Math.Clamp(services.CpuQueryOcclusionRetestPeriodFrames, 1, 64)
                : _cpuQueryOcclusionRetestPeriodFrames;
        }
        set => _cpuQueryOcclusionRetestPeriodFrames = Math.Clamp(value, 1, 64);
    }

    private int _cpuQueryOcclusionMaxQueriesPerFrame = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMaxQueriesPerFrame;
    public int CpuQueryOcclusionMaxQueriesPerFrame
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuQueryOcclusionMaxQueriesPerFrame, 0, 4096)
            : _cpuQueryOcclusionMaxQueriesPerFrame;
        set => _cpuQueryOcclusionMaxQueriesPerFrame = Math.Clamp(value, 0, 4096);
    }

    private float _cpuQueryOcclusionVisibleDemotionBudgetFraction = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVisibleDemotionBudgetFraction;
    public float CpuQueryOcclusionVisibleDemotionBudgetFraction
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuQueryOcclusionVisibleDemotionBudgetFraction, 0.0f, 1.0f)
            : _cpuQueryOcclusionVisibleDemotionBudgetFraction;
        set => _cpuQueryOcclusionVisibleDemotionBudgetFraction = Math.Clamp(value, 0.0f, 1.0f);
    }

    private int _cpuQueryOcclusionRecoveryMinCadenceFrames = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionRecoveryMinCadenceFrames;
    public int CpuQueryOcclusionRecoveryMinCadenceFrames
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuQueryOcclusionRecoveryMinCadenceFrames, 1, 64)
            : _cpuQueryOcclusionRecoveryMinCadenceFrames;
        set => _cpuQueryOcclusionRecoveryMinCadenceFrames = Math.Clamp(value, 1, 64);
    }

    private float _cpuQueryOcclusionSmallMotionMeters = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionSmallMotionMeters;
    public float CpuQueryOcclusionSmallMotionMeters
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampNonNegative(services.CpuQueryOcclusionSmallMotionMeters, 100.0f)
            : _cpuQueryOcclusionSmallMotionMeters;
        set => _cpuQueryOcclusionSmallMotionMeters = ClampNonNegative(value, 100.0f);
    }

    private float _cpuQueryOcclusionMediumMotionMeters = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMediumMotionMeters;
    public float CpuQueryOcclusionMediumMotionMeters
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampNonNegative(services.CpuQueryOcclusionMediumMotionMeters, 100.0f)
            : _cpuQueryOcclusionMediumMotionMeters;
        set => _cpuQueryOcclusionMediumMotionMeters = ClampNonNegative(value, 100.0f);
    }

    private float _cpuQueryOcclusionLargeMotionMeters = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionLargeMotionMeters;
    public float CpuQueryOcclusionLargeMotionMeters
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampNonNegative(services.CpuQueryOcclusionLargeMotionMeters, 100.0f)
            : _cpuQueryOcclusionLargeMotionMeters;
        set => _cpuQueryOcclusionLargeMotionMeters = ClampNonNegative(value, 100.0f);
    }

    private float _cpuQueryOcclusionCameraCutMeters = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionCameraCutMeters;
    public float CpuQueryOcclusionCameraCutMeters
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampNonNegative(services.CpuQueryOcclusionCameraCutMeters, 1000.0f)
            : _cpuQueryOcclusionCameraCutMeters;
        set => _cpuQueryOcclusionCameraCutMeters = ClampNonNegative(value, 1000.0f);
    }

    private float _cpuQueryOcclusionSmallRotationDegrees = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionSmallRotationDegrees;
    public float CpuQueryOcclusionSmallRotationDegrees
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampDegrees(services.CpuQueryOcclusionSmallRotationDegrees)
            : _cpuQueryOcclusionSmallRotationDegrees;
        set => _cpuQueryOcclusionSmallRotationDegrees = ClampDegrees(value);
    }

    private float _cpuQueryOcclusionMediumRotationDegrees = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMediumRotationDegrees;
    public float CpuQueryOcclusionMediumRotationDegrees
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampDegrees(services.CpuQueryOcclusionMediumRotationDegrees)
            : _cpuQueryOcclusionMediumRotationDegrees;
        set => _cpuQueryOcclusionMediumRotationDegrees = ClampDegrees(value);
    }

    private float _cpuQueryOcclusionLargeRotationDegrees = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionLargeRotationDegrees;
    public float CpuQueryOcclusionLargeRotationDegrees
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampDegrees(services.CpuQueryOcclusionLargeRotationDegrees)
            : _cpuQueryOcclusionLargeRotationDegrees;
        set => _cpuQueryOcclusionLargeRotationDegrees = ClampDegrees(value);
    }

    private float _cpuQueryOcclusionCameraCutRotationDegrees = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionCameraCutRotationDegrees;
    public float CpuQueryOcclusionCameraCutRotationDegrees
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampDegrees(services.CpuQueryOcclusionCameraCutRotationDegrees)
            : _cpuQueryOcclusionCameraCutRotationDegrees;
        set => _cpuQueryOcclusionCameraCutRotationDegrees = ClampDegrees(value);
    }

    private float _cpuQueryOcclusionVrHeadMotionMeters = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVrHeadMotionMeters;
    public float CpuQueryOcclusionVrHeadMotionMeters
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampNonNegative(services.CpuQueryOcclusionVrHeadMotionMeters, 10.0f)
            : _cpuQueryOcclusionVrHeadMotionMeters;
        set => _cpuQueryOcclusionVrHeadMotionMeters = ClampNonNegative(value, 10.0f);
    }

    private float _cpuQueryOcclusionVrHeadRotationDegrees = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionVrHeadRotationDegrees;
    public float CpuQueryOcclusionVrHeadRotationDegrees
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? ClampDegrees(services.CpuQueryOcclusionVrHeadRotationDegrees)
            : _cpuQueryOcclusionVrHeadRotationDegrees;
        set => _cpuQueryOcclusionVrHeadRotationDegrees = ClampDegrees(value);
    }

    private ECpuQueryStereoMode _cpuQueryOcclusionStereoMode = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionStereoMode;
    public ECpuQueryStereoMode CpuQueryOcclusionStereoMode
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? services.CpuQueryOcclusionStereoMode
            : _cpuQueryOcclusionStereoMode;
        set => _cpuQueryOcclusionStereoMode = value;
    }

    private int _cpuQueryOcclusionMaxPendingFrames = RuntimeRenderingHostServiceDefaults.CpuQueryOcclusionMaxPendingFrames;
    public int CpuQueryOcclusionMaxPendingFrames
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuQueryOcclusionMaxPendingFrames, 1, 120)
            : _cpuQueryOcclusionMaxPendingFrames;
        set => _cpuQueryOcclusionMaxPendingFrames = Math.Clamp(value, 1, 120);
    }
    private bool _enableCpuSoftwareOcclusionCulling = false;
    public bool EnableCpuSoftwareOcclusionCulling
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuSocOcclusion;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string trimmed = raw.Trim();
                if (trimmed == "1" || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (trimmed == "0" || trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
                ? services.EnableCpuSoftwareOcclusionCulling
                : _enableCpuSoftwareOcclusionCulling;
        }
        set => _enableCpuSoftwareOcclusionCulling = value;
    }
    private int _cpuSocBufferWidth = 256;
    public int CpuSocBufferWidth
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuSocBufferWidth, 64, 4096)
            : _cpuSocBufferWidth;
        set => _cpuSocBufferWidth = Math.Clamp(value, 64, 4096);
    }
    private int _cpuSocBufferHeight = 128;
    public int CpuSocBufferHeight
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuSocBufferHeight, 32, 4096)
            : _cpuSocBufferHeight;
        set => _cpuSocBufferHeight = Math.Clamp(value, 32, 4096);
    }
    private int _cpuSocOccluderTriangleBudget = 5000;
    public int CpuSocOccluderTriangleBudget
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuSocOccluderTriangleBudget, 0, 1_000_000)
            : _cpuSocOccluderTriangleBudget;
        set => _cpuSocOccluderTriangleBudget = Math.Clamp(value, 0, 1_000_000);
    }
    private int _cpuSocMaxOccluders = 64;
    public int CpuSocMaxOccluders
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuSocMaxOccluders, 0, 4096)
            : _cpuSocMaxOccluders;
        set => _cpuSocMaxOccluders = Math.Clamp(value, 0, 4096);
    }
    private float _cpuSocMinOccluderScreenArea = 0.005f;
    public float CpuSocMinOccluderScreenArea
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? Math.Clamp(services.CpuSocMinOccluderScreenArea, 0.0f, 1.0f)
            : _cpuSocMinOccluderScreenArea;
        set => _cpuSocMinOccluderScreenArea = Math.Clamp(value, 0.0f, 1.0f);
    }
    private bool _cpuSocUseAvx2 = true;
    public bool CpuSocUseAvx2
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? services.CpuSocUseAvx2
            : _cpuSocUseAvx2;
        set => _cpuSocUseAvx2 = value;
    }
    private bool _cpuSocDebugVisualization;
    public bool CpuSocDebugVisualization
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? services.CpuSocDebugVisualization
            : _cpuSocDebugVisualization;
        set => _cpuSocDebugVisualization = value;
    }
    private bool _cpuSocDebugForceVisible;
    public bool CpuSocDebugForceVisible
    {
        get => TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
            ? services.CpuSocDebugForceVisible
            : _cpuSocDebugForceVisible;
        set => _cpuSocDebugForceVisible = value;
    }
    public bool GPURenderDispatch { get; set; }
    public EMeshSubmissionStrategy? ForceMeshSubmissionStrategy
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.ForceMeshSubmissionStrategy;
            if (EMeshSubmissionStrategyExtensions.TryParseMeshSubmissionStrategy(
                    raw,
                    out EMeshSubmissionStrategy parsed,
                    out bool usedLegacyName))
            {
                if (usedLegacyName && !_legacyGpuMeshletForceStrategyWarningLogged)
                {
                    _legacyGpuMeshletForceStrategyWarningLogged = true;
                    RuntimeEngine.LogWarning(
                        "XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuMeshlet is deprecated; use GpuMeshletZeroReadback.");
                }

                return parsed;
            }

            return _forceMeshSubmissionStrategy;
        }
        set => _forceMeshSubmissionStrategy = value;
    }
    public uint MsaaSampleCount { get; set; } = 1u;
    public ESkinnedBoundsRecomputePolicy SkinnedBoundsRecomputePolicy { get; set; } = ESkinnedBoundsRecomputePolicy.Selective;
    private ECpuSceneCullingStructure _cpuSceneCullingStructure = RuntimeRenderingHostServiceDefaults.CpuSceneCullingStructure;
    public ECpuSceneCullingStructure CpuSceneCullingStructure
    {
        get
        {
            string? raw = EffectiveSettingsEnvOverrides.CpuSceneCullingStructure;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw, ignoreCase: true, out ECpuSceneCullingStructure parsed))
            {
                return parsed;
            }

            return TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
                ? services.CpuSceneCullingStructure
                : _cpuSceneCullingStructure;
        }
        set => _cpuSceneCullingStructure = value;
    }
    public bool ValidateVulkanDescriptorContracts
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.ValidateVulkanDescriptorContracts
            : _validateVulkanDescriptorContracts;
        set => _validateVulkanDescriptorContracts = value;
    }
    public EVulkanBindlessMaterialMode VulkanBindlessMaterialMode
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanBindlessMaterialMode
            : _vulkanBindlessMaterialMode;
        set => _vulkanBindlessMaterialMode = value;
    }
    public EVulkanGeometryFetchMode VulkanGeometryFetchMode
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanGeometryFetchMode
            : _vulkanGeometryFetchMode;
        set => _vulkanGeometryFetchMode = value;
    }
    public EVulkanRenderTargetMode VulkanRenderTargetMode
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanRenderTargetMode
            : _vulkanRenderTargetMode;
        set => _vulkanRenderTargetMode = value;
    }
    public EVulkanGpuDrivenProfile VulkanGpuDrivenProfile
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanGpuDrivenProfile
            : _vulkanGpuDrivenProfile;
        set => _vulkanGpuDrivenProfile = value;
    }
    public EVulkanQueueOverlapMode VulkanQueueOverlapMode
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanQueueOverlapMode
            : _vulkanQueueOverlapMode;
        set => _vulkanQueueOverlapMode = value;
    }
    public EVulkanDiagnosticPreset VulkanDiagnosticPreset
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanDiagnosticPreset
            : _vulkanDiagnosticPreset;
        set => _vulkanDiagnosticPreset = value;
    }
    public EVulkanDiagnosticFlags VulkanDiagnosticFlags
    {
        get => TryGetHostSettings(out IRuntimeRenderSettingsServices services)
            ? services.VulkanDiagnosticFlags
            : _vulkanDiagnosticFlags;
        set => _vulkanDiagnosticFlags = value;
    }
    public EXessQualityMode XessQuality { get; set; } = EXessQualityMode.Quality;

    private EMeshSubmissionStrategy? _forceMeshSubmissionStrategy;
    private bool _legacyGpuMeshletForceStrategyWarningLogged;
    private EZeroReadbackMaterialDrawPath _zeroReadbackMaterialDrawPath = EZeroReadbackMaterialDrawPath.FullBucketScan;

    private static float ClampNonNegative(float value, float max)
        => Math.Clamp(float.IsFinite(value) ? value : 0.0f, 0.0f, max);

    private static float ClampDegrees(float value)
        => Math.Clamp(float.IsFinite(value) ? value : 0.0f, 0.0f, 180.0f);

    private static bool TryGetHostFrameTiming(out IRuntimeRenderFrameTimingServices services)
    {
        services = RuntimeRenderingHostServices.FrameTiming;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }

    private static bool TryGetHostSettings(out IRuntimeRenderSettingsServices services)
    {
        services = RuntimeRenderingHostServices.Settings;
        return RuntimeRenderingHostServices.HasConcreteHost;
    }
}
