using MemoryPack;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Colors;

namespace XREngine
{
    /// <summary>
    /// Editor-only preferences stored per project (e.g., UI theme and editor viewport behavior).
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class EditorPreferences : XRAsset
    {
        public enum EViewportPresentationMode
        {
            FullViewportBehindImGuiUI,
            UseViewportPanel,
        }

        private EViewportPresentationMode _viewportPresentationMode = EViewportPresentationMode.FullViewportBehindImGuiUI;
        private int _scenePanelResizeDebounceMs = 0;
        private EditorThemeSettings _theme = new();
        private EditorDebugOptions _debug = new();
        private bool _mcpServerEnabled = false;
        private int _mcpServerPort = 5467;

        [Category("Theme")]
        [Description("Theme and color customization for editor visuals.")]
        public EditorThemeSettings Theme
        {
            get => _theme;
            set => SetField(ref _theme, value ?? new EditorThemeSettings());
        }

        [Category("Debug")]
        [Description("Editor-only debug visualization options.")]
        public EditorDebugOptions Debug
        {
            get => _debug;
            set => SetField(ref _debug, value ?? new EditorDebugOptions());
        }

        /// <summary>
        /// Controls how the main world viewport is presented when Dear ImGui is active.
        /// </summary>
        [Category("Viewport")]
        [Description("Controls whether the world renders full-screen behind ImGui UI, or is constrained to the docked Scene panel.")]
        public EViewportPresentationMode ViewportPresentationMode
        {
            get => _viewportPresentationMode;
            set => SetField(ref _viewportPresentationMode, value);
        }

        /// <summary>
        /// Debounce for Scene panel resizes (0 disables).
        /// </summary>
        [Category("Viewport")]
        [Description("Debounce in milliseconds for Scene panel resizes (0 disables).")]
        public int ScenePanelResizeDebounceMs
        {
            get => _scenePanelResizeDebounceMs;
            set => SetField(ref _scenePanelResizeDebounceMs, Math.Max(0, value));
        }

        /// <summary>
        /// Whether the MCP (Model Context Protocol) server is enabled.
        /// When enabled, AI assistants and external tools can interact with the editor via HTTP.
        /// </summary>
        [Category("MCP Server")]
        [Description("Enable the MCP server to allow AI assistants and external tools to interact with the editor.")]
        public bool McpServerEnabled
        {
            get => _mcpServerEnabled;
            set => SetField(ref _mcpServerEnabled, value);
        }

        /// <summary>
        /// The port number for the MCP server.
        /// </summary>
        [Category("MCP Server")]
        [Description("The port number for the MCP server (default: 5467).")]
        public int McpServerPort
        {
            get => _mcpServerPort;
            set => SetField(ref _mcpServerPort, Math.Max(1, Math.Min(65535, value)));
        }

        public void CopyFrom(EditorPreferences source)
        {
            if (source is null)
                return;

            Theme.CopyFrom(source.Theme);
            Debug.CopyFrom(source.Debug);
            ViewportPresentationMode = source.ViewportPresentationMode;
            ScenePanelResizeDebounceMs = source.ScenePanelResizeDebounceMs;
            McpServerEnabled = source.McpServerEnabled;
            McpServerPort = source.McpServerPort;
        }

        public void ApplyOverrides(EditorPreferencesOverrides overrides)
        {
            if (overrides is null)
                return;

            Theme.ApplyOverrides(overrides.Theme);
            Debug.ApplyOverrides(overrides.Debug);

            if (overrides.ViewportPresentationModeOverride is { HasOverride: true } vpOverride)
                ViewportPresentationMode = vpOverride.Value;

            if (overrides.ScenePanelResizeDebounceMsOverride is { HasOverride: true } debounceOverride)
                ScenePanelResizeDebounceMs = Math.Max(0, debounceOverride.Value);

            if (overrides.McpServerEnabledOverride is { HasOverride: true } mcpEnabledOverride)
                McpServerEnabled = mcpEnabledOverride.Value;

            if (overrides.McpServerPortOverride is { HasOverride: true } mcpPortOverride)
                McpServerPort = Math.Max(1, Math.Min(65535, mcpPortOverride.Value));
        }
    }

    [Serializable]
    [MemoryPackable]
    public partial class EditorThemeSettings : XRBase
    {
        private string _themeName = "Dark";
        private ColorF4 _quadtreeIntersectedBoundsColor = ColorF4.LightGray;
        private ColorF4 _quadtreeContainedBoundsColor = ColorF4.Yellow;
        private ColorF4 _octreeIntersectedBoundsColor = ColorF4.LightGray;
        private ColorF4 _octreeContainedBoundsColor = ColorF4.Yellow;
        private ColorF4 _bounds2DColor = ColorF4.LightLavender;
        private ColorF4 _bounds3DColor = ColorF4.LightLavender;
        private ColorF4 _transformPointColor = ColorF4.Orange;
        private ColorF4 _transformLineColor = ColorF4.LightRed;
        private ColorF4 _transformCapsuleColor = ColorF4.LightOrange;

        [Category("Theme")]
        [Description("Name of the editor theme preset to apply.")]
        public string ThemeName
        {
            get => _themeName;
            set => SetField(ref _themeName, value ?? "Dark");
        }

        [Category("Theme")]
        [Description("The color used to represent quadtree intersected bounds in the editor.")]
        public ColorF4 QuadtreeIntersectedBoundsColor
        {
            get => _quadtreeIntersectedBoundsColor;
            set => SetField(ref _quadtreeIntersectedBoundsColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent quadtree contained bounds in the editor.")]
        public ColorF4 QuadtreeContainedBoundsColor
        {
            get => _quadtreeContainedBoundsColor;
            set => SetField(ref _quadtreeContainedBoundsColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent octree intersected bounds in the editor.")]
        public ColorF4 OctreeIntersectedBoundsColor
        {
            get => _octreeIntersectedBoundsColor;
            set => SetField(ref _octreeIntersectedBoundsColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent octree contained bounds in the editor.")]
        public ColorF4 OctreeContainedBoundsColor
        {
            get => _octreeContainedBoundsColor;
            set => SetField(ref _octreeContainedBoundsColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent 2D bounds in the editor.")]
        public ColorF4 Bounds2DColor
        {
            get => _bounds2DColor;
            set => SetField(ref _bounds2DColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent 3D bounds in the editor.")]
        public ColorF4 Bounds3DColor
        {
            get => _bounds3DColor;
            set => SetField(ref _bounds3DColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent transform points in the editor.")]
        public ColorF4 TransformPointColor
        {
            get => _transformPointColor;
            set => SetField(ref _transformPointColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent transform lines in the editor.")]
        public ColorF4 TransformLineColor
        {
            get => _transformLineColor;
            set => SetField(ref _transformLineColor, value);
        }

        [Category("Theme")]
        [Description("The color used to represent transform capsules in the editor.")]
        public ColorF4 TransformCapsuleColor
        {
            get => _transformCapsuleColor;
            set => SetField(ref _transformCapsuleColor, value);
        }

        public void CopyFrom(EditorThemeSettings source)
        {
            if (source is null)
                return;

            ThemeName = source.ThemeName;
            QuadtreeIntersectedBoundsColor = source.QuadtreeIntersectedBoundsColor;
            QuadtreeContainedBoundsColor = source.QuadtreeContainedBoundsColor;
            OctreeIntersectedBoundsColor = source.OctreeIntersectedBoundsColor;
            OctreeContainedBoundsColor = source.OctreeContainedBoundsColor;
            Bounds2DColor = source.Bounds2DColor;
            Bounds3DColor = source.Bounds3DColor;
            TransformPointColor = source.TransformPointColor;
            TransformLineColor = source.TransformLineColor;
            TransformCapsuleColor = source.TransformCapsuleColor;
        }

        public void ApplyOverrides(EditorThemeOverrides overrides)
        {
            if (overrides is null)
                return;

            if (overrides.ThemeNameOverride is { HasOverride: true } themeOverride)
                ThemeName = themeOverride.Value ?? ThemeName;

            if (overrides.QuadtreeIntersectedBoundsColorOverride is { HasOverride: true } qiOverride)
                QuadtreeIntersectedBoundsColor = qiOverride.Value;

            if (overrides.QuadtreeContainedBoundsColorOverride is { HasOverride: true } qcOverride)
                QuadtreeContainedBoundsColor = qcOverride.Value;

            if (overrides.OctreeIntersectedBoundsColorOverride is { HasOverride: true } oiOverride)
                OctreeIntersectedBoundsColor = oiOverride.Value;

            if (overrides.OctreeContainedBoundsColorOverride is { HasOverride: true } ocOverride)
                OctreeContainedBoundsColor = ocOverride.Value;

            if (overrides.Bounds2DColorOverride is { HasOverride: true } b2Override)
                Bounds2DColor = b2Override.Value;

            if (overrides.Bounds3DColorOverride is { HasOverride: true } b3Override)
                Bounds3DColor = b3Override.Value;

            if (overrides.TransformPointColorOverride is { HasOverride: true } tpOverride)
                TransformPointColor = tpOverride.Value;

            if (overrides.TransformLineColorOverride is { HasOverride: true } tlOverride)
                TransformLineColor = tlOverride.Value;

            if (overrides.TransformCapsuleColorOverride is { HasOverride: true } tcOverride)
                TransformCapsuleColor = tcOverride.Value;
        }
    }

    [Serializable]
    [MemoryPackable]
    public partial class EditorDebugOptions : XRBase
    {
        private bool _renderMesh3DBounds;
        private bool _renderMesh2DBounds;
        private bool _renderTransformDebugInfo;
        private bool _renderUITransformCoordinate;
        private bool _renderTransformLines;
        private bool _renderTransformPoints;
        private bool _renderTransformCapsules;
        private bool _visualizeDirectionalLightVolumes;
        private bool _preview3DWorldOctree;
        private bool _preview2DWorldQuadtree;
        private bool _previewTraces;
        private bool _renderCullingVolumes;
        private bool _visualizeTransformId;
        private bool _renderLightProbeTetrahedra = true;
        private float _debugTextMaxLifespan;
        private bool _enableThreadAllocationTracking;
        private bool _useDebugOpaquePipeline;

        [Category("Debug")]
        [Description("If true, the engine will render the bounds of each 3D mesh.")]
        public bool RenderMesh3DBounds
        {
            get => _renderMesh3DBounds;
            set => SetField(ref _renderMesh3DBounds, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render the bounds of each UI mesh.")]
        public bool RenderMesh2DBounds
        {
            get => _renderMesh2DBounds;
            set => SetField(ref _renderMesh2DBounds, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render all transforms in the scene as lines and points.")]
        public bool RenderTransformDebugInfo
        {
            get => _renderTransformDebugInfo;
            set => SetField(ref _renderTransformDebugInfo, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render the coordinate system of UI transforms.")]
        public bool RenderUITransformCoordinate
        {
            get => _renderUITransformCoordinate;
            set => SetField(ref _renderUITransformCoordinate, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render all transforms in the scene as lines.")]
        public bool RenderTransformLines
        {
            get => _renderTransformLines;
            set => SetField(ref _renderTransformLines, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render all transforms in the scene as points.")]
        public bool RenderTransformPoints
        {
            get => _renderTransformPoints;
            set => SetField(ref _renderTransformPoints, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render capsules around transforms for debugging purposes.")]
        public bool RenderTransformCapsules
        {
            get => _renderTransformCapsules;
            set => SetField(ref _renderTransformCapsules, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will visualize the volumes of directional lights.")]
        public bool VisualizeDirectionalLightVolumes
        {
            get => _visualizeDirectionalLightVolumes;
            set => SetField(ref _visualizeDirectionalLightVolumes, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render the octree for the 3D world.")]
        public bool Preview3DWorldOctree
        {
            get => _preview3DWorldOctree;
            set => SetField(ref _preview3DWorldOctree, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render the quadtree for the 2D world.")]
        public bool Preview2DWorldQuadtree
        {
            get => _preview2DWorldQuadtree;
            set => SetField(ref _preview2DWorldQuadtree, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will render physics traces.")]
        public bool PreviewTraces
        {
            get => _previewTraces;
            set => SetField(ref _previewTraces, value);
        }

        [Category("Debug")]
        [Description("If true, the engine will visualize the per-draw TransformId buffer as a false-color output.")]
        public bool VisualizeTransformId
        {
            get => _visualizeTransformId;
            set => SetField(ref _visualizeTransformId, value);
        }

        [Category("Debug")]
        [Description("If true, culling volumes will be rendered for debugging purposes.")]
        public bool RenderCullingVolumes
        {
            get => _renderCullingVolumes;
            set => SetField(ref _renderCullingVolumes, value);
        }

        [Category("Debug")]
        [Description("How long a cache object for text rendering should exist without receiving any further updates.")]
        public float DebugTextMaxLifespan
        {
            get => _debugTextMaxLifespan;
            set => SetField(ref _debugTextMaxLifespan, value);
        }

        [Category("Debug")]
        [Description("If true, renders light probe tetrahedra for visualization.")]
        public bool RenderLightProbeTetrahedra
        {
            get => _renderLightProbeTetrahedra;
            set => SetField(ref _renderLightProbeTetrahedra, value);
        }

        [Category("Debug")]
        [Description("Whether to use the debug opaque render pipeline.")]
        public bool UseDebugOpaquePipeline
        {
            get => _useDebugOpaquePipeline;
            set => SetField(ref _useDebugOpaquePipeline, value);
        }

        [Category("Profiling")]
        [Description("Tracks GC allocations per engine thread/tick using GC.GetAllocatedBytesForCurrentThread(). Used by the Profiler panel.")]
        public bool EnableThreadAllocationTracking
        {
            get => _enableThreadAllocationTracking;
            set => SetField(ref _enableThreadAllocationTracking, value);
        }

        public void CopyFrom(EditorDebugOptions source)
        {
            if (source is null)
                return;

            RenderMesh3DBounds = source.RenderMesh3DBounds;
            RenderMesh2DBounds = source.RenderMesh2DBounds;
            RenderTransformDebugInfo = source.RenderTransformDebugInfo;
            RenderUITransformCoordinate = source.RenderUITransformCoordinate;
            RenderTransformLines = source.RenderTransformLines;
            RenderTransformPoints = source.RenderTransformPoints;
            RenderTransformCapsules = source.RenderTransformCapsules;
            VisualizeDirectionalLightVolumes = source.VisualizeDirectionalLightVolumes;
            Preview3DWorldOctree = source.Preview3DWorldOctree;
            Preview2DWorldQuadtree = source.Preview2DWorldQuadtree;
            PreviewTraces = source.PreviewTraces;
            RenderCullingVolumes = source.RenderCullingVolumes;
            VisualizeTransformId = source.VisualizeTransformId;
            RenderLightProbeTetrahedra = source.RenderLightProbeTetrahedra;
            DebugTextMaxLifespan = source.DebugTextMaxLifespan;
            EnableThreadAllocationTracking = source.EnableThreadAllocationTracking;
            UseDebugOpaquePipeline = source.UseDebugOpaquePipeline;
        }

        public void ApplyOverrides(EditorDebugOverrides overrides)
        {
            if (overrides is null)
                return;

            if (overrides.RenderMesh3DBoundsOverride is { HasOverride: true } mesh3d)
                RenderMesh3DBounds = mesh3d.Value;
            if (overrides.RenderMesh2DBoundsOverride is { HasOverride: true } mesh2d)
                RenderMesh2DBounds = mesh2d.Value;
            if (overrides.RenderTransformDebugInfoOverride is { HasOverride: true } transformInfo)
                RenderTransformDebugInfo = transformInfo.Value;
            if (overrides.RenderUITransformCoordinateOverride is { HasOverride: true } uiCoord)
                RenderUITransformCoordinate = uiCoord.Value;
            if (overrides.RenderTransformLinesOverride is { HasOverride: true } lines)
                RenderTransformLines = lines.Value;
            if (overrides.RenderTransformPointsOverride is { HasOverride: true } points)
                RenderTransformPoints = points.Value;
            if (overrides.RenderTransformCapsulesOverride is { HasOverride: true } capsules)
                RenderTransformCapsules = capsules.Value;
            if (overrides.VisualizeDirectionalLightVolumesOverride is { HasOverride: true } lightVolumes)
                VisualizeDirectionalLightVolumes = lightVolumes.Value;
            if (overrides.Preview3DWorldOctreeOverride is { HasOverride: true } preview3d)
                Preview3DWorldOctree = preview3d.Value;
            if (overrides.Preview2DWorldQuadtreeOverride is { HasOverride: true } preview2d)
                Preview2DWorldQuadtree = preview2d.Value;
            if (overrides.PreviewTracesOverride is { HasOverride: true } previewTraces)
                PreviewTraces = previewTraces.Value;
            if (overrides.RenderCullingVolumesOverride is { HasOverride: true } culling)
                RenderCullingVolumes = culling.Value;
            if (overrides.VisualizeTransformIdOverride is { HasOverride: true } transformId)
                VisualizeTransformId = transformId.Value;
            if (overrides.RenderLightProbeTetrahedraOverride is { HasOverride: true } tetra)
                RenderLightProbeTetrahedra = tetra.Value;
            if (overrides.DebugTextMaxLifespanOverride is { HasOverride: true } lifespan)
                DebugTextMaxLifespan = lifespan.Value;
            if (overrides.EnableThreadAllocationTrackingOverride is { HasOverride: true } alloc)
                EnableThreadAllocationTracking = alloc.Value;
            if (overrides.UseDebugOpaquePipelineOverride is { HasOverride: true } debugOpaque)
                UseDebugOpaquePipeline = debugOpaque.Value;
        }
    }
}
