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
        private OverrideableSetting<bool> _mcpServerRequireAuthOverride = new();
        private OverrideableSetting<string> _mcpServerAuthTokenOverride = new();
        private OverrideableSetting<string> _mcpServerCorsAllowlistOverride = new();
        private OverrideableSetting<int> _mcpServerMaxRequestBytesOverride = new();
        private OverrideableSetting<int> _mcpServerRequestTimeoutMsOverride = new();
        private OverrideableSetting<bool> _mcpServerReadOnlyOverride = new();
        private OverrideableSetting<string> _mcpServerAllowedToolsOverride = new();
        private OverrideableSetting<string> _mcpServerDeniedToolsOverride = new();
        private OverrideableSetting<bool> _mcpServerRateLimitEnabledOverride = new();
        private OverrideableSetting<int> _mcpServerRateLimitRequestsOverride = new();
        private OverrideableSetting<int> _mcpServerRateLimitWindowSecondsOverride = new();
        private OverrideableSetting<bool> _mcpServerIncludeStatusInPingOverride = new();

        // Audio overrides (Editor > Game > User cascade)
        private OverrideableSetting<EAudioTransport> _audioTransportOverride = new();
        private OverrideableSetting<EAudioEffects> _audioEffectsOverride = new();
        private OverrideableSetting<bool> _audioArchitectureV2Override = new();
        private OverrideableSetting<int> _audioSampleRateOverride = new();

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

        [Category("MCP Server Overrides")]
        [Description("Override for requiring bearer auth on MCP requests.")]
        public OverrideableSetting<bool> McpServerRequireAuthOverride
        {
            get => _mcpServerRequireAuthOverride;
            set => SetField(ref _mcpServerRequireAuthOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP bearer auth token.")]
        public OverrideableSetting<string> McpServerAuthTokenOverride
        {
            get => _mcpServerAuthTokenOverride;
            set => SetField(ref _mcpServerAuthTokenOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP CORS allowlist.")]
        public OverrideableSetting<string> McpServerCorsAllowlistOverride
        {
            get => _mcpServerCorsAllowlistOverride;
            set => SetField(ref _mcpServerCorsAllowlistOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP max request payload bytes.")]
        public OverrideableSetting<int> McpServerMaxRequestBytesOverride
        {
            get => _mcpServerMaxRequestBytesOverride;
            set => SetField(ref _mcpServerMaxRequestBytesOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP request timeout in milliseconds.")]
        public OverrideableSetting<int> McpServerRequestTimeoutMsOverride
        {
            get => _mcpServerRequestTimeoutMsOverride;
            set => SetField(ref _mcpServerRequestTimeoutMsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP read-only mode.")]
        public OverrideableSetting<bool> McpServerReadOnlyOverride
        {
            get => _mcpServerReadOnlyOverride;
            set => SetField(ref _mcpServerReadOnlyOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP allowed-tools allow-list.")]
        public OverrideableSetting<string> McpServerAllowedToolsOverride
        {
            get => _mcpServerAllowedToolsOverride;
            set => SetField(ref _mcpServerAllowedToolsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP denied-tools deny-list.")]
        public OverrideableSetting<string> McpServerDeniedToolsOverride
        {
            get => _mcpServerDeniedToolsOverride;
            set => SetField(ref _mcpServerDeniedToolsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for enabling MCP per-client rate limiting.")]
        public OverrideableSetting<bool> McpServerRateLimitEnabledOverride
        {
            get => _mcpServerRateLimitEnabledOverride;
            set => SetField(ref _mcpServerRateLimitEnabledOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP rate-limit request quota.")]
        public OverrideableSetting<int> McpServerRateLimitRequestsOverride
        {
            get => _mcpServerRateLimitRequestsOverride;
            set => SetField(ref _mcpServerRateLimitRequestsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for MCP rate-limit window in seconds.")]
        public OverrideableSetting<int> McpServerRateLimitWindowSecondsOverride
        {
            get => _mcpServerRateLimitWindowSecondsOverride;
            set => SetField(ref _mcpServerRateLimitWindowSecondsOverride, value ?? new());
        }

        [Category("MCP Server Overrides")]
        [Description("Override for including expanded status payload in MCP ping responses.")]
        public OverrideableSetting<bool> McpServerIncludeStatusInPingOverride
        {
            get => _mcpServerIncludeStatusInPingOverride;
            set => SetField(ref _mcpServerIncludeStatusInPingOverride, value ?? new());
        }

        /// <summary>
        /// Editor override for audio transport backend.
        /// Highest priority in the audio cascade (Editor > Game > User).
        /// Useful for testing specific backends during development.
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Editor override for audio transport backend (for dev/testing).")]
        public OverrideableSetting<EAudioTransport> AudioTransportOverride
        {
            get => _audioTransportOverride;
            set => SetField(ref _audioTransportOverride, value ?? new());
        }

        /// <summary>
        /// Editor override for audio effects processor.
        /// Highest priority in the audio cascade (Editor > Game > User).
        /// Useful for testing specific effects pipelines during development.
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Editor override for audio effects processor (for dev/testing).")]
        public OverrideableSetting<EAudioEffects> AudioEffectsOverride
        {
            get => _audioEffectsOverride;
            set => SetField(ref _audioEffectsOverride, value ?? new());
        }

        /// <summary>
        /// Editor override for the V2 streaming audio architecture.
        /// Highest priority in the audio cascade (Editor > Game > User).
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Editor override for V2 streaming audio architecture (for dev/testing).")]
        public OverrideableSetting<bool> AudioArchitectureV2Override
        {
            get => _audioArchitectureV2Override;
            set => SetField(ref _audioArchitectureV2Override, value ?? new());
        }

        /// <summary>
        /// Editor override for audio sample rate.
        /// Highest priority in the audio cascade (Editor > Game > User).
        /// </summary>
        [Category("Audio Overrides")]
        [Description("Editor override for audio sample rate in Hz (for dev/testing).")]
        public OverrideableSetting<int> AudioSampleRateOverride
        {
            get => _audioSampleRateOverride;
            set => SetField(ref _audioSampleRateOverride, value ?? new());
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
        private OverrideableSetting<ColorF4> _consoleGeneralColorOverride = new();
        private OverrideableSetting<ColorF4> _consoleRenderingColorOverride = new();
        private OverrideableSetting<ColorF4> _consoleOpenGLColorOverride = new();
        private OverrideableSetting<ColorF4> _consolePhysicsColorOverride = new();
        private OverrideableSetting<ColorF4> _consoleAnimationColorOverride = new();
        private OverrideableSetting<ColorF4> _consoleUIColorOverride = new();

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

        public OverrideableSetting<ColorF4> ConsoleGeneralColorOverride
        {
            get => _consoleGeneralColorOverride;
            set => SetField(ref _consoleGeneralColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> ConsoleRenderingColorOverride
        {
            get => _consoleRenderingColorOverride;
            set => SetField(ref _consoleRenderingColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> ConsoleOpenGLColorOverride
        {
            get => _consoleOpenGLColorOverride;
            set => SetField(ref _consoleOpenGLColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> ConsolePhysicsColorOverride
        {
            get => _consolePhysicsColorOverride;
            set => SetField(ref _consolePhysicsColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> ConsoleAnimationColorOverride
        {
            get => _consoleAnimationColorOverride;
            set => SetField(ref _consoleAnimationColorOverride, value ?? new());
        }

        public OverrideableSetting<ColorF4> ConsoleUIColorOverride
        {
            get => _consoleUIColorOverride;
            set => SetField(ref _consoleUIColorOverride, value ?? new());
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
        private OverrideableSetting<bool> _enableUILayoutDebugLoggingOverride = new();
        private OverrideableSetting<bool> _enableProfilerUdpSendingOverride = new();
        private OverrideableSetting<EDebugShapePopulationMode> _debugShapePopulationModeOverride = new();
        private OverrideableSetting<EDebugVisualizerPopulationMode> _debugVisualizerPopulationModeOverride = new();
        private OverrideableSetting<EDebugPrimitiveBufferFormat> _debugPrimitiveBufferFormatOverride = new();

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

        public OverrideableSetting<bool> EnableUILayoutDebugLoggingOverride
        {
            get => _enableUILayoutDebugLoggingOverride;
            set => SetField(ref _enableUILayoutDebugLoggingOverride, value ?? new());
        }

        public OverrideableSetting<bool> EnableProfilerUdpSendingOverride
        {
            get => _enableProfilerUdpSendingOverride;
            set => SetField(ref _enableProfilerUdpSendingOverride, value ?? new());
        }

        public OverrideableSetting<EDebugShapePopulationMode> DebugShapePopulationModeOverride
        {
            get => _debugShapePopulationModeOverride;
            set => SetField(ref _debugShapePopulationModeOverride, value ?? new());
        }

        public OverrideableSetting<EDebugVisualizerPopulationMode> DebugVisualizerPopulationModeOverride
        {
            get => _debugVisualizerPopulationModeOverride;
            set => SetField(ref _debugVisualizerPopulationModeOverride, value ?? new());
        }

        public OverrideableSetting<EDebugPrimitiveBufferFormat> DebugPrimitiveBufferFormatOverride
        {
            get => _debugPrimitiveBufferFormatOverride;
            set => SetField(ref _debugPrimitiveBufferFormatOverride, value ?? new());
        }
    }
}
