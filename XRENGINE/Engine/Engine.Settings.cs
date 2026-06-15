using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Audio;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Settings properties, change handlers, and settings application for the engine.
    /// </summary>
    public static partial class Engine
    {
        #region Settings Properties

        /// <summary>
        /// User-defined settings such as graphical quality, audio options, and input preferences.
        /// </summary>
        /// <remarks>
        /// These settings persist per-user and can override game-defined defaults.
        /// Changes to this property trigger <see cref="UserSettingsChanged"/>.
        /// </remarks>
        public static UserSettings UserSettings
        {
            get => _userSettings;
            set
            {
                if (!ReplaceSettingsRoot(
                    ref _userSettings,
                    value,
                    static () => new UserSettings(),
                    DetachUserSettings,
                    AttachUserSettings))
                {
                    return;
                }

                OnUserSettingsChanged();
            }
        }

        /// <summary>
        /// Game-defined startup settings including initial world, libraries, and networking configuration.
        /// </summary>
        /// <remarks>
        /// These settings are typically defined by the game project and loaded at startup.
        /// </remarks>
        public static GameStartupSettings GameSettings
        {
            get => _gameSettings;
            set
            {
                if (!ReplaceSettingsRoot(
                    ref _gameSettings,
                    value,
                    static () => new GameStartupSettings(),
                    DetachGameSettings,
                    AttachGameSettings))
                    return;
                
                BuildSettingsChanged?.Invoke(_gameSettings.BuildSettings);

                ApplyEffectiveSettingsForProperty(null);
            }
        }

        /// <summary>
        /// Project-level build settings describing how packaged builds should be produced.
        /// </summary>
        /// <remarks>
        /// Changes to this property trigger <see cref="BuildSettingsChanged"/>.
        /// </remarks>
        public static BuildSettings BuildSettings
        {
            get => GameSettings.BuildSettings;
            set => SetBuildSettings(value);
        }

        /// <summary>
        /// Global editor preferences serving as base defaults.
        /// </summary>
        /// <remarks>
        /// These preferences are shared across all projects and can be overridden
        /// by <see cref="EditorPreferencesOverrides"/> for project-specific customization.
        /// </remarks>
        public static EditorPreferences GlobalEditorPreferences
        {
            get => _globalEditorPreferences;
            set
            {
                if (!ReplaceSettingsRoot(
                    ref _globalEditorPreferences,
                    value,
                    static () => new EditorPreferences(),
                    DetachGlobalEditorPreferences,
                    AttachGlobalEditorPreferences))
                    return;

                UpdateEffectiveEditorPreferences();
            }
        }

        /// <summary>
        /// Project/sandbox-local overrides for editor preferences.
        /// </summary>
        /// <remarks>
        /// These overrides are applied on top of <see cref="GlobalEditorPreferences"/>
        /// to produce the effective <see cref="EditorPreferences"/>.
        /// </remarks>
        public static EditorPreferencesOverrides EditorPreferencesOverrides
        {
            get => _editorPreferencesOverrides;
            set
            {
                if (!ReplaceSettingsRoot(
                    ref _editorPreferencesOverrides,
                    value,
                    static () => new EditorPreferencesOverrides(),
                    DetachEditorPreferencesOverrides,
                    AttachEditorPreferencesOverrides))
                    return;

                UpdateEffectiveEditorPreferences();
            }
        }

        /// <summary>
        /// Effective editor preferences after applying project overrides to global preferences.
        /// </summary>
        /// <remarks>
        /// This is a computed view that combines <see cref="GlobalEditorPreferences"/>
        /// with <see cref="EditorPreferencesOverrides"/>. Changes trigger <see cref="EditorPreferencesChanged"/>.
        /// </remarks>
        public static EditorPreferences EditorPreferences => _editorPreferences;

        #endregion

        #region Settings Property Helpers

        private static bool ReplaceSettingsRoot<T>(
            ref T field,
            T? value,
            Func<T> createDefault,
            Action<T> detach,
            Action<T> attach)
            where T : class
        {
            if (ReferenceEquals(field, value) && value is not null)
                return false;

            if (field is not null)
                detach(field);

            field = value ?? createDefault();
            attach(field);
            return true;
        }

        private static void AttachPropertyChanged(IXRNotifyPropertyChanged source, XRPropertyChangedEventHandler handler)
        {
            source.PropertyChanged -= handler;
            source.PropertyChanged += handler;
        }

        private static void DetachPropertyChanged(IXRNotifyPropertyChanged source, XRPropertyChangedEventHandler handler)
            => source.PropertyChanged -= handler;

        private static void AttachUserSettings(UserSettings settings)
        {
            AttachPropertyChanged(settings, HandleUserSettingsChanged);
            TrackOverrideableSettings(settings, _trackedUserOverrideableSettings);
            Assets?.EnsureTracked(settings.SourceAsset ?? settings);
        }

        private static void DetachUserSettings(UserSettings settings)
        {
            DetachPropertyChanged(settings, HandleUserSettingsChanged);
            UntrackOverrideableSettings(_trackedUserOverrideableSettings, HandleOverrideableSettingChanged);
        }

        private static void AttachGameSettings(GameStartupSettings settings)
        {
            settings.BuildSettings ??= new BuildSettings();
            AttachBuildSettings(settings.BuildSettings);
            AttachPropertyChanged(settings, HandleGameSettingsChanged);
            TrackOverrideableSettings(settings, _trackedGameOverrideableSettings);
        }

        private static void DetachGameSettings(GameStartupSettings settings)
        {
            DetachPropertyChanged(settings, HandleGameSettingsChanged);
            DetachBuildSettings(settings.BuildSettings);
            UntrackOverrideableSettings(_trackedGameOverrideableSettings, HandleOverrideableSettingChanged);
        }

        private static void SetBuildSettings(BuildSettings? value)
        {
            BuildSettings? current = GameSettings.BuildSettings;
            if (ReferenceEquals(current, value) && value is not null)
                return;

            if (current is not null)
                DetachBuildSettings(current);

            GameSettings.BuildSettings = value ?? new BuildSettings();
            AttachBuildSettings(GameSettings.BuildSettings);
            BuildSettingsChanged?.Invoke(GameSettings.BuildSettings);
        }

        private static void AttachBuildSettings(BuildSettings settings)
            => AttachPropertyChanged(settings, HandleBuildSettingsChanged);

        private static void DetachBuildSettings(BuildSettings? settings)
        {
            if (settings is not null)
                DetachPropertyChanged(settings, HandleBuildSettingsChanged);
        }

        private static void AttachGlobalEditorPreferences(EditorPreferences preferences)
        {
            AttachPropertyChanged(preferences, HandleGlobalEditorPreferencesChanged);
            AttachEditorPreferencesSubSettings(preferences);
        }

        private static void DetachGlobalEditorPreferences(EditorPreferences preferences)
        {
            DetachPropertyChanged(preferences, HandleGlobalEditorPreferencesChanged);
            DetachEditorPreferencesSubSettings(preferences);
        }

        private static void AttachEditorPreferencesOverrides(EditorPreferencesOverrides overrides)
        {
            AttachPropertyChanged(overrides, HandleEditorPreferencesOverridesChanged);
            TrackOverrideableSettings(overrides, _trackedEditorOverrideableSettings, HandleEditorPreferencesOverridesChanged);
            AttachEditorPreferencesOverridesSubSettings(overrides);
        }

        private static void DetachEditorPreferencesOverrides(EditorPreferencesOverrides overrides)
        {
            DetachPropertyChanged(overrides, HandleEditorPreferencesOverridesChanged);
            UntrackOverrideableSettings(_trackedEditorOverrideableSettings, HandleEditorPreferencesOverridesChanged);
            DetachEditorPreferencesOverridesSubSettings(overrides);
        }

        #endregion

        #region Settings Change Handlers

        /// <summary>
        /// Called when user settings change to apply effective settings.
        /// </summary>
        private static void OnUserSettingsChanged()
        {
            ApplyEffectiveSettingsForProperty(null);
            UserSettingsChanged?.Invoke(_userSettings);
        }

        /// <summary>
        /// Handles property changes on <see cref="UserSettings"/>.
        /// </summary>
        private static void HandleUserSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSettingsMetadataProperty(e.PropertyName))
                return;

            RefreshOverrideableSettingsTracking(
                e,
                _userSettings,
                _trackedUserOverrideableSettings,
                HandleOverrideableSettingChanged);

            ApplyEffectiveSettingsForProperty(e.PropertyName);
        }

        /// <summary>
        /// Handles property changes on <see cref="BuildSettings"/>.
        /// </summary>
        private static void HandleBuildSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            BuildSettingsChanged?.Invoke(GameSettings.BuildSettings);
        }

        /// <summary>
        /// Handles property changes on <see cref="GameSettings"/>.
        /// </summary>
        private static void HandleGameSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSettingsMetadataProperty(e.PropertyName))
                return;

            RefreshOverrideableSettingsTracking(
                e,
                _gameSettings,
                _trackedGameOverrideableSettings,
                HandleOverrideableSettingChanged);

            ApplyEffectiveSettingsForProperty(e.PropertyName);
        }

        /// <summary>
        /// Handles property changes on <see cref="GlobalEditorPreferences"/>.
        /// </summary>
        private static void HandleGlobalEditorPreferencesChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSettingsMetadataProperty(e.PropertyName))
                return;

            RefreshSubSettingsSubscription<EditorThemeSettings>(
                e,
                nameof(EditorPreferences.Theme),
                HandleGlobalEditorPreferencesChanged);

            RefreshSubSettingsSubscription<EditorDebugOptions>(
                e,
                nameof(EditorPreferences.Debug),
                HandleGlobalEditorPreferencesChanged);

            UpdateEffectiveEditorPreferences();
        }

        /// <summary>
        /// Handles property changes on <see cref="EditorPreferencesOverrides"/>.
        /// </summary>
        private static void HandleEditorPreferencesOverridesChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSettingsMetadataProperty(e.PropertyName))
                return;

            RefreshEditorPreferencesOverrideableSettingsTracking(sender, e);

            RefreshSubSettingsSubscription<EditorThemeOverrides>(
                e,
                nameof(EditorPreferencesOverrides.Theme),
                HandleEditorPreferencesOverridesChanged,
                _trackedEditorThemeOverrideableSettings);

            RefreshSubSettingsSubscription<EditorDebugOverrides>(
                e,
                nameof(EditorPreferencesOverrides.Debug),
                HandleEditorPreferencesOverridesChanged,
                _trackedEditorDebugOverrideableSettings);

            UpdateEffectiveEditorPreferences();
        }

        /// <summary>
        /// Handles changes to individual overrideable settings.
        /// </summary>
        private static void HandleOverrideableSettingChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is not IOverrideableSetting setting || !_overrideableSettingPropertyMap.TryGetValue(setting, out var propertyName))
                return;

            if (IsEditorPreferencesOverrideSetting(setting))
            {
                UpdateEffectiveEditorPreferences();
                return;
            }

            ApplyEffectiveSettingsForProperty(propertyName);
        }

        private static bool IsEditorPreferencesOverrideSetting(IOverrideableSetting setting)
            => _trackedEditorOverrideableSettings.Contains(setting)
                || _trackedEditorThemeOverrideableSettings.Contains(setting)
                || _trackedEditorDebugOverrideableSettings.Contains(setting);

        private static void RefreshEditorPreferencesOverrideableSettingsTracking(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (_editorPreferencesOverrides is null)
                return;

            if (ReferenceEquals(sender, _editorPreferencesOverrides))
            {
                RefreshOverrideableSettingsTracking(
                    e,
                    _editorPreferencesOverrides,
                    _trackedEditorOverrideableSettings,
                    HandleEditorPreferencesOverridesChanged);
                return;
            }

            if (ReferenceEquals(sender, _editorPreferencesOverrides.Theme))
            {
                RefreshOverrideableSettingsTracking(
                    e,
                    _editorPreferencesOverrides.Theme,
                    _trackedEditorThemeOverrideableSettings,
                    HandleEditorPreferencesOverridesChanged);
                return;
            }

            if (ReferenceEquals(sender, _editorPreferencesOverrides.Debug))
            {
                RefreshOverrideableSettingsTracking(
                    e,
                    _editorPreferencesOverrides.Debug,
                    _trackedEditorDebugOverrideableSettings,
                    HandleEditorPreferencesOverridesChanged);
            }
        }

        private static bool IsSettingsMetadataProperty(string? propertyName)
            => propertyName is nameof(XRObjectBase.Name)
                or nameof(XRAsset.FilePath)
                or nameof(XRAsset.IsDirty)
                or nameof(XRAsset.OriginalPath)
                or nameof(XRAsset.OriginalLastWriteTimeUtc);

        #endregion

        #region Settings Application

        private readonly struct SettingsCascadeSuppressionScope(bool applyOnDispose) : IDisposable
        {
            public void Dispose()
                => EndSettingsCascadeSuppression(applyOnDispose);
        }

        private static SettingsCascadeSuppressionScope SuppressSettingsCascades(bool applyOnDispose = true)
        {
            _settingsCascadeSuppressionDepth++;
            _suppressSettingsCascades = true;
            return new SettingsCascadeSuppressionScope(applyOnDispose);
        }

        private static void EndSettingsCascadeSuppression(bool applyPending)
        {
            if (_settingsCascadeSuppressionDepth <= 0)
                return;

            _settingsCascadeSuppressionDepth--;
            if (_settingsCascadeSuppressionDepth > 0)
                return;

            _suppressSettingsCascades = false;

            if (applyPending)
            {
                FlushSuppressedSettingsCascades();
                return;
            }

            ClearPendingSettingsCascades();
        }

        private static void MarkRuntimeSettingsApplyPending()
            => _runtimeSettingsApplyPending = true;

        private static void MarkEditorPreferencesApplyPending()
        {
            _editorPreferencesApplyPending = true;
            _runtimeSettingsApplyPending = true;
        }

        private static void FlushSuppressedSettingsCascades()
        {
            bool applyEditorPreferences = _editorPreferencesApplyPending;
            bool applyRuntimeSettings = _runtimeSettingsApplyPending;
            ClearPendingSettingsCascades();

            if (applyEditorPreferences)
            {
                ApplyEditorPreferencesRuntimeSideEffects();
                Rendering.ApplyEditorPreferencesChange(null);
            }

            if (applyRuntimeSettings)
                ApplyEffectiveSettingsRuntime();
        }

        private static void ClearPendingSettingsCascades()
        {
            _editorPreferencesApplyPending = false;
            _runtimeSettingsApplyPending = false;
        }

        /// <summary>
        /// Maps property names from UserSettings, GameStartupSettings, and EditorPreferencesOverrides
        /// to the handler(s) that apply the effective cascade value at runtime.
        /// Built once; lookups are O(1).
        /// </summary>
        private static readonly Dictionary<string, Action> _settingsPropertyHandlers = BuildSettingsPropertyHandlers();

        private static Dictionary<string, Action> BuildSettingsPropertyHandlers()
        {
            var h = new Dictionary<string, Action>(StringComparer.Ordinal);

            // ── GI ──
            static void ApplyGI() => Rendering.ApplyGlobalIlluminationModePreference();
            h[nameof(UserSettings.GlobalIlluminationMode)] = ApplyGI;
            h[nameof(UserSettings.GlobalIlluminationModeOverride)] = ApplyGI;     // also matches GameStartup's identically-named property

            // ── GPU render dispatch ──
            static void ApplyGpuDispatchSettings()
            {
                Rendering.ApplyGpuRenderDispatchPreference();
                Rendering.LogVulkanFeatureProfileFingerprint();
            }
            h[nameof(GameStartupSettings.GPURenderDispatch)] = Rendering.ApplyGpuRenderDispatchPreference;
            h[nameof(UserSettings.GPURenderDispatchOverride)] = Rendering.ApplyGpuRenderDispatchPreference;
            h[nameof(GameStartupSettings.EnableGpuIndirectDebugLoggingOverride)] = ApplyGpuDispatchSettings;
            h[nameof(GameStartupSettings.EnableGpuIndirectCpuFallbackOverride)] = ApplyGpuDispatchSettings;
            h[nameof(GameStartupSettings.EnableGpuIndirectValidationLoggingOverride)] = ApplyGpuDispatchSettings;
            h[nameof(GameStartupSettings.EnableZeroReadbackMaterialScatterOverride)] = ApplyGpuDispatchSettings;
            h[nameof(GameStartupSettings.ZeroReadbackMaterialDrawPathOverride)] = ApplyGpuDispatchSettings;
            h[nameof(UserSettings.EnableGpuIndirectDebugLoggingOverride)] = ApplyGpuDispatchSettings;
            h[nameof(UserSettings.EnableGpuIndirectCpuFallbackOverride)] = ApplyGpuDispatchSettings;
            h[nameof(UserSettings.EnableGpuIndirectValidationLoggingOverride)] = ApplyGpuDispatchSettings;
            h[nameof(UserSettings.EnableZeroReadbackMaterialScatterOverride)] = ApplyGpuDispatchSettings;
            h[nameof(UserSettings.ZeroReadbackMaterialDrawPathOverride)] = ApplyGpuDispatchSettings;

            // ── GPU BVH ──
            h[nameof(GameStartupSettings.UseGpuBvhOverride)] = Rendering.ApplyGpuBvhPreference;
            h[nameof(GameStartupSettings.CpuSceneCullingStructureOverride)] = Rendering.ApplyCpuSceneCullingStructurePreference;

            // ── Vulkan GPU-driven profile (compound) ──
            static void ApplyVulkanProfileSettings()
            {
                Rendering.ApplyGpuRenderDispatchPreference();
                Rendering.ApplyGpuBvhPreference();
                Rendering.LogVulkanFeatureProfileFingerprint();
            }
            h[nameof(GameStartupSettings.VulkanGpuDrivenProfileOverride)] = ApplyVulkanProfileSettings;

            // ── NVIDIA DLSS ──
            h[nameof(UserSettings.EnableNvidiaDlssOverride)] = Rendering.ApplyNvidiaDlssPreference;
            h[nameof(UserSettings.DlssQualityOverride)] = Rendering.ApplyNvidiaDlssPreference;
            h[nameof(UserSettings.EnableNvidiaDlssFrameGenerationOverride)] = Rendering.ApplyNvidiaDlssPreference;
            h[nameof(UserSettings.NvidiaDlssFrameGenerationModeOverride)] = Rendering.ApplyNvidiaDlssPreference;

            // ── Intel XeSS ──
            h[nameof(UserSettings.EnableIntelXessOverride)] = Rendering.ApplyIntelXessPreference;
            h[nameof(UserSettings.XessQualityOverride)] = Rendering.ApplyIntelXessPreference;

            // ── Anti-aliasing ──
            Action applyAA = Rendering.ApplyAntiAliasingPreference;
            h[nameof(UserSettings.AntiAliasingModeOverride)] = applyAA;           // also matches GameStartup
            h[nameof(UserSettings.MsaaSampleCountOverride)] = applyAA;            // also matches GameStartup

            // ── Timer / VSync ──
            Action applyTimer = ApplyTimerSettings;
            h[nameof(UserSettings.VSync)] = applyTimer;
            h[nameof(UserSettings.VSyncOverride)] = applyTimer;                   // also matches GameStartup
            h[nameof(UserSettings.TargetUpdatesPerSecondOverride)] = applyTimer;
            h[nameof(UserSettings.TargetFramesPerSecondOverride)] = applyTimer;
            h[nameof(UserSettings.UnfocusedTargetFramesPerSecondOverride)] = applyTimer;
            h[nameof(UserSettings.FixedFramesPerSecondOverride)] = applyTimer;
            h[nameof(GameStartupSettings.TargetUpdatesPerSecond)] = applyTimer;
            h[nameof(GameStartupSettings.TargetFramesPerSecond)] = applyTimer;
            h[nameof(GameStartupSettings.UnfocusedTargetFramesPerSecond)] = applyTimer;
            h[nameof(GameStartupSettings.FixedFramesPerSecond)] = applyTimer;

            // ── Audio ──
            Action applyAudio = ApplyAudioPreferences;
            h[nameof(UserSettings.AudioTransport)] = applyAudio;
            h[nameof(UserSettings.AudioEffects)] = applyAudio;
            h[nameof(UserSettings.AudioArchitectureV2)] = applyAudio;
            h[nameof(UserSettings.AudioSampleRate)] = applyAudio;
            h[nameof(GameStartupSettings.AudioTransportOverride)] = applyAudio;
            h[nameof(GameStartupSettings.AudioEffectsOverride)] = applyAudio;
            h[nameof(GameStartupSettings.AudioArchitectureV2Override)] = applyAudio;
            h[nameof(GameStartupSettings.AudioSampleRateOverride)] = applyAudio;
            h[nameof(EditorPreferencesOverrides.AudioTransportOverride)] = applyAudio;
            h[nameof(EditorPreferencesOverrides.AudioEffectsOverride)] = applyAudio;
            h[nameof(EditorPreferencesOverrides.AudioArchitectureV2Override)] = applyAudio;
            h[nameof(EditorPreferencesOverrides.AudioSampleRateOverride)] = applyAudio;

            // ── Camera depth mode ──
            Action applyDepthMode = Rendering.ApplySceneCameraDepthModePreference;
            h[nameof(GameStartupSettings.DepthModeOverride)] = applyDepthMode;

            // ── Parallel tick ──
            h[nameof(UserSettings.TickGroupedItemsInParallelOverride)] = Rendering.ApplyTickGroupedItemsInParallelPreference;
            // GameStartup's identically-named property matches the same key

            // ── Technical / compute overrides (GameStartup → Engine push) ──
            h[nameof(GameStartupSettings.AllowShaderPipelinesOverride)] = Rendering.ApplyAllowShaderPipelinesPreference;
            h[nameof(GameStartupSettings.AllowSkinningOverride)] = Rendering.ApplyAllowSkinningPreference;
            h[nameof(GameStartupSettings.RecalcChildMatricesLoopTypeOverride)] = Rendering.ApplyRecalcChildMatricesLoopTypePreference;
            Action applyCompute = Rendering.ApplyComputeRenderingPreference;
            h[nameof(GameStartupSettings.CalculateSkinningInComputeShaderOverride)] = applyCompute;
            h[nameof(GameStartupSettings.CalculateBlendshapesInComputeShaderOverride)] = applyCompute;
            h[nameof(GameStartupSettings.UseDetailPreservingComputeMipmapsOverride)] = applyCompute;

            return h;
        }

        /// <summary>
        /// Replays editor-preference side effects that cannot rely on value-changing setters during startup.
        /// </summary>
        private static void ApplyEditorPreferencesRuntimeSideEffects()
            => _editorPreferences?.ApplyRuntimeSideEffects();

        /// <summary>
        /// Recomputes effective editor preferences from global defaults and project/runtime overrides,
        /// then reapplies their runtime side effects.
        /// </summary>
        public static void RefreshEffectiveEditorPreferences()
            => UpdateEffectiveEditorPreferences();

        /// <summary>
        /// Applies all runtime-effective settings (called when settings change globally).
        /// </summary>
        private static void ApplyEffectiveSettingsRuntime()
        {
            Rendering.ApplyRenderPipelinePreference();
            Rendering.ApplyGlobalIlluminationModePreference();
            Rendering.ApplyAntiAliasingPreference();
            Rendering.ApplyGpuRenderDispatchPreference();
            Rendering.ApplyGpuBvhPreference();
            Rendering.ApplyCpuSceneCullingStructurePreference();
            Rendering.ApplyNvidiaDlssPreference();
            Rendering.ApplyIntelXessPreference();
            Rendering.ApplyTickGroupedItemsInParallelPreference();
            Rendering.ApplyAllowShaderPipelinesPreference();
            Rendering.ApplyAllowSkinningPreference();
            Rendering.ApplyRecalcChildMatricesLoopTypePreference();
            Rendering.ApplyComputeRenderingPreference();
            ApplyTimerSettings();
            ApplyAudioPreferences();
        }

        /// <summary>
        /// Applies settings changes for a specific property, or all settings if property is null.
        /// </summary>
        private static void ApplyEffectiveSettingsForProperty(string? propertyName)
        {
            if (_suppressSettingsCascades)
            {
                MarkRuntimeSettingsApplyPending();
                return;
            }

            if (string.IsNullOrWhiteSpace(propertyName))
            {
                ApplyEffectiveSettingsRuntime();
                return;
            }

            if (_settingsPropertyHandlers.TryGetValue(propertyName, out var handler))
                handler();
        }

        /// <summary>
        /// Applies timer settings from effective settings.
        /// </summary>
        private static void ApplyTimerSettings()
        {
            EVSyncMode vSync = EffectiveSettings.VSync;
            float targetRenderFrequency = Time.ResolveRenderFrequency(LastFocusState, vSync);
            float targetUpdateFrequency = EffectiveSettings.TargetUpdatesPerSecond ?? 0.0f;
            float fixedUpdateFrequency = EffectiveSettings.FixedFramesPerSecond;

            Time.UpdateTimer(
                targetRenderFrequency,
                targetUpdateFrequency,
                fixedUpdateFrequency,
                vSync);

            ApplyWindowVSyncSettings();
        }

        /// <summary>
        /// Applies audio preferences from the cascading settings system to the audio subsystem.
        /// Cascade: Editor Prefs Override > Game Override > User Preference.
        /// Emits an audio warning with a stack trace for startup call-site diagnostics.
        /// </summary>
        private static int _applyAudioPreferencesCallCount;

        private static void ApplyAudioPreferences()
        {
            bool v2 = EffectiveSettings.AudioArchitectureV2;
            var transport = EffectiveSettings.AudioTransport;
            var effects = EffectiveSettings.AudioEffects;
            int sampleRate = EffectiveSettings.AudioSampleRate;

            bool currentV2 = AudioSettings.AudioArchitectureV2;
            var currentTransport = AudioSettings.DefaultTransport;
            var currentEffects = AudioSettings.DefaultEffects;
            int currentSampleRate = AudioSettings.SampleRate;
            bool alreadyApplied =
                currentV2 == v2
                && currentTransport == transport
                && currentEffects == effects
                && currentSampleRate == sampleRate;

            LogApplyAudioPreferencesCall(
                v2,
                transport,
                effects,
                sampleRate,
                currentV2,
                currentTransport,
                currentEffects,
                currentSampleRate,
                !alreadyApplied);

            if (alreadyApplied)
                return; // Nothing changed.

            AudioSettings.AudioArchitectureV2 = v2;
            AudioSettings.DefaultTransport = transport;
            AudioSettings.DefaultEffects = effects;
            AudioSettings.SampleRate = sampleRate;
            AudioSettings.ApplyTo(Audio);
        }

        private static void LogApplyAudioPreferencesCall(
            bool effectiveV2,
            EAudioTransport effectiveTransport,
            EAudioEffects effectiveEffects,
            int effectiveSampleRate,
            bool currentV2,
            EAudioTransport currentTransport,
            EAudioEffects currentEffects,
            int currentSampleRate,
            bool willApply)
        {
            int callCount = System.Threading.Interlocked.Increment(ref _applyAudioPreferencesCallCount);
            string action = willApply ? "applying audio settings" : "audio settings already current";
            Debug.LogWarning(
                ELogCategory.Audio,
                0,
                32,
                "ApplyAudioPreferences call #{0} on thread {1}: {2}. Effective(V2={3}, Transport={4}, Effects={5}, SampleRate={6}); Current(V2={7}, Transport={8}, Effects={9}, SampleRate={10}).",
                callCount,
                Environment.CurrentManagedThreadId,
                action,
                effectiveV2,
                effectiveTransport,
                effectiveEffects,
                effectiveSampleRate,
                currentV2,
                currentTransport,
                currentEffects,
                currentSampleRate);
        }

        #endregion

        #region Overrideable Settings Tracking

        private static void RefreshOverrideableSettingsTracking<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            IXRPropertyChangedEventArgs e,
            T settingsRoot,
            List<IOverrideableSetting> cache,
            XRPropertyChangedEventHandler handler)
        {
            if (ReferenceEquals(e.PreviousValue, e.NewValue))
                return;

            if (e.PreviousValue is IOverrideableSetting || e.NewValue is IOverrideableSetting)
                TrackOverrideableSettings(settingsRoot, cache, handler);
        }

        /// <summary>
        /// Tracks overrideable settings from a settings root object.
        /// </summary>
        private static void TrackOverrideableSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            T settingsRoot,
            List<IOverrideableSetting> cache)
            => TrackOverrideableSettings(settingsRoot, cache, HandleOverrideableSettingChanged);

        /// <summary>
        /// Tracks overrideable settings from a settings root object with a custom change handler.
        /// </summary>
        private static void TrackOverrideableSettings<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            T settingsRoot,
            List<IOverrideableSetting> cache,
            XRPropertyChangedEventHandler handler)
        {
            UntrackOverrideableSettings(cache, handler);

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                if (!typeof(IOverrideableSetting).IsAssignableFrom(property.PropertyType))
                    continue;

                IOverrideableSetting? overrideable = null;
                try
                {
                    overrideable = property.GetValue(settingsRoot) as IOverrideableSetting;
                }
                catch
                {
                    continue;
                }

                if (overrideable is null)
                    continue;

                if (cache.Contains(overrideable))
                    continue;

                cache.Add(overrideable);
                _overrideableSettingPropertyMap[overrideable] = property.Name;

                if (overrideable is IXRNotifyPropertyChanged notify)
                    notify.PropertyChanged += handler;
            }
        }

        /// <summary>
        /// Untracks previously tracked overrideable settings.
        /// </summary>
        private static void UntrackOverrideableSettings(List<IOverrideableSetting> cache, XRPropertyChangedEventHandler handler)
        {
            foreach (var tracked in cache)
            {
                if (tracked is IXRNotifyPropertyChanged notify)
                    notify.PropertyChanged -= handler;

                _overrideableSettingPropertyMap.Remove(tracked);
            }

            cache.Clear();
        }

        #endregion

        #region Editor Preferences Helpers

        private static void RefreshSubSettingsSubscription<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
            IXRPropertyChangedEventArgs e,
            string propertyName,
            XRPropertyChangedEventHandler handler,
            List<IOverrideableSetting>? trackedOverrideableSettings = null)
            where T : class, IXRNotifyPropertyChanged
        {
            if (e.PropertyName != propertyName)
                return;

            if (e.PreviousValue is T previous)
                DetachPropertyChanged(previous, handler);

            if (trackedOverrideableSettings is not null)
                UntrackOverrideableSettings(trackedOverrideableSettings, handler);

            if (e.NewValue is not T current)
                return;

            AttachPropertyChanged(current, handler);

            if (trackedOverrideableSettings is not null)
                TrackOverrideableSettings(current, trackedOverrideableSettings, handler);
        }

        /// <summary>
        /// Attaches change handlers to editor preferences sub-settings.
        /// </summary>
        private static void AttachEditorPreferencesSubSettings(EditorPreferences preferences)
        {
            if (preferences.Theme is not null)
                AttachPropertyChanged(preferences.Theme, HandleGlobalEditorPreferencesChanged);

            if (preferences.Debug is not null)
                AttachPropertyChanged(preferences.Debug, HandleGlobalEditorPreferencesChanged);
        }

        /// <summary>
        /// Detaches change handlers from editor preferences sub-settings.
        /// </summary>
        private static void DetachEditorPreferencesSubSettings(EditorPreferences preferences)
        {
            if (preferences.Theme is not null)
                DetachPropertyChanged(preferences.Theme, HandleGlobalEditorPreferencesChanged);

            if (preferences.Debug is not null)
                DetachPropertyChanged(preferences.Debug, HandleGlobalEditorPreferencesChanged);
        }

        /// <summary>
        /// Attaches change handlers to editor preferences overrides sub-settings.
        /// </summary>
        private static void AttachEditorPreferencesOverridesSubSettings(EditorPreferencesOverrides overrides)
        {
            if (overrides.Theme is not null)
                AttachPropertyChanged(overrides.Theme, HandleEditorPreferencesOverridesChanged);

            if (overrides.Debug is not null)
                AttachPropertyChanged(overrides.Debug, HandleEditorPreferencesOverridesChanged);

            if (overrides.Theme is not null)
                TrackOverrideableSettings(overrides.Theme, _trackedEditorThemeOverrideableSettings, HandleEditorPreferencesOverridesChanged);

            if (overrides.Debug is not null)
                TrackOverrideableSettings(overrides.Debug, _trackedEditorDebugOverrideableSettings, HandleEditorPreferencesOverridesChanged);
        }

        /// <summary>
        /// Detaches change handlers from editor preferences overrides sub-settings.
        /// </summary>
        private static void DetachEditorPreferencesOverridesSubSettings(EditorPreferencesOverrides overrides)
        {
            if (overrides.Theme is not null)
                DetachPropertyChanged(overrides.Theme, HandleEditorPreferencesOverridesChanged);

            if (overrides.Debug is not null)
                DetachPropertyChanged(overrides.Debug, HandleEditorPreferencesOverridesChanged);

            UntrackOverrideableSettings(_trackedEditorThemeOverrideableSettings, HandleEditorPreferencesOverridesChanged);
            UntrackOverrideableSettings(_trackedEditorDebugOverrideableSettings, HandleEditorPreferencesOverridesChanged);
        }

        /// <summary>
        /// Recomputes effective editor preferences by applying overrides to global preferences.
        /// </summary>
        private static void UpdateEffectiveEditorPreferences()
        {
            _editorPreferences ??= new EditorPreferences();
            _globalEditorPreferences ??= new EditorPreferences();
            _editorPreferencesOverrides ??= new EditorPreferencesOverrides();

            _editorPreferences.CopyFrom(_globalEditorPreferences);
            _editorPreferences.ApplyOverrides(_editorPreferencesOverrides);

            if (_suppressSettingsCascades)
            {
                MarkEditorPreferencesApplyPending();
            }
            else
            {
                ApplyEditorPreferencesRuntimeSideEffects();
                Rendering.ApplyEditorPreferencesChange(null);
                ApplyAudioPreferences();
            }

            EditorPreferencesChanged?.Invoke(_editorPreferences);
        }

        #endregion
    }
}
