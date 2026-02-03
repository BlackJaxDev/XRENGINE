using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
                if (ReferenceEquals(_userSettings, value) && value is not null)
                    return;

                _userSettings?.PropertyChanged -= HandleUserSettingsChanged;
                _userSettings = value ?? new UserSettings();
                _userSettings.PropertyChanged += HandleUserSettingsChanged;
                TrackOverrideableSettings(_userSettings, _trackedUserOverrideableSettings);

                Assets?.EnsureTracked(_userSettings.SourceAsset ?? _userSettings);

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
                if (ReferenceEquals(_gameSettings, value) && value is not null)
                    return;

                if (_gameSettings is not null)
                    _gameSettings.PropertyChanged -= HandleGameSettingsChanged;

                if (_gameSettings?.BuildSettings is not null)
                    _gameSettings.BuildSettings.PropertyChanged -= HandleBuildSettingsChanged;

                _gameSettings = value ?? new GameStartupSettings();

                _gameSettings.BuildSettings ??= new BuildSettings();

                _gameSettings.BuildSettings.PropertyChanged += HandleBuildSettingsChanged;
                _gameSettings.PropertyChanged += HandleGameSettingsChanged;
                TrackOverrideableSettings(_gameSettings, _trackedGameOverrideableSettings);
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
            set
            {
                if (ReferenceEquals(GameSettings.BuildSettings, value) && value is not null)
                    return;

                if (GameSettings.BuildSettings is not null)
                    GameSettings.BuildSettings.PropertyChanged -= HandleBuildSettingsChanged;

                GameSettings.BuildSettings = value ?? new BuildSettings();
                GameSettings.BuildSettings.PropertyChanged += HandleBuildSettingsChanged;
                BuildSettingsChanged?.Invoke(GameSettings.BuildSettings);
            }
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
                if (ReferenceEquals(_globalEditorPreferences, value) && value is not null)
                    return;

                if (_globalEditorPreferences is not null)
                {
                    _globalEditorPreferences.PropertyChanged -= HandleGlobalEditorPreferencesChanged;
                    DetachEditorPreferencesSubSettings(_globalEditorPreferences);
                }

                _globalEditorPreferences = value ?? new EditorPreferences();
                _globalEditorPreferences.PropertyChanged += HandleGlobalEditorPreferencesChanged;
                AttachEditorPreferencesSubSettings(_globalEditorPreferences);
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
                if (ReferenceEquals(_editorPreferencesOverrides, value) && value is not null)
                    return;

                if (_editorPreferencesOverrides is not null)
                {
                    _editorPreferencesOverrides.PropertyChanged -= HandleEditorPreferencesOverridesChanged;
                    DetachEditorPreferencesOverridesSubSettings(_editorPreferencesOverrides);
                }

                _editorPreferencesOverrides = value ?? new EditorPreferencesOverrides();
                _editorPreferencesOverrides.PropertyChanged += HandleEditorPreferencesOverridesChanged;
                AttachEditorPreferencesOverridesSubSettings(_editorPreferencesOverrides);
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
            TrackOverrideableSettings(_userSettings, _trackedUserOverrideableSettings);
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
            TrackOverrideableSettings(_gameSettings, _trackedGameOverrideableSettings);
            ApplyEffectiveSettingsForProperty(e.PropertyName);
        }

        /// <summary>
        /// Handles property changes on <see cref="GlobalEditorPreferences"/>.
        /// </summary>
        private static void HandleGlobalEditorPreferencesChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorPreferences.Theme))
            {
                if (e.PreviousValue is EditorThemeSettings previous)
                    previous.PropertyChanged -= HandleGlobalEditorPreferencesChanged;

                if (e.NewValue is EditorThemeSettings current)
                    current.PropertyChanged += HandleGlobalEditorPreferencesChanged;
            }

            if (e.PropertyName == nameof(EditorPreferences.Debug))
            {
                if (e.PreviousValue is EditorDebugOptions previous)
                    previous.PropertyChanged -= HandleGlobalEditorPreferencesChanged;

                if (e.NewValue is EditorDebugOptions current)
                    current.PropertyChanged += HandleGlobalEditorPreferencesChanged;
            }

            UpdateEffectiveEditorPreferences();
        }

        /// <summary>
        /// Handles property changes on <see cref="EditorPreferencesOverrides"/>.
        /// </summary>
        private static void HandleEditorPreferencesOverridesChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorPreferencesOverrides.Theme))
            {
                if (e.PreviousValue is EditorThemeOverrides previous)
                {
                    previous.PropertyChanged -= HandleEditorPreferencesOverridesChanged;
                    UntrackOverrideableSettings(_trackedEditorThemeOverrideableSettings, HandleEditorPreferencesOverridesChanged);
                }

                if (e.NewValue is EditorThemeOverrides current)
                {
                    current.PropertyChanged += HandleEditorPreferencesOverridesChanged;
                    TrackOverrideableSettings(current, _trackedEditorThemeOverrideableSettings, HandleEditorPreferencesOverridesChanged);
                }
            }

            if (e.PropertyName == nameof(EditorPreferencesOverrides.Debug))
            {
                if (e.PreviousValue is EditorDebugOverrides previous)
                {
                    previous.PropertyChanged -= HandleEditorPreferencesOverridesChanged;
                    UntrackOverrideableSettings(_trackedEditorDebugOverrideableSettings, HandleEditorPreferencesOverridesChanged);
                }

                if (e.NewValue is EditorDebugOverrides current)
                {
                    current.PropertyChanged += HandleEditorPreferencesOverridesChanged;
                    TrackOverrideableSettings(current, _trackedEditorDebugOverrideableSettings, HandleEditorPreferencesOverridesChanged);
                }
            }

            UpdateEffectiveEditorPreferences();
        }

        /// <summary>
        /// Handles changes to individual overrideable settings.
        /// </summary>
        private static void HandleOverrideableSettingChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (sender is IOverrideableSetting setting && _overrideableSettingPropertyMap.TryGetValue(setting, out var propertyName))
                ApplyEffectiveSettingsForProperty(propertyName);
        }

        #endregion

        #region Settings Application

        /// <summary>
        /// Applies all runtime-effective settings (called when settings change globally).
        /// </summary>
        private static void ApplyEffectiveSettingsRuntime()
        {
            Rendering.ApplyRenderPipelinePreference();
            Rendering.ApplyGlobalIlluminationModePreference();
            Rendering.ApplyGpuRenderDispatchPreference();
            Rendering.ApplyGpuBvhPreference();
            Rendering.ApplyNvidiaDlssPreference();
            Rendering.ApplyIntelXessPreference();
            ApplyTimerSettings();
        }

        /// <summary>
        /// Applies settings changes for a specific property, or all settings if property is null.
        /// </summary>
        private static void ApplyEffectiveSettingsForProperty(string? propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                ApplyEffectiveSettingsRuntime();
                return;
            }

            if (propertyName == nameof(UserSettings.GlobalIlluminationMode)
                || propertyName == nameof(UserSettings.GlobalIlluminationModeOverride))
            {
                Rendering.ApplyGlobalIlluminationModePreference();
            }
            else if (propertyName == nameof(GameStartupSettings.GPURenderDispatch)
                || propertyName == nameof(UserSettings.GPURenderDispatchOverride))
            {
                Rendering.ApplyGpuRenderDispatchPreference();
            }
            else if (propertyName == nameof(GameStartupSettings.UseGpuBvhOverride))
            {
                Rendering.ApplyGpuBvhPreference();
            }
            else if (propertyName == nameof(UserSettings.EnableNvidiaDlssOverride)
                || propertyName == nameof(UserSettings.DlssQualityOverride))
            {
                Rendering.ApplyNvidiaDlssPreference();
            }
            else if (propertyName == nameof(UserSettings.EnableIntelXessOverride)
                || propertyName == nameof(UserSettings.XessQualityOverride))
            {
                Rendering.ApplyIntelXessPreference();
            }
            else if (propertyName == nameof(UserSettings.VSync)
                || propertyName == nameof(UserSettings.VSyncOverride)
                || propertyName == nameof(UserSettings.TargetUpdatesPerSecondOverride)
                || propertyName == nameof(UserSettings.TargetFramesPerSecondOverride)
                || propertyName == nameof(UserSettings.UnfocusedTargetFramesPerSecondOverride)
                || propertyName == nameof(UserSettings.FixedFramesPerSecondOverride)
                || propertyName == nameof(GameStartupSettings.TargetUpdatesPerSecond)
                || propertyName == nameof(GameStartupSettings.TargetFramesPerSecond)
                || propertyName == nameof(GameStartupSettings.UnfocusedTargetFramesPerSecond)
                || propertyName == nameof(GameStartupSettings.FixedFramesPerSecond))
            {
                ApplyTimerSettings();
            }
        }

        /// <summary>
        /// Applies timer settings from effective settings.
        /// </summary>
        private static void ApplyTimerSettings()
        {
            float targetRenderFrequency = EffectiveSettings.TargetFramesPerSecond ?? 0.0f;
            float targetUpdateFrequency = EffectiveSettings.TargetUpdatesPerSecond ?? 0.0f;
            float fixedUpdateFrequency = EffectiveSettings.FixedFramesPerSecond;
            EVSyncMode vSync = EffectiveSettings.VSync;

            Time.UpdateTimer(
                targetRenderFrequency,
                targetUpdateFrequency,
                fixedUpdateFrequency,
                vSync);
        }

        #endregion

        #region Overrideable Settings Tracking

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

        /// <summary>
        /// Attaches change handlers to editor preferences sub-settings.
        /// </summary>
        private static void AttachEditorPreferencesSubSettings(EditorPreferences preferences)
        {
            if (preferences?.Theme is not null)
                preferences.Theme.PropertyChanged += HandleGlobalEditorPreferencesChanged;

            if (preferences?.Debug is not null)
                preferences.Debug.PropertyChanged += HandleGlobalEditorPreferencesChanged;
        }

        /// <summary>
        /// Detaches change handlers from editor preferences sub-settings.
        /// </summary>
        private static void DetachEditorPreferencesSubSettings(EditorPreferences preferences)
        {
            if (preferences?.Theme is not null)
                preferences.Theme.PropertyChanged -= HandleGlobalEditorPreferencesChanged;

            if (preferences?.Debug is not null)
                preferences.Debug.PropertyChanged -= HandleGlobalEditorPreferencesChanged;
        }

        /// <summary>
        /// Attaches change handlers to editor preferences overrides sub-settings.
        /// </summary>
        private static void AttachEditorPreferencesOverridesSubSettings(EditorPreferencesOverrides overrides)
        {
            if (overrides?.Theme is not null)
                overrides.Theme.PropertyChanged += HandleEditorPreferencesOverridesChanged;

            if (overrides?.Debug is not null)
                overrides.Debug.PropertyChanged += HandleEditorPreferencesOverridesChanged;

            if (overrides is not null)
                TrackOverrideableSettings(overrides, _trackedEditorOverrideableSettings, HandleEditorPreferencesOverridesChanged);

            if (overrides?.Theme is not null)
                TrackOverrideableSettings(overrides.Theme, _trackedEditorThemeOverrideableSettings, HandleEditorPreferencesOverridesChanged);

            if (overrides?.Debug is not null)
                TrackOverrideableSettings(overrides.Debug, _trackedEditorDebugOverrideableSettings, HandleEditorPreferencesOverridesChanged);
        }

        /// <summary>
        /// Detaches change handlers from editor preferences overrides sub-settings.
        /// </summary>
        private static void DetachEditorPreferencesOverridesSubSettings(EditorPreferencesOverrides overrides)
        {
            if (overrides?.Theme is not null)
                overrides.Theme.PropertyChanged -= HandleEditorPreferencesOverridesChanged;

            if (overrides?.Debug is not null)
                overrides.Debug.PropertyChanged -= HandleEditorPreferencesOverridesChanged;

            UntrackOverrideableSettings(_trackedEditorOverrideableSettings, HandleEditorPreferencesOverridesChanged);
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

            Rendering.ApplyEditorPreferencesChange(null);
            EditorPreferencesChanged?.Invoke(_editorPreferences);
        }

        #endregion
    }
}
