using System;

namespace XREngine.Rendering;

/// <summary>
/// Centralized cache for engine environment-variable overrides that participate in the
/// effective-settings cascade (CPU/GPU culling structure, zero-readback draw path, forced
/// mesh submission strategy, etc.).
/// <para>
/// Each field captures the raw <see cref="Environment.GetEnvironmentVariable"/> value
/// once at static-ctor time. Consumers should treat env-vars as a startup-only seed:
/// the process environment is fixed for the duration of the run, so caching here removes
/// per-call OS calls and centralizes the env-var contract for the editor and runtime.
/// </para>
/// </summary>
public static class EffectiveSettingsEnvOverrides
{
    static EffectiveSettingsEnvOverrides()
    {
        try
        {
            CpuSceneCullingStructure = Read("XRE_CPU_SCENE_CULLING_STRUCTURE");
            ZeroReadbackMaterialDrawPath = Read("XRE_ZERO_READBACK_MATERIAL_DRAW_PATH");
            ForceMeshSubmissionStrategy = Read("XRE_FORCE_MESH_SUBMISSION_STRATEGY");
            OcclusionCullingMode = Read("XRE_OCCLUSION_CULLING_MODE");
            CpuQueryOcclusionRetestPeriodFrames = Read("XRE_CPU_QUERY_OCCLUSION_RETEST_PERIOD_FRAMES");
            CpuSocOcclusion = Read("XRE_CPU_SOC_OCCLUSION");
        }
        catch
        {
        }
    }

    /// <summary>Raw value of <c>XRE_CPU_SCENE_CULLING_STRUCTURE</c> (trimmed) or null if unset.</summary>
    public static string? CpuSceneCullingStructure { get; }

    /// <summary>Raw value of <c>XRE_ZERO_READBACK_MATERIAL_DRAW_PATH</c> (trimmed) or null if unset.</summary>
    public static string? ZeroReadbackMaterialDrawPath { get; }

    /// <summary>Raw value of <c>XRE_FORCE_MESH_SUBMISSION_STRATEGY</c> (untrimmed; parser tolerates whitespace).</summary>
    public static string? ForceMeshSubmissionStrategy { get; }

    /// <summary>Raw value of <c>XRE_OCCLUSION_CULLING_MODE</c> (trimmed) or null if unset.</summary>
    public static string? OcclusionCullingMode { get; }

    /// <summary>Raw value of <c>XRE_CPU_QUERY_OCCLUSION_RETEST_PERIOD_FRAMES</c> (trimmed) or null if unset.</summary>
    public static string? CpuQueryOcclusionRetestPeriodFrames { get; }

    /// <summary>Raw value of <c>XRE_CPU_SOC_OCCLUSION</c> (trimmed) or null if unset.</summary>
    public static string? CpuSocOcclusion { get; }

    private static string? Read(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // ForceMeshSubmissionStrategy parser handles its own trimming; everything else is trimmed here.
        return name == "XRE_FORCE_MESH_SUBMISSION_STRATEGY" ? raw : raw.Trim();
    }
}
