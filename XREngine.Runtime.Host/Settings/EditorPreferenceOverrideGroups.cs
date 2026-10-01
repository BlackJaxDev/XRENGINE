using MemoryPack;
using Newtonsoft.Json;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine;

public partial class EditorPreferencesOverrides
{
    private EditorViewportPreferenceOverrides? _viewport;
    private EditorSelectionPreferenceOverrides? _selection;
    private EditorDiagnosticsPreferenceOverrides? _diagnostics;

    [JsonIgnore]
    [YamlIgnore]
    [MemoryPackIgnore]
    public EditorViewportPreferenceOverrides Viewport
        => _viewport ??= new EditorViewportPreferenceOverrides(this);

    [JsonIgnore]
    [YamlIgnore]
    [MemoryPackIgnore]
    public EditorSelectionPreferenceOverrides Selection
        => _selection ??= new EditorSelectionPreferenceOverrides(this);

    [JsonIgnore]
    [YamlIgnore]
    [MemoryPackIgnore]
    public EditorDiagnosticsPreferenceOverrides Diagnostics
        => _diagnostics ??= new EditorDiagnosticsPreferenceOverrides(Debug);
}

public sealed class EditorViewportPreferenceOverrides(EditorPreferencesOverrides owner)
{
    public OverrideableSetting<EditorPreferences.EViewportPresentationMode> PresentationModeOverride
    {
        get => owner.ViewportPresentationModeOverride;
        set => owner.ViewportPresentationModeOverride = value ?? new();
    }

    public OverrideableSetting<EditorPreferences.ESceneDepthModePreference> SceneDepthModeOverride
    {
        get => owner.SceneDepthModeOverride;
        set => owner.SceneDepthModeOverride = value ?? new();
    }

    public OverrideableSetting<int> ScenePanelResizeDebounceMsOverride
    {
        get => owner.ScenePanelResizeDebounceMsOverride;
        set => owner.ScenePanelResizeDebounceMsOverride = value ?? new();
    }

    public OverrideableSetting<EInteractiveWindowResizeStrategy> InteractiveResizeStrategyOverride
    {
        get => owner.InteractiveResizeStrategyOverride;
        set => owner.InteractiveResizeStrategyOverride = value ?? new();
    }
}

public sealed class EditorSelectionPreferenceOverrides(EditorPreferencesOverrides owner)
{
    public OverrideableSetting<bool> HoverOutlineEnabledOverride
    {
        get => owner.HoverOutlineEnabledOverride;
        set => owner.HoverOutlineEnabledOverride = value ?? new();
    }

    public OverrideableSetting<bool> SelectionOutlineEnabledOverride
    {
        get => owner.SelectionOutlineEnabledOverride;
        set => owner.SelectionOutlineEnabledOverride = value ?? new();
    }

    public OverrideableSetting<ColorF4> HoverOutlineColorOverride
    {
        get => owner.HoverOutlineColorOverride;
        set => owner.HoverOutlineColorOverride = value ?? new();
    }

    public OverrideableSetting<ColorF4> SelectionOutlineColorOverride
    {
        get => owner.SelectionOutlineColorOverride;
        set => owner.SelectionOutlineColorOverride = value ?? new();
    }
}

public sealed class EditorDiagnosticsPreferenceOverrides
{
    public EditorDiagnosticsPreferenceOverrides(EditorDebugOverrides owner)
    {
        General = new EditorGeneralDiagnosticsPreferenceOverrides();
        Visualization = new EditorVisualizationDiagnosticsPreferenceOverrides(owner);
        RenderPipeline = new EditorRenderPipelineDiagnosticsPreferenceOverrides(owner);
        Culling = new EditorCullingDiagnosticsPreferenceOverrides(owner);
        Exceptions = new EditorExceptionDiagnosticsPreferenceOverrides();
        OpenGL = new EditorOpenGLDiagnosticsPreferenceOverrides();
        Vulkan = new EditorVulkanDiagnosticsPreferenceOverrides();
        Profiler = new EditorProfilerPreferenceOverrides(owner);
    }

    public EditorGeneralDiagnosticsPreferenceOverrides General { get; }
    public EditorVisualizationDiagnosticsPreferenceOverrides Visualization { get; }
    public EditorRenderPipelineDiagnosticsPreferenceOverrides RenderPipeline { get; }
    public EditorCullingDiagnosticsPreferenceOverrides Culling { get; }
    public EditorExceptionDiagnosticsPreferenceOverrides Exceptions { get; }
    public EditorOpenGLDiagnosticsPreferenceOverrides OpenGL { get; }
    public EditorVulkanDiagnosticsPreferenceOverrides Vulkan { get; }
    public EditorProfilerPreferenceOverrides Profiler { get; }
}

public sealed class EditorGeneralDiagnosticsPreferenceOverrides
{
    public OverrideableSetting<bool> ModelRenderDiagnosticsEnabledOverride
    {
        get => new();
        set { }
    }

    public OverrideableSetting<bool> DirectionalShadowAuditOverride
    {
        get => new();
        set { }
    }
}

public sealed class EditorVisualizationDiagnosticsPreferenceOverrides(EditorDebugOverrides owner)
{
    public OverrideableSetting<bool> RenderMesh3DBoundsOverride
    {
        get => owner.RenderMesh3DBoundsOverride;
        set => owner.RenderMesh3DBoundsOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderMesh2DBoundsOverride
    {
        get => owner.RenderMesh2DBoundsOverride;
        set => owner.RenderMesh2DBoundsOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderTransformDebugInfoOverride
    {
        get => owner.RenderTransformDebugInfoOverride;
        set => owner.RenderTransformDebugInfoOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderTransformLinesOverride
    {
        get => owner.RenderTransformLinesOverride;
        set => owner.RenderTransformLinesOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderTransformPointsOverride
    {
        get => owner.RenderTransformPointsOverride;
        set => owner.RenderTransformPointsOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderTransformCapsulesOverride
    {
        get => owner.RenderTransformCapsulesOverride;
        set => owner.RenderTransformCapsulesOverride = value ?? new();
    }

    public OverrideableSetting<bool> Preview3DWorldOctreeOverride
    {
        get => owner.Preview3DWorldOctreeOverride;
        set => owner.Preview3DWorldOctreeOverride = value ?? new();
    }

    public OverrideableSetting<bool> Preview2DWorldQuadtreeOverride
    {
        get => owner.Preview2DWorldQuadtreeOverride;
        set => owner.Preview2DWorldQuadtreeOverride = value ?? new();
    }

    public OverrideableSetting<bool> PreviewTracesOverride
    {
        get => owner.PreviewTracesOverride;
        set => owner.PreviewTracesOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderCullingVolumesOverride
    {
        get => owner.RenderCullingVolumesOverride;
        set => owner.RenderCullingVolumesOverride = value ?? new();
    }

    public OverrideableSetting<bool> RenderLightProbeTetrahedraOverride
    {
        get => owner.RenderLightProbeTetrahedraOverride;
        set => owner.RenderLightProbeTetrahedraOverride = value ?? new();
    }
}

public sealed class EditorRenderPipelineDiagnosticsPreferenceOverrides(EditorDebugOverrides owner)
{
    public OverrideableSetting<bool> UseDebugOpaquePipelineOverride
    {
        get => owner.UseDebugOpaquePipelineOverride;
        set => owner.UseDebugOpaquePipelineOverride = value ?? new();
    }
}

public sealed class EditorCullingDiagnosticsPreferenceOverrides(EditorDebugOverrides owner)
{
    public OverrideableSetting<bool> ForceGpuPassthroughCullingOverride
    {
        get => owner.ForceGpuPassthroughCullingOverride;
        set => owner.ForceGpuPassthroughCullingOverride = value ?? new();
    }

    public OverrideableSetting<bool> AllowGpuCpuFallbackOverride
    {
        get => owner.AllowGpuCpuFallbackOverride;
        set => owner.AllowGpuCpuFallbackOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableZeroReadbackMaterialScatterOverride
    {
        get => owner.EnableZeroReadbackMaterialScatterOverride;
        set => owner.EnableZeroReadbackMaterialScatterOverride = value ?? new();
    }

    public OverrideableSetting<EZeroReadbackMaterialDrawPath> ZeroReadbackMaterialDrawPathOverride
    {
        get => owner.ZeroReadbackMaterialDrawPathOverride;
        set => owner.ZeroReadbackMaterialDrawPathOverride = value ?? new();
    }
}

public sealed class EditorExceptionDiagnosticsPreferenceOverrides
{
}

public sealed class EditorOpenGLDiagnosticsPreferenceOverrides
{
}

public sealed class EditorVulkanDiagnosticsPreferenceOverrides
{
}

public sealed class EditorProfilerPreferenceOverrides(EditorDebugOverrides owner)
{
    public OverrideableSetting<bool> EnableFrameLoggingOverride
    {
        get => owner.EnableProfilerFrameLoggingOverride;
        set => owner.EnableProfilerFrameLoggingOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableComponentTimingOverride
    {
        get => owner.EnableProfilerComponentTimingOverride;
        set => owner.EnableProfilerComponentTimingOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableRenderStatisticsTrackingOverride
    {
        get => owner.EnableRenderStatisticsTrackingOverride;
        set => owner.EnableRenderStatisticsTrackingOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableGpuRenderPipelineProfilingOverride
    {
        get => owner.EnableGpuRenderPipelineProfilingOverride;
        set => owner.EnableGpuRenderPipelineProfilingOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableThreadAllocationTrackingOverride
    {
        get => owner.EnableThreadAllocationTrackingOverride;
        set => owner.EnableThreadAllocationTrackingOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableMainThreadInvokeDiagnosticsOverride
    {
        get => owner.EnableMainThreadInvokeDiagnosticsOverride;
        set => owner.EnableMainThreadInvokeDiagnosticsOverride = value ?? new();
    }

    public OverrideableSetting<bool> EnableUdpSendingOverride
    {
        get => owner.EnableProfilerUdpSendingOverride;
        set => owner.EnableProfilerUdpSendingOverride = value ?? new();
    }

    public OverrideableSetting<bool> StartExternalProfilerOnStartupOverride
    {
        get => owner.StartExternalProfilerOnStartupOverride;
        set => owner.StartExternalProfilerOnStartupOverride = value ?? new();
    }

    public OverrideableSetting<ProfilerTimingDisplayMode> CpuTimingDisplayModeOverride
    {
        get => owner.ProfilerPanelCpuTimingDisplayModeOverride;
        set => owner.ProfilerPanelCpuTimingDisplayModeOverride = value ?? new();
    }

    public OverrideableSetting<ProfilerTimingDisplayMode> GpuTimingDisplayModeOverride
    {
        get => owner.ProfilerPanelGpuTimingDisplayModeOverride;
        set => owner.ProfilerPanelGpuTimingDisplayModeOverride = value ?? new();
    }
}
