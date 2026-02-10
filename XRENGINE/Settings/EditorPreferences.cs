using MemoryPack;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Colors;
using XREngine.Data.Profiling;

namespace XREngine
{
    /// <summary>
    /// Editor-only preferences stored per project (e.g., UI theme and editor viewport behavior).
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class EditorPreferences : XRAsset
    {
        public EditorPreferences()
        {
            AttachSubSettings(_theme, _debug);
        }

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
        [DisplayName("Theme")]
        [Description("Theme and color customization for editor visuals.")]
        public EditorThemeSettings Theme
        {
            get => _theme;
            set => SetField(ref _theme, value ?? new EditorThemeSettings());
        }

        [Category("Debug")]
        [DisplayName("Debug")]
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
        [DisplayName("Viewport Presentation Mode")]
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
        [DisplayName("Scene Panel Resize Debounce (ms)")]
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
        [DisplayName("MCP Server Enabled")]
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
        [DisplayName("MCP Server Port")]
        [Description("The port number for the MCP server (default: 5467).")]
        public int McpServerPort
        {
            get => _mcpServerPort;
            set => SetField(ref _mcpServerPort, Math.Max(1, Math.Min(65535, value)));
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(Theme))
            {
                if (prev is EditorThemeSettings previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is EditorThemeSettings current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }

            if (propName == nameof(Debug))
            {
                if (prev is EditorDebugOptions previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is EditorDebugOptions current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }
        }

        private void AttachSubSettings(EditorThemeSettings? theme, EditorDebugOptions? debug)
        {
            if (theme is not null)
                theme.PropertyChanged += HandleSubSettingsChanged;

            if (debug is not null)
                debug.PropertyChanged += HandleSubSettingsChanged;
        }

        private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!IsDirty)
                MarkDirty();
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

        // Console log category colors
        private ColorF4 _consoleGeneralColor = new ColorF4(0.9f, 0.9f, 0.9f, 1.0f);    // White/Gray
        private ColorF4 _consoleRenderingColor = new ColorF4(0.4f, 0.8f, 1.0f, 1.0f);  // Light Blue
        private ColorF4 _consoleOpenGLColor = new ColorF4(0.4f, 1.0f, 0.4f, 1.0f);     // Light Green
        private ColorF4 _consolePhysicsColor = new ColorF4(1.0f, 0.8f, 0.4f, 1.0f);    // Orange
        private ColorF4 _consoleAnimationColor = new ColorF4(1.0f, 0.6f, 0.8f, 1.0f);  // Pink
        private ColorF4 _consoleUIColor = new ColorF4(0.8f, 0.6f, 1.0f, 1.0f);         // Purple

        [Category("Theme")]
        [DisplayName("Theme Name")]
        [Description("Name of the editor theme preset to apply.")]
        public string ThemeName
        {
            get => _themeName;
            set => SetField(ref _themeName, value ?? "Dark");
        }

        [Category("Theme")]
        [DisplayName("Quadtree Intersected Bounds Color")]
        [Description("The color used to represent quadtree intersected bounds in the editor.")]
        public ColorF4 QuadtreeIntersectedBoundsColor
        {
            get => _quadtreeIntersectedBoundsColor;
            set => SetField(ref _quadtreeIntersectedBoundsColor, value);
        }

        [Category("Theme")]
        [DisplayName("Quadtree Contained Bounds Color")]
        [Description("The color used to represent quadtree contained bounds in the editor.")]
        public ColorF4 QuadtreeContainedBoundsColor
        {
            get => _quadtreeContainedBoundsColor;
            set => SetField(ref _quadtreeContainedBoundsColor, value);
        }

        [Category("Theme")]
        [DisplayName("Octree Intersected Bounds Color")]
        [Description("The color used to represent octree intersected bounds in the editor.")]
        public ColorF4 OctreeIntersectedBoundsColor
        {
            get => _octreeIntersectedBoundsColor;
            set => SetField(ref _octreeIntersectedBoundsColor, value);
        }

        [Category("Theme")]
        [DisplayName("Octree Contained Bounds Color")]
        [Description("The color used to represent octree contained bounds in the editor.")]
        public ColorF4 OctreeContainedBoundsColor
        {
            get => _octreeContainedBoundsColor;
            set => SetField(ref _octreeContainedBoundsColor, value);
        }

        [Category("Theme")]
        [DisplayName("2D Bounds Color")]
        [Description("The color used to represent 2D bounds in the editor.")]
        public ColorF4 Bounds2DColor
        {
            get => _bounds2DColor;
            set => SetField(ref _bounds2DColor, value);
        }

        [Category("Theme")]
        [DisplayName("3D Bounds Color")]
        [Description("The color used to represent 3D bounds in the editor.")]
        public ColorF4 Bounds3DColor
        {
            get => _bounds3DColor;
            set => SetField(ref _bounds3DColor, value);
        }

        [Category("Theme")]
        [DisplayName("Transform Point Color")]
        [Description("The color used to represent transform points in the editor.")]
        public ColorF4 TransformPointColor
        {
            get => _transformPointColor;
            set => SetField(ref _transformPointColor, value);
        }

        [Category("Theme")]
        [DisplayName("Transform Line Color")]
        [Description("The color used to represent transform lines in the editor.")]
        public ColorF4 TransformLineColor
        {
            get => _transformLineColor;
            set => SetField(ref _transformLineColor, value);
        }

        [Category("Theme")]
        [DisplayName("Transform Capsule Color")]
        [Description("The color used to represent transform capsules in the editor.")]
        public ColorF4 TransformCapsuleColor
        {
            get => _transformCapsuleColor;
            set => SetField(ref _transformCapsuleColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console General Color")]
        [Description("The color used for General log entries in the console.")]
        public ColorF4 ConsoleGeneralColor
        {
            get => _consoleGeneralColor;
            set => SetField(ref _consoleGeneralColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console Rendering Color")]
        [Description("The color used for Rendering log entries in the console.")]
        public ColorF4 ConsoleRenderingColor
        {
            get => _consoleRenderingColor;
            set => SetField(ref _consoleRenderingColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console OpenGL Color")]
        [Description("The color used for OpenGL log entries in the console.")]
        public ColorF4 ConsoleOpenGLColor
        {
            get => _consoleOpenGLColor;
            set => SetField(ref _consoleOpenGLColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console Physics Color")]
        [Description("The color used for Physics log entries in the console.")]
        public ColorF4 ConsolePhysicsColor
        {
            get => _consolePhysicsColor;
            set => SetField(ref _consolePhysicsColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console Animation Color")]
        [Description("The color used for Animation log entries in the console.")]
        public ColorF4 ConsoleAnimationColor
        {
            get => _consoleAnimationColor;
            set => SetField(ref _consoleAnimationColor, value);
        }

        [Category("Console Colors")]
        [DisplayName("Console UI Color")]
        [Description("The color used for UI log entries in the console.")]
        public ColorF4 ConsoleUIColor
        {
            get => _consoleUIColor;
            set => SetField(ref _consoleUIColor, value);
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
            ConsoleGeneralColor = source.ConsoleGeneralColor;
            ConsoleRenderingColor = source.ConsoleRenderingColor;
            ConsoleOpenGLColor = source.ConsoleOpenGLColor;
            ConsolePhysicsColor = source.ConsolePhysicsColor;
            ConsoleAnimationColor = source.ConsoleAnimationColor;
            ConsoleUIColor = source.ConsoleUIColor;
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

            if (overrides.ConsoleGeneralColorOverride is { HasOverride: true } cgOverride)
                ConsoleGeneralColor = cgOverride.Value;

            if (overrides.ConsoleRenderingColorOverride is { HasOverride: true } crOverride)
                ConsoleRenderingColor = crOverride.Value;

            if (overrides.ConsoleOpenGLColorOverride is { HasOverride: true } coOverride)
                ConsoleOpenGLColor = coOverride.Value;

            if (overrides.ConsolePhysicsColorOverride is { HasOverride: true } cpOverride)
                ConsolePhysicsColor = cpOverride.Value;

            if (overrides.ConsoleAnimationColorOverride is { HasOverride: true } caOverride)
                ConsoleAnimationColor = caOverride.Value;

            if (overrides.ConsoleUIColorOverride is { HasOverride: true } cuOverride)
                ConsoleUIColor = cuOverride.Value;
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
        private bool _forceGpuPassthroughCulling = false;
        private bool _allowGpuCpuFallback = false;
        private bool _enableProfilerFrameLogging = true;
        private bool _enableRenderStatisticsTracking = true;
        private bool _enableUILayoutDebugLogging = false;
        private bool _enableProfilerUdpSending = false;

        [Category("Debug")]
        [DisplayName("Render 3D Mesh Bounds")]
        [Description("If true, the engine will render the bounds of each 3D mesh.")]
        public bool RenderMesh3DBounds
        {
            get => _renderMesh3DBounds;
            set => SetField(ref _renderMesh3DBounds, value);
        }

        [Category("Debug")]
        [DisplayName("Render 2D Mesh Bounds")]
        [Description("If true, the engine will render the bounds of each UI mesh.")]
        public bool RenderMesh2DBounds
        {
            get => _renderMesh2DBounds;
            set => SetField(ref _renderMesh2DBounds, value);
        }

        [Category("Debug")]
        [DisplayName("Render Transform Debug Info")]
        [Description("If true, the engine will render all transforms in the scene as lines and points.")]
        public bool RenderTransformDebugInfo
        {
            get => _renderTransformDebugInfo;
            set => SetField(ref _renderTransformDebugInfo, value);
        }

        [Category("Debug")]
        [DisplayName("Render UI Transform Coordinate")]
        [Description("If true, the engine will render the coordinate system of UI transforms.")]
        public bool RenderUITransformCoordinate
        {
            get => _renderUITransformCoordinate;
            set => SetField(ref _renderUITransformCoordinate, value);
        }

        [Category("Debug")]
        [DisplayName("Render Transform Lines")]
        [Description("If true, the engine will render all transforms in the scene as lines.")]
        public bool RenderTransformLines
        {
            get => _renderTransformLines;
            set => SetField(ref _renderTransformLines, value);
        }

        [Category("Debug")]
        [DisplayName("Render Transform Points")]
        [Description("If true, the engine will render all transforms in the scene as points.")]
        public bool RenderTransformPoints
        {
            get => _renderTransformPoints;
            set => SetField(ref _renderTransformPoints, value);
        }

        [Category("Debug")]
        [DisplayName("Render Transform Capsules")]
        [Description("If true, the engine will render capsules around transforms for debugging purposes.")]
        public bool RenderTransformCapsules
        {
            get => _renderTransformCapsules;
            set => SetField(ref _renderTransformCapsules, value);
        }

        [Category("Debug")]
        [DisplayName("Visualize Directional Light Volumes")]
        [Description("If true, the engine will visualize the volumes of directional lights.")]
        public bool VisualizeDirectionalLightVolumes
        {
            get => _visualizeDirectionalLightVolumes;
            set => SetField(ref _visualizeDirectionalLightVolumes, value);
        }

        [Category("Debug")]
        [DisplayName("Preview 3D World Octree")]
        [Description("If true, the engine will render the octree for the 3D world.")]
        public bool Preview3DWorldOctree
        {
            get => _preview3DWorldOctree;
            set => SetField(ref _preview3DWorldOctree, value);
        }

        [Category("Debug")]
        [DisplayName("Preview 2D World Quadtree")]
        [Description("If true, the engine will render the quadtree for the 2D world.")]
        public bool Preview2DWorldQuadtree
        {
            get => _preview2DWorldQuadtree;
            set => SetField(ref _preview2DWorldQuadtree, value);
        }

        [Category("Debug")]
        [DisplayName("Preview Traces")]
        [Description("If true, the engine will render physics traces.")]
        public bool PreviewTraces
        {
            get => _previewTraces;
            set => SetField(ref _previewTraces, value);
        }

        [Category("Debug")]
        [DisplayName("Visualize Transform ID")]
        [Description("If true, the engine will visualize the per-draw TransformId buffer as a false-color output.")]
        public bool VisualizeTransformId
        {
            get => _visualizeTransformId;
            set => SetField(ref _visualizeTransformId, value);
        }

        [Category("Debug")]
        [DisplayName("Render Culling Volumes")]
        [Description("If true, culling volumes will be rendered for debugging purposes.")]
        public bool RenderCullingVolumes
        {
            get => _renderCullingVolumes;
            set => SetField(ref _renderCullingVolumes, value);
        }

        [Category("Debug")]
        [DisplayName("Debug Text Max Lifespan")]
        [Description("How long a cache object for text rendering should exist without receiving any further updates.")]
        public float DebugTextMaxLifespan
        {
            get => _debugTextMaxLifespan;
            set => SetField(ref _debugTextMaxLifespan, value);
        }

        [Category("Debug")]
        [DisplayName("Render Light Probe Tetrahedra")]
        [Description("If true, renders light probe tetrahedra for visualization.")]
        public bool RenderLightProbeTetrahedra
        {
            get => _renderLightProbeTetrahedra;
            set => SetField(ref _renderLightProbeTetrahedra, value);
        }

        [Category("Debug")]
        [DisplayName("Use Debug Opaque Pipeline")]
        [Description("Whether to use the debug opaque render pipeline.")]
        public bool UseDebugOpaquePipeline
        {
            get => _useDebugOpaquePipeline;
            set => SetField(ref _useDebugOpaquePipeline, value);
        }

        [Category("GPU Rendering")]
        [DisplayName("Force GPU Passthrough Culling")]
        [Description("When true, GPU culling uses passthrough mode (copies all commands without frustum culling). When false, uses actual GPU frustum culling.")]
        public bool ForceGpuPassthroughCulling
        {
            get => _forceGpuPassthroughCulling;
            set => SetField(ref _forceGpuPassthroughCulling, value);
        }

        [Category("GPU Rendering")]
        [DisplayName("Allow GPU CPU Fallback")]
        [Description("When true, allows CPU fallback rendering when GPU indirect rendering produces zero visible commands. Useful for debugging GPU rendering issues.")]
        public bool AllowGpuCpuFallback
        {
            get => _allowGpuCpuFallback;
            set => SetField(ref _allowGpuCpuFallback, value);
        }

        [Category("Profiling")]
        [DisplayName("Enable Thread Allocation Tracking")]
        [Description("Tracks GC allocations per engine thread/tick using GC.GetAllocatedBytesForCurrentThread(). Used by the Profiler panel.")]
        public bool EnableThreadAllocationTracking
        {
            get => _enableThreadAllocationTracking;
            set => SetField(ref _enableThreadAllocationTracking, value);
        }

        [Category("Profiling")]
        [DisplayName("Enable Profiler Frame Logging")]
        [Description("When enabled, the code profiler records method timings for the Profiler panel. Disable to reduce overhead in hot paths.")]
        public bool EnableProfilerFrameLogging
        {
            get => _enableProfilerFrameLogging;
            set
            {
                if (SetField(ref _enableProfilerFrameLogging, value))
                    Engine.Profiler.EnableFrameLogging = value;
            }
        }

        [Category("Profiling")]
        [DisplayName("Enable Render Statistics Tracking")]
        [Description("When enabled, tracks per-frame rendering statistics (draw calls, triangles, etc.). Disable to reduce overhead.")]
        public bool EnableRenderStatisticsTracking
        {
            get => _enableRenderStatisticsTracking;
            set
            {
                if (SetField(ref _enableRenderStatisticsTracking, value))
                    Engine.Rendering.Stats.EnableTracking = value;
            }
        }

        [Category("Debug")]
        [DisplayName("Enable UI Layout Debug Logging")]
        [Description("When enabled, logs verbose UI layout system measure/arrange passes to the UI log category.")]
        public bool EnableUILayoutDebugLogging
        {
            get => _enableUILayoutDebugLogging;
            set
            {
                if (SetField(ref _enableUILayoutDebugLogging, value))
                    XREngine.Rendering.UI.UILayoutSystem.EnableDebugLogging = value;
            }
        }

        [Category("Profiling")]
        [DisplayName("Enable Profiler UDP Sending")]
        [Description("When enabled, sends profiler telemetry over UDP to an external XREngine.Profiler instance on localhost. When disabled, zero overhead (no thread, no socket).")]
        public bool EnableProfilerUdpSending
        {
            get => _enableProfilerUdpSending;
            set
            {
                if (SetField(ref _enableProfilerUdpSending, value))
                {
                    if (value)
                    {
                        Engine.WireProfilerSenderCollectors();
                        UdpProfilerSender.Start();
                    }
                    else
                    {
                        UdpProfilerSender.Stop();
                    }
                }
            }
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
            ForceGpuPassthroughCulling = source.ForceGpuPassthroughCulling;
            AllowGpuCpuFallback = source.AllowGpuCpuFallback;
            EnableProfilerFrameLogging = source.EnableProfilerFrameLogging;
            EnableRenderStatisticsTracking = source.EnableRenderStatisticsTracking;
            EnableUILayoutDebugLogging = source.EnableUILayoutDebugLogging;
            EnableProfilerUdpSending = source.EnableProfilerUdpSending;
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
            if (overrides.ForceGpuPassthroughCullingOverride is { HasOverride: true } passthrough)
                ForceGpuPassthroughCulling = passthrough.Value;
            if (overrides.AllowGpuCpuFallbackOverride is { HasOverride: true } cpuFallback)
                AllowGpuCpuFallback = cpuFallback.Value;
            if (overrides.EnableProfilerFrameLoggingOverride is { HasOverride: true } profilerLogging)
                EnableProfilerFrameLogging = profilerLogging.Value;
            if (overrides.EnableRenderStatisticsTrackingOverride is { HasOverride: true } statsTracking)
                EnableRenderStatisticsTracking = statsTracking.Value;
            if (overrides.EnableUILayoutDebugLoggingOverride is { HasOverride: true } uiLayoutDebug)
                EnableUILayoutDebugLogging = uiLayoutDebug.Value;
            if (overrides.EnableProfilerUdpSendingOverride is { HasOverride: true } profilerUdp)
                EnableProfilerUdpSending = profilerUdp.Value;
        }
    }
}
