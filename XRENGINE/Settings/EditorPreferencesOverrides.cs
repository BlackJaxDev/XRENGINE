using MemoryPack;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Colors;

namespace XREngine
{
    /// <summary>
    /// Project/sandbox-local overrides for editor preferences.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class EditorPreferencesOverrides : OverrideableSettingsAssetBase
    {
        public EditorPreferencesOverrides()
        {
            AttachSubSettings(_theme, _debug);
            TrackOverrideableSettings();
        }

        private EditorThemeOverrides _theme = new();
        private EditorDebugOverrides _debug = new();
        private OverrideableSetting<EditorPreferences.EViewportPresentationMode> _viewportPresentationModeOverride = new();
        private OverrideableSetting<int> _scenePanelResizeDebounceMsOverride = new();
        private OverrideableSetting<bool> _mcpServerEnabledOverride = new();
        private OverrideableSetting<int> _mcpServerPortOverride = new();

        [Category("Theme Overrides")]
        [Description("Overrides for editor theme and colors.")]
        public EditorThemeOverrides Theme
        {
            get => _theme;
            set => SetField(ref _theme, value ?? new EditorThemeOverrides());
        }

        [Category("Debug Overrides")]
        [Description("Overrides for editor debug visualization options.")]
        public EditorDebugOverrides Debug
        {
            get => _debug;
            set => SetField(ref _debug, value ?? new EditorDebugOverrides());
        }

        [Category("Viewport Overrides")]
        [Description("Override for editor viewport presentation mode.")]
        public OverrideableSetting<EditorPreferences.EViewportPresentationMode> ViewportPresentationModeOverride
        {
            get => _viewportPresentationModeOverride;
            set => SetField(ref _viewportPresentationModeOverride, value ?? new());
        }

        [Category("Viewport Overrides")]
        [Description("Override for scene panel resize debounce in milliseconds.")]
        public OverrideableSetting<int> ScenePanelResizeDebounceMsOverride
        {
            get => _scenePanelResizeDebounceMsOverride;
            set => SetField(ref _scenePanelResizeDebounceMsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP server enabled state.")]
        public OverrideableSetting<bool> McpServerEnabledOverride
        {
            get => _mcpServerEnabledOverride;
            set => SetField(ref _mcpServerEnabledOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP server port.")]
        public OverrideableSetting<int> McpServerPortOverride
        {
            get => _mcpServerPortOverride;
            set => SetField(ref _mcpServerPortOverride, value ?? new());
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(Theme))
            {
                if (prev is EditorThemeOverrides previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is EditorThemeOverrides current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }

            if (propName == nameof(Debug))
            {
                if (prev is EditorDebugOverrides previous)
                    previous.PropertyChanged -= HandleSubSettingsChanged;

                if (field is EditorDebugOverrides current)
                    current.PropertyChanged += HandleSubSettingsChanged;
            }

        }

        private void AttachSubSettings(EditorThemeOverrides? theme, EditorDebugOverrides? debug)
        {
            theme?.PropertyChanged += HandleSubSettingsChanged;
            debug?.PropertyChanged += HandleSubSettingsChanged;
        }

        private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (!IsDirty)
                MarkDirty();
        }

        protected override void OnOverrideableSettingChanged(string propertyName, IOverrideableSetting setting, IXRPropertyChangedEventArgs e)
        {
            base.OnOverrideableSettingChanged(propertyName, setting, e);

            if (!IsDirty)
                MarkDirty();
        }
    }

    [Serializable]
    [MemoryPackable]
    public partial class EditorThemeOverrides : OverrideableSettingsOwnerBase
    {
        public EditorThemeOverrides()
        {
            TrackOverrideableSettings();
        }

        private OverrideableSetting<string> _themeNameOverride = new();
        private OverrideableSetting<ColorF4> _quadtreeIntersectedBoundsColorOverride = new();
        private OverrideableSetting<ColorF4> _quadtreeContainedBoundsColorOverride = new();
        private OverrideableSetting<ColorF4> _octreeIntersectedBoundsColorOverride = new();
        private OverrideableSetting<ColorF4> _octreeContainedBoundsColorOverride = new();
        private OverrideableSetting<ColorF4> _bounds2DColorOverride = new();
        private OverrideableSetting<ColorF4> _bounds3DColorOverride = new();
        private OverrideableSetting<ColorF4> _transformPointColorOverride = new();
        private OverrideableSetting<ColorF4> _transformLineColorOverride = new();
        private OverrideableSetting<ColorF4> _transformCapsuleColorOverride = new();

        [Category("Theme Overrides")]
        [Description("Override for theme preset name.")]
        public OverrideableSetting<string> ThemeNameOverride
        {
            get => _themeNameOverride;
            set => SetField(ref _themeNameOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> QuadtreeIntersectedBoundsColorOverride
        {
            get => _quadtreeIntersectedBoundsColorOverride;
            set => SetField(ref _quadtreeIntersectedBoundsColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> QuadtreeContainedBoundsColorOverride
        {
            get => _quadtreeContainedBoundsColorOverride;
            set => SetField(ref _quadtreeContainedBoundsColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> OctreeIntersectedBoundsColorOverride
        {
            get => _octreeIntersectedBoundsColorOverride;
            set => SetField(ref _octreeIntersectedBoundsColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> OctreeContainedBoundsColorOverride
        {
            get => _octreeContainedBoundsColorOverride;
            set => SetField(ref _octreeContainedBoundsColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> Bounds2DColorOverride
        {
            get => _bounds2DColorOverride;
            set => SetField(ref _bounds2DColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> Bounds3DColorOverride
        {
            get => _bounds3DColorOverride;
            set => SetField(ref _bounds3DColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> TransformPointColorOverride
        {
            get => _transformPointColorOverride;
            set => SetField(ref _transformPointColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> TransformLineColorOverride
        {
            get => _transformLineColorOverride;
            set => SetField(ref _transformLineColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> TransformCapsuleColorOverride
        {
            get => _transformCapsuleColorOverride;
            set => SetField(ref _transformCapsuleColorOverride, value ?? new());
        }
    }

    [Serializable]
    [MemoryPackable]
    public partial class EditorDebugOverrides : OverrideableSettingsOwnerBase
    {
        public EditorDebugOverrides()
        {
            TrackOverrideableSettings();
        }

        private OverrideableSetting<bool> _renderMesh3DBoundsOverride = new();
        private OverrideableSetting<bool> _renderMesh2DBoundsOverride = new();
        private OverrideableSetting<bool> _renderTransformDebugInfoOverride = new();
        private OverrideableSetting<bool> _renderUITransformCoordinateOverride = new();
        private OverrideableSetting<bool> _renderTransformLinesOverride = new();
        private OverrideableSetting<bool> _renderTransformPointsOverride = new();
        private OverrideableSetting<bool> _renderTransformCapsulesOverride = new();
        private OverrideableSetting<bool> _visualizeDirectionalLightVolumesOverride = new();
        private OverrideableSetting<bool> _preview3DWorldOctreeOverride = new();
        private OverrideableSetting<bool> _preview2DWorldQuadtreeOverride = new();
        private OverrideableSetting<bool> _previewTracesOverride = new();
        private OverrideableSetting<bool> _renderCullingVolumesOverride = new();
        private OverrideableSetting<bool> _visualizeTransformIdOverride = new();
        private OverrideableSetting<bool> _renderLightProbeTetrahedraOverride = new();
        private OverrideableSetting<float> _debugTextMaxLifespanOverride = new();
        private OverrideableSetting<bool> _enableThreadAllocationTrackingOverride = new();
        private OverrideableSetting<bool> _useDebugOpaquePipelineOverride = new();
        private OverrideableSetting<bool> _forceGpuPassthroughCullingOverride = new();
        private OverrideableSetting<bool> _allowGpuCpuFallbackOverride = new();
        private OverrideableSetting<bool> _enableProfilerFrameLoggingOverride = new();
        private OverrideableSetting<bool> _enableRenderStatisticsTrackingOverride = new();

        public OverrideableSetting<bool> RenderMesh3DBoundsOverride
        {
            get => _renderMesh3DBoundsOverride;
            set => SetField(ref _renderMesh3DBoundsOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderMesh2DBoundsOverride
        {
            get => _renderMesh2DBoundsOverride;
            set => SetField(ref _renderMesh2DBoundsOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderTransformDebugInfoOverride
        {
            get => _renderTransformDebugInfoOverride;
            set => SetField(ref _renderTransformDebugInfoOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderUITransformCoordinateOverride
        {
            get => _renderUITransformCoordinateOverride;
            set => SetField(ref _renderUITransformCoordinateOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderTransformLinesOverride
        {
            get => _renderTransformLinesOverride;
            set => SetField(ref _renderTransformLinesOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderTransformPointsOverride
        {
            get => _renderTransformPointsOverride;
            set => SetField(ref _renderTransformPointsOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderTransformCapsulesOverride
        {
            get => _renderTransformCapsulesOverride;
            set => SetField(ref _renderTransformCapsulesOverride, value ?? new());
        }

        public OverrideableSetting<bool> VisualizeDirectionalLightVolumesOverride
        {
            get => _visualizeDirectionalLightVolumesOverride;
            set => SetField(ref _visualizeDirectionalLightVolumesOverride, value ?? new());
        }

        public OverrideableSetting<bool> Preview3DWorldOctreeOverride
        {
            get => _preview3DWorldOctreeOverride;
            set => SetField(ref _preview3DWorldOctreeOverride, value ?? new());
        }

        public OverrideableSetting<bool> Preview2DWorldQuadtreeOverride
        {
            get => _preview2DWorldQuadtreeOverride;
            set => SetField(ref _preview2DWorldQuadtreeOverride, value ?? new());
        }

        public OverrideableSetting<bool> PreviewTracesOverride
        {
            get => _previewTracesOverride;
            set => SetField(ref _previewTracesOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderCullingVolumesOverride
        {
            get => _renderCullingVolumesOverride;
            set => SetField(ref _renderCullingVolumesOverride, value ?? new());
        }

        public OverrideableSetting<bool> VisualizeTransformIdOverride
        {
            get => _visualizeTransformIdOverride;
            set => SetField(ref _visualizeTransformIdOverride, value ?? new());
        }

        public OverrideableSetting<bool> RenderLightProbeTetrahedraOverride
        {
            get => _renderLightProbeTetrahedraOverride;
            set => SetField(ref _renderLightProbeTetrahedraOverride, value ?? new());
        }

        public OverrideableSetting<float> DebugTextMaxLifespanOverride
        {
            get => _debugTextMaxLifespanOverride;
            set => SetField(ref _debugTextMaxLifespanOverride, value ?? new());
        }

        public OverrideableSetting<bool> EnableThreadAllocationTrackingOverride
        {
            get => _enableThreadAllocationTrackingOverride;
            set => SetField(ref _enableThreadAllocationTrackingOverride, value ?? new());
        }

        public OverrideableSetting<bool> UseDebugOpaquePipelineOverride
        {
            get => _useDebugOpaquePipelineOverride;
            set => SetField(ref _useDebugOpaquePipelineOverride, value ?? new());
        }

        public OverrideableSetting<bool> ForceGpuPassthroughCullingOverride
        {
            get => _forceGpuPassthroughCullingOverride;
            set => SetField(ref _forceGpuPassthroughCullingOverride, value ?? new());
        }

        public OverrideableSetting<bool> AllowGpuCpuFallbackOverride
        {
            get => _allowGpuCpuFallbackOverride;
            set => SetField(ref _allowGpuCpuFallbackOverride, value ?? new());
        }

        public OverrideableSetting<bool> EnableProfilerFrameLoggingOverride
        {
            get => _enableProfilerFrameLoggingOverride;
            set => SetField(ref _enableProfilerFrameLoggingOverride, value ?? new());
        }

        public OverrideableSetting<bool> EnableRenderStatisticsTrackingOverride
        {
            get => _enableRenderStatisticsTrackingOverride;
            set => SetField(ref _enableRenderStatisticsTrackingOverride, value ?? new());
        }
    }
}
