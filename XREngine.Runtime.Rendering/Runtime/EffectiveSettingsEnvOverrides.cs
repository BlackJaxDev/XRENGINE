using System;

namespace XREngine.Rendering;

/// <summary>
/// Runtime-refreshable cache for environment overrides that participate in the effective
/// rendering-settings cascade.
/// </summary>
public static class EffectiveSettingsEnvOverrides
{
    static EffectiveSettingsEnvOverrides()
    {
        Reload();
        XREnvironment.VariableChanged += HandleEnvironmentVariableChanged;
    }

    /// <summary>
    /// Re-reads every effective-settings override from the runtime environment facade.
    /// </summary>
    public static void ReloadFromEnvironment()
        => Reload();

    private static void Reload()
    {
        try
        {
            CpuSceneCullingStructure = Read(XREngineEnvironmentVariables.CpuSceneCullingStructure);
            ZeroReadbackMaterialDrawPath = Read(XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath);
            ForceMeshSubmissionStrategy = Read(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy);
            ForceCpuIndirectBuild = Read(XREngineEnvironmentVariables.ForceCpuIndirectBuild);
            OcclusionCullingMode = Read(XREngineEnvironmentVariables.OcclusionCullingMode);
            CpuQueryOcclusionRetestPeriodFrames = Read(XREngineEnvironmentVariables.CpuQueryOcclusionRetestPeriodFrames);
            CpuSocOcclusion = Read(XREngineEnvironmentVariables.CpuSoftwareOcclusion);
            AdvancedRenderPipelineMode = Read(XREngineEnvironmentVariables.AdvancedRenderPipelineMode);
        }
        catch
        {
        }
    }

    internal static void ReloadForTests()
    {
        XREnvironment.RefreshFromProcess();
        Reload();
    }

    /// <summary>Raw value of <c>XRE_CPU_SCENE_CULLING_STRUCTURE</c> (trimmed) or null if unset.</summary>
    public static string? CpuSceneCullingStructure { get; private set; }

    /// <summary>Raw value of <c>XRE_ZERO_READBACK_MATERIAL_DRAW_PATH</c> (trimmed) or null if unset.</summary>
    public static string? ZeroReadbackMaterialDrawPath { get; private set; }

    /// <summary>Raw value of <c>XRE_FORCE_MESH_SUBMISSION_STRATEGY</c> (untrimmed; parser tolerates whitespace).</summary>
    public static string? ForceMeshSubmissionStrategy { get; private set; }

    /// <summary>Raw value of <c>XRE_FORCE_CPU_INDIRECT_BUILD</c> (trimmed) or null if unset.</summary>
    public static string? ForceCpuIndirectBuild { get; private set; }

    /// <summary>Raw value of <c>XRE_OCCLUSION_CULLING_MODE</c> (trimmed) or null if unset.</summary>
    public static string? OcclusionCullingMode { get; private set; }

    /// <summary>Raw value of <c>XRE_CPU_QUERY_OCCLUSION_RETEST_PERIOD_FRAMES</c> (trimmed) or null if unset.</summary>
    public static string? CpuQueryOcclusionRetestPeriodFrames { get; private set; }

    /// <summary>Raw value of <c>XRE_CPU_SOC_OCCLUSION</c> (trimmed) or null if unset.</summary>
    public static string? CpuSocOcclusion { get; private set; }

    /// <summary>Raw value of <c>XRE_ADVANCED_RENDER_PIPELINE_MODE</c> (trimmed) or null if unset.</summary>
    public static string? AdvancedRenderPipelineMode { get; private set; }

    private static string? Read(string name)
    {
        string? raw = XREnvironment.GetValue(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // ForceMeshSubmissionStrategy parser handles its own trimming; everything else is trimmed here.
        return name == XREngineEnvironmentVariables.ForceMeshSubmissionStrategy ? raw : raw.Trim();
    }

    private static void HandleEnvironmentVariableChanged(RuntimeEnvironmentVariableChange change)
    {
        if (change.Name.Equals(XREngineEnvironmentVariables.CpuSceneCullingStructure, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.ZeroReadbackMaterialDrawPath, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.ForceMeshSubmissionStrategy, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.ForceCpuIndirectBuild, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.OcclusionCullingMode, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.CpuQueryOcclusionRetestPeriodFrames, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.CpuSoftwareOcclusion, StringComparison.OrdinalIgnoreCase) ||
            change.Name.Equals(XREngineEnvironmentVariables.AdvancedRenderPipelineMode, StringComparison.OrdinalIgnoreCase))
        {
            Reload();
        }
    }
}
