using XREngine.Data.Colors;
using XREngine.Data.Profiling;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine;

public sealed class EditorViewportPreferences(EditorPreferences owner)
{
    public EditorPreferences.EViewportPresentationMode PresentationMode
    {
        get => owner.ViewportPresentationMode;
        set => owner.ViewportPresentationMode = value;
    }

    public EditorPreferences.ESceneDepthModePreference SceneDepthMode
    {
        get => owner.SceneDepthMode;
        set => owner.SceneDepthMode = value;
    }

    public int ScenePanelResizeDebounceMs
    {
        get => owner.ScenePanelResizeDebounceMs;
        set => owner.ScenePanelResizeDebounceMs = value;
    }

    public EInteractiveWindowResizeStrategy InteractiveResizeStrategy
    {
        get => owner.InteractiveResizeStrategy;
        set => owner.InteractiveResizeStrategy = value;
    }

    public bool ScenePanelPresentationEnabled
    {
        get => owner.ViewportPresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;
        set => owner.ViewportPresentationMode = value
            ? EditorPreferences.EViewportPresentationMode.UseViewportPanel
            : EditorPreferences.EViewportPresentationMode.FullViewportBehindImGuiUI;
    }

    public void CopyFrom(EditorViewportPreferences source)
    {
        PresentationMode = source.PresentationMode;
        SceneDepthMode = source.SceneDepthMode;
        ScenePanelResizeDebounceMs = source.ScenePanelResizeDebounceMs;
        InteractiveResizeStrategy = source.InteractiveResizeStrategy;
    }

    public void ApplyOverrides(EditorViewportPreferenceOverrides overrides)
    {
        if (overrides.PresentationModeOverride is { HasOverride: true } presentationMode)
            PresentationMode = presentationMode.Value;
        if (overrides.SceneDepthModeOverride is { HasOverride: true } sceneDepth)
            SceneDepthMode = sceneDepth.Value;
        if (overrides.ScenePanelResizeDebounceMsOverride is { HasOverride: true } resizeDebounce)
            ScenePanelResizeDebounceMs = resizeDebounce.Value;
        if (overrides.InteractiveResizeStrategyOverride is { HasOverride: true } resizeStrategy)
            InteractiveResizeStrategy = resizeStrategy.Value;
    }
}

public sealed class EditorSelectionPreferences(EditorPreferences owner)
{
    public bool HoverOutlineEnabled
    {
        get => owner.HoverOutlineEnabled;
        set => owner.HoverOutlineEnabled = value;
    }

    public bool SelectionOutlineEnabled
    {
        get => owner.SelectionOutlineEnabled;
        set => owner.SelectionOutlineEnabled = value;
    }

    public ColorF4 HoverOutlineColor
    {
        get => owner.HoverOutlineColor;
        set => owner.HoverOutlineColor = value;
    }

    public ColorF4 SelectionOutlineColor
    {
        get => owner.SelectionOutlineColor;
        set => owner.SelectionOutlineColor = value;
    }

    public bool GpuMeshBvhClickPickEnabled
    {
        get => owner.GpuMeshBvhClickPickEnabled;
        set => owner.GpuMeshBvhClickPickEnabled = value;
    }

    public void CopyFrom(EditorSelectionPreferences source)
    {
        HoverOutlineEnabled = source.HoverOutlineEnabled;
        SelectionOutlineEnabled = source.SelectionOutlineEnabled;
        HoverOutlineColor = source.HoverOutlineColor;
        SelectionOutlineColor = source.SelectionOutlineColor;
        GpuMeshBvhClickPickEnabled = source.GpuMeshBvhClickPickEnabled;
    }

    public void ApplyOverrides(EditorSelectionPreferenceOverrides overrides)
    {
        if (overrides.HoverOutlineEnabledOverride is { HasOverride: true } hoverEnabled)
            HoverOutlineEnabled = hoverEnabled.Value;
        if (overrides.SelectionOutlineEnabledOverride is { HasOverride: true } selectionEnabled)
            SelectionOutlineEnabled = selectionEnabled.Value;
        if (overrides.HoverOutlineColorOverride is { HasOverride: true } hoverColor)
            HoverOutlineColor = hoverColor.Value;
        if (overrides.SelectionOutlineColorOverride is { HasOverride: true } selectionColor)
            SelectionOutlineColor = selectionColor.Value;
    }
}

public sealed class EditorDiagnosticsPreferences
{
    public EditorDiagnosticsPreferences(EditorDebugOptions owner)
    {
        General = new EditorGeneralDiagnosticsPreferences(owner);
        Visualization = new EditorVisualizationDiagnosticsPreferences(owner);
        RenderPipeline = new EditorRenderPipelineDiagnosticsPreferences(owner);
        Culling = new EditorCullingDiagnosticsPreferences(owner);
        Exceptions = new EditorExceptionDiagnosticsPreferences(owner);
        OpenGL = new EditorOpenGLDiagnosticsPreferences(owner);
        Vulkan = new EditorVulkanDiagnosticsPreferences(owner);
        Profiler = new EditorProfilerPreferences(owner);
    }

    public EditorGeneralDiagnosticsPreferences General { get; }
    public EditorVisualizationDiagnosticsPreferences Visualization { get; }
    public EditorRenderPipelineDiagnosticsPreferences RenderPipeline { get; }
    public EditorCullingDiagnosticsPreferences Culling { get; }
    public EditorExceptionDiagnosticsPreferences Exceptions { get; }
    public EditorOpenGLDiagnosticsPreferences OpenGL { get; }
    public EditorVulkanDiagnosticsPreferences Vulkan { get; }
    public EditorProfilerPreferences Profiler { get; }

    public void CopyFrom(EditorDiagnosticsPreferences source)
    {
        General.CopyFrom(source.General);
        Visualization.CopyFrom(source.Visualization);
        RenderPipeline.CopyFrom(source.RenderPipeline);
        Culling.CopyFrom(source.Culling);
        Exceptions.CopyFrom(source.Exceptions);
        OpenGL.CopyFrom(source.OpenGL);
        Vulkan.CopyFrom(source.Vulkan);
        Profiler.CopyFrom(source.Profiler);
    }

    public void ApplyOverrides(EditorDiagnosticsPreferenceOverrides overrides)
    {
        General.ApplyOverrides(overrides.General);
        Visualization.ApplyOverrides(overrides.Visualization);
        RenderPipeline.ApplyOverrides(overrides.RenderPipeline);
        Culling.ApplyOverrides(overrides.Culling);
        Exceptions.ApplyOverrides(overrides.Exceptions);
        OpenGL.ApplyOverrides(overrides.OpenGL);
        Vulkan.ApplyOverrides(overrides.Vulkan);
        Profiler.ApplyOverrides(overrides.Profiler);
    }
}

public sealed class EditorGeneralDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DebugModelRender)]
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ModelRenderDiag)]
    public bool ModelRenderDiagnosticsEnabled
    {
        get => owner.ModelRenderDiagEnabled;
        set => owner.ModelRenderDiagEnabled = value;
    }

    public bool JoltDebugRenderDiagnostics
    {
        get => owner.JoltDebugRenderDiagnostics;
        set => owner.JoltDebugRenderDiagnostics = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DirectionalShadowAudit)]
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ShadowAudit)]
    public bool DirectionalShadowAudit
    {
        get => owner.DirectionalShadowAudit;
        set => owner.DirectionalShadowAudit = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.SkinningPrepassDiag)]
    public bool SkinningPrepassDiagnostics
    {
        get => owner.SkinningPrepassDiag;
        set => owner.SkinningPrepassDiag = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ForceSkinnedUnbounded)]
    public bool ForceSkinnedUnbounded
    {
        get => owner.ForceSkinnedUnbounded;
        set => owner.ForceSkinnedUnbounded = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.SkinCullRejectDiag)]
    public bool SkinCullRejectDiagnostics
    {
        get => owner.SkinCullRejectDiag;
        set => owner.SkinCullRejectDiag = value;
    }

    public void CopyFrom(EditorGeneralDiagnosticsPreferences source)
    {
        ModelRenderDiagnosticsEnabled = source.ModelRenderDiagnosticsEnabled;
        JoltDebugRenderDiagnostics = source.JoltDebugRenderDiagnostics;
        DirectionalShadowAudit = source.DirectionalShadowAudit;
        SkinningPrepassDiagnostics = source.SkinningPrepassDiagnostics;
        ForceSkinnedUnbounded = source.ForceSkinnedUnbounded;
        SkinCullRejectDiagnostics = source.SkinCullRejectDiagnostics;
    }

    public void ApplyOverrides(EditorGeneralDiagnosticsPreferenceOverrides overrides)
    {
        if (overrides.ModelRenderDiagnosticsEnabledOverride is { HasOverride: true } modelRender)
            ModelRenderDiagnosticsEnabled = modelRender.Value;
        if (overrides.DirectionalShadowAuditOverride is { HasOverride: true } shadowAudit)
            DirectionalShadowAudit = shadowAudit.Value;
    }
}

public sealed class EditorVisualizationDiagnosticsPreferences(EditorDebugOptions owner)
{
    public bool RenderMesh3DBounds
    {
        get => owner.RenderMesh3DBounds;
        set => owner.RenderMesh3DBounds = value;
    }

    public bool RenderMesh2DBounds
    {
        get => owner.RenderMesh2DBounds;
        set => owner.RenderMesh2DBounds = value;
    }

    public bool RenderTransformDebugInfo
    {
        get => owner.RenderTransformDebugInfo;
        set => owner.RenderTransformDebugInfo = value;
    }

    public bool RenderTransformLines
    {
        get => owner.RenderTransformLines;
        set => owner.RenderTransformLines = value;
    }

    public bool RenderTransformPoints
    {
        get => owner.RenderTransformPoints;
        set => owner.RenderTransformPoints = value;
    }

    public bool RenderTransformCapsules
    {
        get => owner.RenderTransformCapsules;
        set => owner.RenderTransformCapsules = value;
    }

    public bool Preview3DWorldOctree
    {
        get => owner.Preview3DWorldOctree;
        set => owner.Preview3DWorldOctree = value;
    }

    public bool Preview2DWorldQuadtree
    {
        get => owner.Preview2DWorldQuadtree;
        set => owner.Preview2DWorldQuadtree = value;
    }

    public bool PreviewTraces
    {
        get => owner.PreviewTraces;
        set => owner.PreviewTraces = value;
    }

    public bool RenderCullingVolumes
    {
        get => owner.RenderCullingVolumes;
        set => owner.RenderCullingVolumes = value;
    }

    public bool RenderLightProbeTetrahedra
    {
        get => owner.RenderLightProbeTetrahedra;
        set => owner.RenderLightProbeTetrahedra = value;
    }

    public void CopyFrom(EditorVisualizationDiagnosticsPreferences source)
    {
        RenderMesh3DBounds = source.RenderMesh3DBounds;
        RenderMesh2DBounds = source.RenderMesh2DBounds;
        RenderTransformDebugInfo = source.RenderTransformDebugInfo;
        RenderTransformLines = source.RenderTransformLines;
        RenderTransformPoints = source.RenderTransformPoints;
        RenderTransformCapsules = source.RenderTransformCapsules;
        Preview3DWorldOctree = source.Preview3DWorldOctree;
        Preview2DWorldQuadtree = source.Preview2DWorldQuadtree;
        PreviewTraces = source.PreviewTraces;
        RenderCullingVolumes = source.RenderCullingVolumes;
        RenderLightProbeTetrahedra = source.RenderLightProbeTetrahedra;
    }

    public void ApplyOverrides(EditorVisualizationDiagnosticsPreferenceOverrides overrides)
    {
        if (overrides.RenderMesh3DBoundsOverride is { HasOverride: true } mesh3d)
            RenderMesh3DBounds = mesh3d.Value;
        if (overrides.RenderMesh2DBoundsOverride is { HasOverride: true } mesh2d)
            RenderMesh2DBounds = mesh2d.Value;
        if (overrides.RenderTransformDebugInfoOverride is { HasOverride: true } transformInfo)
            RenderTransformDebugInfo = transformInfo.Value;
        if (overrides.RenderTransformLinesOverride is { HasOverride: true } transformLines)
            RenderTransformLines = transformLines.Value;
        if (overrides.RenderTransformPointsOverride is { HasOverride: true } transformPoints)
            RenderTransformPoints = transformPoints.Value;
        if (overrides.RenderTransformCapsulesOverride is { HasOverride: true } transformCapsules)
            RenderTransformCapsules = transformCapsules.Value;
        if (overrides.Preview3DWorldOctreeOverride is { HasOverride: true } octree)
            Preview3DWorldOctree = octree.Value;
        if (overrides.Preview2DWorldQuadtreeOverride is { HasOverride: true } quadtree)
            Preview2DWorldQuadtree = quadtree.Value;
        if (overrides.PreviewTracesOverride is { HasOverride: true } traces)
            PreviewTraces = traces.Value;
        if (overrides.RenderCullingVolumesOverride is { HasOverride: true } cullingVolumes)
            RenderCullingVolumes = cullingVolumes.Value;
        if (overrides.RenderLightProbeTetrahedraOverride is { HasOverride: true } lightProbes)
            RenderLightProbeTetrahedra = lightProbes.Value;
    }
}

public sealed class EditorRenderPipelineDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DiagVendorUpscale)]
    public bool VendorUpscaleDiagnostics
    {
        get => owner.DiagVendorUpscale;
        set => owner.DiagVendorUpscale = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DiagQuadBlit)]
    public bool QuadBlitDiagnostics
    {
        get => owner.DiagQuadBlit;
        set => owner.DiagQuadBlit = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DiagPostProcess)]
    public bool PostProcessDiagnostics
    {
        get => owner.DiagPostProcess;
        set => owner.DiagPostProcess = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DebugPresentClear)]
    public bool DebugPresentClear
    {
        get => owner.DebugPresentClear;
        set => owner.DebugPresentClear = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DeferredDebug)]
    public int DeferredDebugView
    {
        get => owner.DeferredDebugView;
        set => owner.DeferredDebugView = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DiagDeferredLighting)]
    public bool DiagDeferredLighting
    {
        get => owner.DiagDeferredLighting;
        set => owner.DiagDeferredLighting = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ForceFullViewport)]
    public bool ForceFullViewport
    {
        get => owner.ForceFullViewport;
        set => owner.ForceFullViewport = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ForceDebugOpaquePipeline)]
    public bool ForceDebugOpaquePipeline
    {
        get => owner.ForceDebugOpaquePipeline;
        set => owner.ForceDebugOpaquePipeline = value;
    }

    public bool UseDebugOpaquePipeline
    {
        get => owner.UseDebugOpaquePipeline;
        set => owner.UseDebugOpaquePipeline = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.BypassVendorUpscale)]
    public bool BypassVendorUpscale
    {
        get => owner.BypassVendorUpscale;
        set => owner.BypassVendorUpscale = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.OutputSourceFbo)]
    public string? OutputSourceFboOverride
    {
        get => owner.OutputSourceFboOverride;
        set => owner.OutputSourceFboOverride = value;
    }

    public void CopyFrom(EditorRenderPipelineDiagnosticsPreferences source)
    {
        VendorUpscaleDiagnostics = source.VendorUpscaleDiagnostics;
        QuadBlitDiagnostics = source.QuadBlitDiagnostics;
        PostProcessDiagnostics = source.PostProcessDiagnostics;
        DebugPresentClear = source.DebugPresentClear;
        DeferredDebugView = source.DeferredDebugView;
        DiagDeferredLighting = source.DiagDeferredLighting;
        ForceFullViewport = source.ForceFullViewport;
        ForceDebugOpaquePipeline = source.ForceDebugOpaquePipeline;
        UseDebugOpaquePipeline = source.UseDebugOpaquePipeline;
        BypassVendorUpscale = source.BypassVendorUpscale;
        OutputSourceFboOverride = source.OutputSourceFboOverride;
    }

    public void ApplyOverrides(EditorRenderPipelineDiagnosticsPreferenceOverrides overrides)
    {
        if (overrides.UseDebugOpaquePipelineOverride is { HasOverride: true } debugOpaque)
            UseDebugOpaquePipeline = debugOpaque.Value;
    }
}

public sealed class EditorCullingDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.HizCullTrace)]
    public bool HiZCullTrace
    {
        get => owner.HiZCullTrace;
        set => owner.HiZCullTrace = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.GpuHizDirtyBypass)]
    public bool GpuHiZDirtyBypass
    {
        get => owner.GpuHiZDirtyBypass;
        set => owner.GpuHiZDirtyBypass = value;
    }

    public bool ForceGpuPassthroughCulling
    {
        get => owner.ForceGpuPassthroughCulling;
        set => owner.ForceGpuPassthroughCulling = value;
    }

    public bool AllowGpuCpuFallback
    {
        get => owner.AllowGpuCpuFallback;
        set => owner.AllowGpuCpuFallback = value;
    }

    public bool EnableZeroReadbackMaterialScatter
    {
        get => owner.EnableZeroReadbackMaterialScatter;
        set => owner.EnableZeroReadbackMaterialScatter = value;
    }

    public EZeroReadbackMaterialDrawPath ZeroReadbackMaterialDrawPath
    {
        get => owner.ZeroReadbackMaterialDrawPath;
        set => owner.ZeroReadbackMaterialDrawPath = value;
    }

    public void CopyFrom(EditorCullingDiagnosticsPreferences source)
    {
        HiZCullTrace = source.HiZCullTrace;
        GpuHiZDirtyBypass = source.GpuHiZDirtyBypass;
        ForceGpuPassthroughCulling = source.ForceGpuPassthroughCulling;
        AllowGpuCpuFallback = source.AllowGpuCpuFallback;
        EnableZeroReadbackMaterialScatter = source.EnableZeroReadbackMaterialScatter;
        ZeroReadbackMaterialDrawPath = source.ZeroReadbackMaterialDrawPath;
    }

    public void ApplyOverrides(EditorCullingDiagnosticsPreferenceOverrides overrides)
    {
        if (overrides.ForceGpuPassthroughCullingOverride is { HasOverride: true } passthrough)
            ForceGpuPassthroughCulling = passthrough.Value;
        if (overrides.AllowGpuCpuFallbackOverride is { HasOverride: true } cpuFallback)
            AllowGpuCpuFallback = cpuFallback.Value;
        if (overrides.EnableZeroReadbackMaterialScatterOverride is { HasOverride: true } zeroReadback)
            EnableZeroReadbackMaterialScatter = zeroReadback.Value;
        if (overrides.ZeroReadbackMaterialDrawPathOverride is { HasOverride: true } drawPath)
            ZeroReadbackMaterialDrawPath = drawPath.Value;
    }
}

public sealed class EditorExceptionDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.FirstChanceExceptions)]
    public string? FirstChanceExceptionFilter
    {
        get => owner.FirstChanceExceptionFilter;
        set => owner.FirstChanceExceptionFilter = value;
    }

    public void CopyFrom(EditorExceptionDiagnosticsPreferences source)
        => FirstChanceExceptionFilter = source.FirstChanceExceptionFilter;

    public void ApplyOverrides(EditorExceptionDiagnosticsPreferenceOverrides overrides)
    {
    }
}

public sealed class EditorOpenGLDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.GlDebug)]
    public bool DebugContext
    {
        get => owner.GLDebug;
        set => owner.GLDebug = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.GlSubmitTrace)]
    public int SubmitTraceLevel
    {
        get => owner.GLSubmitTraceLevel;
        set => owner.GLSubmitTraceLevel = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.CrashBreadcrumbs)]
    public bool CrashBreadcrumbs
    {
        get => owner.CrashBreadcrumbs;
        set => owner.CrashBreadcrumbs = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.PushSubDataBreakdown)]
    public bool PushSubDataBreakdown
    {
        get => owner.PushSubDataBreakdown;
        set => owner.PushSubDataBreakdown = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.PushSubDataTrace)]
    public bool PushSubDataTrace
    {
        get => owner.PushSubDataTrace;
        set => owner.PushSubDataTrace = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DispatchTrace)]
    public bool DispatchTrace
    {
        get => owner.DispatchTrace;
        set => owner.DispatchTrace = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.DispatchFinish)]
    public bool DispatchFinish
    {
        get => owner.DispatchFinish;
        set => owner.DispatchFinish = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.UploadStageLogging)]
    public bool UploadStageLogging
    {
        get => owner.UploadStageLogging;
        set => owner.UploadStageLogging = value;
    }

    public void CopyFrom(EditorOpenGLDiagnosticsPreferences source)
    {
        DebugContext = source.DebugContext;
        SubmitTraceLevel = source.SubmitTraceLevel;
        CrashBreadcrumbs = source.CrashBreadcrumbs;
        PushSubDataBreakdown = source.PushSubDataBreakdown;
        PushSubDataTrace = source.PushSubDataTrace;
        DispatchTrace = source.DispatchTrace;
        DispatchFinish = source.DispatchFinish;
        UploadStageLogging = source.UploadStageLogging;
    }

    public void ApplyOverrides(EditorOpenGLDiagnosticsPreferenceOverrides overrides)
    {
    }
}

public sealed class EditorVulkanDiagnosticsPreferences(EditorDebugOptions owner)
{
    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkEnableAutoUniformRewrite)]
    public bool AutoUniformRewrite
    {
        get => owner.VkEnableAutoUniformRewrite;
        set => owner.VkEnableAutoUniformRewrite = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkDumpShaderOnError)]
    public bool DumpShaderOnError
    {
        get => owner.VkDumpShaderOnError;
        set => owner.VkDumpShaderOnError = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.ShaderSourceOptimizer)]
    public bool ShaderSourceOptimizer
    {
        get => owner.ShaderSourceOptimizerEnabled;
        set => owner.ShaderSourceOptimizerEnabled = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkTracePipeCreate)]
    public bool TracePipelineCreation
    {
        get => owner.VkTracePipeCreate;
        set => owner.VkTracePipeCreate = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkTraceSwapDraw)]
    public bool TraceSwapchainDraws
    {
        get => owner.VkTraceSwapDraw;
        set => owner.VkTraceSwapDraw = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkTraceDraw)]
    public bool TraceAllDraws
    {
        get => owner.VkTraceDraw;
        set => owner.VkTraceDraw = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkSkipUiPipeline)]
    public bool SkipUiPipeline
    {
        get => owner.VkSkipUiPipeline;
        set => owner.VkSkipUiPipeline = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkSkipUiBatchText)]
    public bool SkipUiBatchText
    {
        get => owner.VkSkipUiBatchText;
        set => owner.VkSkipUiBatchText = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkSkipOcclusionQueryOps)]
    public bool SkipOcclusionQueryOps
    {
        get => owner.VkSkipOcclusionQueryOps;
        set => owner.VkSkipOcclusionQueryOps = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkForceSwapchainMagenta)]
    public bool ForceSwapchainMagenta
    {
        get => owner.VkForceSwapchainMagenta;
        set => owner.VkForceSwapchainMagenta = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VkSkipImGui)]
    public bool SkipImGui
    {
        get => owner.VkSkipImGui;
        set => owner.VkSkipImGui = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanAsyncTextureUpload)]
    public bool AsyncTextureUpload
    {
        get => owner.VkAsyncTextureUpload;
        set => owner.VkAsyncTextureUpload = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanTextureUploadTransferQueue)]
    public bool TextureUploadTransferQueue
    {
        get => owner.VkTextureUploadTransferQueue;
        set => owner.VkTextureUploadTransferQueue = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanTextureUploadPrepWorker)]
    public bool TextureUploadPrepWorker
    {
        get => owner.VkTextureUploadPrepWorker;
        set => owner.VkTextureUploadPrepWorker = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanTextureUploadPrepBudgetMs)]
    public double TextureUploadPrepBudgetMilliseconds
    {
        get => owner.VkTextureUploadPrepBudgetMilliseconds;
        set => owner.VkTextureUploadPrepBudgetMilliseconds = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanTextureUploadTrace)]
    public bool TextureUploadTrace
    {
        get => owner.VkTextureUploadTrace;
        set => owner.VkTextureUploadTrace = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanProgressiveTextureUpload)]
    public bool ProgressiveTextureUpload
    {
        get => owner.VkProgressiveTextureUpload;
        set => owner.VkProgressiveTextureUpload = value;
    }

    [EnvironmentVariablePreference(XREngineEnvironmentVariables.VulkanImportedTexturePreviewFreeze)]
    public bool ImportedTexturePreviewFreeze
    {
        get => owner.VkImportedTexturePreviewFreeze;
        set => owner.VkImportedTexturePreviewFreeze = value;
    }

    public void CopyFrom(EditorVulkanDiagnosticsPreferences source)
    {
        AutoUniformRewrite = source.AutoUniformRewrite;
        DumpShaderOnError = source.DumpShaderOnError;
        ShaderSourceOptimizer = source.ShaderSourceOptimizer;
        TracePipelineCreation = source.TracePipelineCreation;
        TraceSwapchainDraws = source.TraceSwapchainDraws;
        TraceAllDraws = source.TraceAllDraws;
        SkipUiPipeline = source.SkipUiPipeline;
        SkipUiBatchText = source.SkipUiBatchText;
        SkipOcclusionQueryOps = source.SkipOcclusionQueryOps;
        ForceSwapchainMagenta = source.ForceSwapchainMagenta;
        SkipImGui = source.SkipImGui;
        AsyncTextureUpload = source.AsyncTextureUpload;
        TextureUploadTransferQueue = source.TextureUploadTransferQueue;
        TextureUploadPrepWorker = source.TextureUploadPrepWorker;
        TextureUploadPrepBudgetMilliseconds = source.TextureUploadPrepBudgetMilliseconds;
        TextureUploadTrace = source.TextureUploadTrace;
        ProgressiveTextureUpload = source.ProgressiveTextureUpload;
        ImportedTexturePreviewFreeze = source.ImportedTexturePreviewFreeze;
    }

    public void ApplyOverrides(EditorVulkanDiagnosticsPreferenceOverrides overrides)
    {
    }
}

public sealed class EditorProfilerPreferences(EditorDebugOptions owner)
{
    public bool EnableFrameLogging
    {
        get => owner.EnableProfilerFrameLogging;
        set => owner.EnableProfilerFrameLogging = value;
    }

    public bool EnableComponentTiming
    {
        get => owner.EnableProfilerComponentTiming;
        set => owner.EnableProfilerComponentTiming = value;
    }

    public bool EnableRenderStatisticsTracking
    {
        get => owner.EnableRenderStatisticsTracking;
        set => owner.EnableRenderStatisticsTracking = value;
    }

    public bool EnableGpuRenderPipelineProfiling
    {
        get => owner.EnableGpuRenderPipelineProfiling;
        set => owner.EnableGpuRenderPipelineProfiling = value;
    }

    public bool EnableThreadAllocationTracking
    {
        get => owner.EnableThreadAllocationTracking;
        set => owner.EnableThreadAllocationTracking = value;
    }

    public bool EnableMainThreadInvokeDiagnostics
    {
        get => owner.EnableMainThreadInvokeDiagnostics;
        set => owner.EnableMainThreadInvokeDiagnostics = value;
    }

    public bool EnableUdpSending
    {
        get => owner.EnableProfilerUdpSending;
        set => owner.EnableProfilerUdpSending = value;
    }

    public bool StartExternalProfilerOnStartup
    {
        get => owner.StartExternalProfilerOnStartup;
        set => owner.StartExternalProfilerOnStartup = value;
    }

    public float DebugOutputMinElapsedMs
    {
        get => owner.CodeProfilerDebugOutputMinElapsedMs;
        set => owner.CodeProfilerDebugOutputMinElapsedMs = value;
    }

    public int StatsThreadIntervalMs
    {
        get => owner.CodeProfilerStatsThreadIntervalMs;
        set => owner.CodeProfilerStatsThreadIntervalMs = value;
    }

    public int SnapshotIntervalMs
    {
        get => owner.CodeProfilerSnapshotIntervalMs;
        set => owner.CodeProfilerSnapshotIntervalMs = value;
    }

    public bool PanelPaused
    {
        get => owner.ProfilerPanelPaused;
        set => owner.ProfilerPanelPaused = value;
    }

    public bool PanelSortByTime
    {
        get => owner.ProfilerPanelSortByTime;
        set => owner.ProfilerPanelSortByTime = value;
    }

    public float PanelSmoothingAlpha
    {
        get => owner.ProfilerPanelSmoothingAlpha;
        set => owner.ProfilerPanelSmoothingAlpha = value;
    }

    public float PanelUpdateIntervalSeconds
    {
        get => owner.ProfilerPanelUpdateIntervalSeconds;
        set => owner.ProfilerPanelUpdateIntervalSeconds = value;
    }

    public int PanelGraphSampleCount
    {
        get => owner.ProfilerPanelGraphSampleCount;
        set => owner.ProfilerPanelGraphSampleCount = value;
    }

    public ProfilerTimingDisplayMode CpuTimingDisplayMode
    {
        get => owner.ProfilerPanelCpuTimingDisplayMode;
        set => owner.ProfilerPanelCpuTimingDisplayMode = value;
    }

    public ProfilerTimingDisplayMode GpuTimingDisplayMode
    {
        get => owner.ProfilerPanelGpuTimingDisplayMode;
        set => owner.ProfilerPanelGpuTimingDisplayMode = value;
    }

    public void CopyFrom(EditorProfilerPreferences source)
    {
        EnableFrameLogging = source.EnableFrameLogging;
        EnableComponentTiming = source.EnableComponentTiming;
        EnableRenderStatisticsTracking = source.EnableRenderStatisticsTracking;
        EnableGpuRenderPipelineProfiling = source.EnableGpuRenderPipelineProfiling;
        EnableThreadAllocationTracking = source.EnableThreadAllocationTracking;
        EnableMainThreadInvokeDiagnostics = source.EnableMainThreadInvokeDiagnostics;
        EnableUdpSending = source.EnableUdpSending;
        StartExternalProfilerOnStartup = source.StartExternalProfilerOnStartup;
        DebugOutputMinElapsedMs = source.DebugOutputMinElapsedMs;
        StatsThreadIntervalMs = source.StatsThreadIntervalMs;
        SnapshotIntervalMs = source.SnapshotIntervalMs;
        PanelPaused = source.PanelPaused;
        PanelSortByTime = source.PanelSortByTime;
        PanelSmoothingAlpha = source.PanelSmoothingAlpha;
        PanelUpdateIntervalSeconds = source.PanelUpdateIntervalSeconds;
        PanelGraphSampleCount = source.PanelGraphSampleCount;
        CpuTimingDisplayMode = source.CpuTimingDisplayMode;
        GpuTimingDisplayMode = source.GpuTimingDisplayMode;
    }

    public void ApplyOverrides(EditorProfilerPreferenceOverrides overrides)
    {
        if (overrides.EnableFrameLoggingOverride is { HasOverride: true } frameLogging)
            EnableFrameLogging = frameLogging.Value;
        if (overrides.EnableComponentTimingOverride is { HasOverride: true } componentTiming)
            EnableComponentTiming = componentTiming.Value;
        if (overrides.EnableRenderStatisticsTrackingOverride is { HasOverride: true } stats)
            EnableRenderStatisticsTracking = stats.Value;
        if (overrides.EnableGpuRenderPipelineProfilingOverride is { HasOverride: true } gpuProfiling)
            EnableGpuRenderPipelineProfiling = gpuProfiling.Value;
        if (overrides.EnableThreadAllocationTrackingOverride is { HasOverride: true } allocations)
            EnableThreadAllocationTracking = allocations.Value;
        if (overrides.EnableMainThreadInvokeDiagnosticsOverride is { HasOverride: true } invokes)
            EnableMainThreadInvokeDiagnostics = invokes.Value;
        if (overrides.EnableUdpSendingOverride is { HasOverride: true } udp)
            EnableUdpSending = udp.Value;
        if (overrides.StartExternalProfilerOnStartupOverride is { HasOverride: true } external)
            StartExternalProfilerOnStartup = external.Value;
        if (overrides.CpuTimingDisplayModeOverride is { HasOverride: true } cpuMode)
            CpuTimingDisplayMode = cpuMode.Value;
        if (overrides.GpuTimingDisplayModeOverride is { HasOverride: true } gpuMode)
            GpuTimingDisplayMode = gpuMode.Value;
    }
}
