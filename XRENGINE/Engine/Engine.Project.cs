using System;
using System.IO;
using System.Linq;
using XREngine.Core.Files;
using XREngine.Diagnostics;
using static XREngine.Engine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        private const string SandboxFolderName = "Sandbox";
        private const string SandboxConfigFolderName = "Config";
        /// <summary>
        /// The currently loaded project, if any.
        /// </summary>
        public static XRProject? CurrentProject { get; private set; }

        /// <summary>
        /// Event fired when a project is loaded.
        /// </summary>
        public static event Action<XRProject>? ProjectLoaded;

        /// <summary>
        /// Event fired when a project is unloaded.
        /// </summary>
        public static event Action? ProjectUnloaded;

        /// <summary>
        /// Loads a project from the specified .xrproj file path.
        /// This will also load the project's engine and user settings.
        /// </summary>
        /// <param name="projectFilePath">The path to the .xrproj file.</param>
        /// <returns>True if the project was loaded successfully.</returns>
        public static bool LoadProject(string projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            {
                Debug.LogWarning($"Project file not found: {projectFilePath}");
                return false;
            }

            var project = XRProject.Load(projectFilePath);
            if (project is null)
            {
                Debug.LogWarning($"Failed to load project from: {projectFilePath}");
                return false;
            }

            return LoadProject(project);
        }

        /// <summary>
        /// Loads a project and its associated settings.
        /// </summary>
        /// <param name="project">The project to load.</param>
        /// <returns>True if the project was loaded successfully.</returns>
        public static bool LoadProject(XRProject project)
        {
            if (project is null)
                return false;

            project.EnsureStructure();
            LogUnexpectedProjectEntries(project);

            // Unload any existing project
            UnloadProject();

            CurrentProject = project;

            ConfigureProjectDirectories(project);
            Assets.SyncMetadataWithAssets();

            // Load global editor preferences + project overrides
            LoadGlobalEditorPreferences();
            LoadProjectEditorPreferencesOverrides();

            // Load project-specific user settings
            LoadProjectUserSettings();

            // Load project-specific build settings
            LoadProjectBuildSettings();

            // Clear any dirty state that accumulated during loading
            ClearSettingsDirtyState();

            Debug.Out($"Project loaded: {project.ProjectName}");
            ProjectLoaded?.Invoke(project);
            return true;
        }

        /// <summary>
        /// Unloads the current project.
        /// </summary>
        public static void UnloadProject()
        {
            if (CurrentProject is null)
                return;

            Debug.Out($"Project unloaded: {CurrentProject.ProjectName}");
            CurrentProject = null;
            ProjectUnloaded?.Invoke();
        }

        /// <summary>
        /// Loads global settings when running without a project (sandbox mode).
        /// </summary>
        public static void LoadSandboxSettings()
        {
            if (Assets is null)
                return;

            LoadGlobalEditorPreferences();
            LoadSandboxEditorPreferencesOverrides();
            LoadSandboxUserSettings();
            LoadSandboxBuildSettings();

            // Clear any dirty state that accumulated during initialization.
            // Settings created during startup (e.g., VRGameStartupSettings with DefaultUserSettings)
            // may have been marked dirty before the actual saved settings were loaded.
            ClearSettingsDirtyState();
        }

        /// <summary>
        /// Clears dirty state on all engine settings objects.
        /// Called after loading settings to ensure a clean starting state.
        /// </summary>
        private static void ClearSettingsDirtyState()
        {
            _globalEditorPreferences?.ClearDirty();
            _editorPreferencesOverrides?.ClearDirty();
            _userSettings?.ClearDirty();
            _gameSettings?.ClearDirty();
            _gameSettings?.BuildSettings?.ClearDirty();
            _gameSettings?.DefaultUserSettings?.ClearDirty();
        }

        private static string? GetSandboxConfigDirectory()
        {
            string? baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                return null;

            return Path.Combine(baseDir, "XREngine", SandboxFolderName, SandboxConfigFolderName);
        }

        private static string? GetSandboxEditorPreferencesOverridePath()
        {
            string? configDir = GetSandboxConfigDirectory();
            return configDir is null ? null : Path.Combine(configDir, XRProject.EngineSettingsFileName);
        }

        private static string? GetGlobalEditorPreferencesPath()
        {
            string? baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                return null;

            string configDir = Path.Combine(baseDir, "XREngine", "Global", SandboxConfigFolderName);
            return Path.Combine(configDir, "editor_preferences_global.asset");
        }

        private static string? GetSandboxUserSettingsPath()
        {
            string? configDir = GetSandboxConfigDirectory();
            return configDir is null ? null : Path.Combine(configDir, XRProject.UserSettingsFileName);
        }

        private static string? GetSandboxBuildSettingsPath()
        {
            string? configDir = GetSandboxConfigDirectory();
            return configDir is null ? null : Path.Combine(configDir, XRProject.BuildSettingsFileName);
        }

        /// <summary>
        /// Loads the global editor preferences from the user profile.
        /// </summary>
        private static void LoadGlobalEditorPreferences()
        {
            if (Assets is null)
                return;

            string? settingsPath = GetGlobalEditorPreferencesPath();
            if (string.IsNullOrWhiteSpace(settingsPath))
                return;

            if (File.Exists(settingsPath))
            {
                var settings = Assets.Load<EditorPreferences>(settingsPath);
                if (settings is not null)
                {
                    settings.FilePath = settingsPath;
                    settings.Name = "Global Editor Preferences";
                    Assets.EnsureTracked(settings);
                    GlobalEditorPreferences = settings;
                    Debug.Out("Loaded global editor preferences.");
                }
                return;
            }

            var created = GlobalEditorPreferences ?? new EditorPreferences();
            created.FilePath = settingsPath;
            created.Name = "Global Editor Preferences";
            Assets.EnsureTracked(created);
            GlobalEditorPreferences = created;
        }

        /// <summary>
        /// Loads the editor preference overrides from the current project directory.
        /// </summary>
        private static void LoadProjectEditorPreferencesOverrides()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.EngineSettingsPath is null)
            {
                LoadSandboxEditorPreferencesOverrides();
                return;
            }

            string settingsPath = CurrentProject.EngineSettingsPath;

            if (File.Exists(settingsPath))
            {
                var settings = Assets.Load<EditorPreferencesOverrides>(settingsPath);
                if (settings is not null)
                {
                    settings.FilePath = settingsPath;
                    settings.Name = "Editor Preferences Overrides";
                    Assets.EnsureTracked(settings);
                    EditorPreferencesOverrides = settings;
                    Debug.Out("Loaded project editor preference overrides.");
                }
                return;
            }

            var created = EditorPreferencesOverrides ?? new EditorPreferencesOverrides();
            created.FilePath = settingsPath;
            created.Name = "Editor Preferences Overrides";
            Assets.EnsureTracked(created);
            EditorPreferencesOverrides = created;
        }

        private static void LoadSandboxEditorPreferencesOverrides()
        {
            string? settingsPath = GetSandboxEditorPreferencesOverridePath();
            if (string.IsNullOrWhiteSpace(settingsPath) || Assets is null)
                return;

            if (File.Exists(settingsPath))
            {
                var settings = Assets.Load<EditorPreferencesOverrides>(settingsPath);
                if (settings is not null)
                {
                    settings.FilePath = settingsPath;
                    settings.Name = "Editor Preferences Overrides";
                    Assets.EnsureTracked(settings);
                    EditorPreferencesOverrides = settings;
                    Debug.Out("Loaded sandbox editor preference overrides.");
                }
                return;
            }

            var created = EditorPreferencesOverrides ?? new EditorPreferencesOverrides();
            created.FilePath = settingsPath;
            created.Name = "Editor Preferences Overrides";
            Assets.EnsureTracked(created);
            EditorPreferencesOverrides = created;
        }

        /// <summary>
        /// Loads the user settings from the current project directory.
        /// </summary>
        private static void LoadProjectUserSettings()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.UserSettingsPath is null)
            {
                LoadSandboxUserSettings();
                return;
            }

            string userSettingsPath = CurrentProject.UserSettingsPath;

            if (File.Exists(userSettingsPath))
            {
                var loadedSettings = Assets.Load<UserSettings>(userSettingsPath);
                if (loadedSettings is not null)
                {
                    loadedSettings.Name = "User Settings";
                    UserSettings = loadedSettings;
                    Debug.Out("Loaded project user settings.");
                }
                return;
            }

            // No file yet: ensure UserSettings is tracked so changes show up in Save/Save All
            UserSettings.FilePath = userSettingsPath;
            UserSettings.Name = "User Settings";

            string? settingsDirectory = Path.GetDirectoryName(userSettingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            Assets.EnsureTracked(UserSettings);
            UserSettings.MarkDirty();
        }

        private static void LoadSandboxUserSettings()
        {
            string? userSettingsPath = GetSandboxUserSettingsPath();
            if (string.IsNullOrWhiteSpace(userSettingsPath) || Assets is null)
                return;

            if (File.Exists(userSettingsPath))
            {
                var loadedSettings = Assets.Load<UserSettings>(userSettingsPath);
                if (loadedSettings is not null)
                {
                    loadedSettings.Name = "User Settings";
                    UserSettings = loadedSettings;
                    Debug.Out("Loaded sandbox user settings.");
                }
                return;
            }

            // No file yet: ensure UserSettings is tracked so changes show up in Save/Save All
            UserSettings.FilePath = userSettingsPath;
            UserSettings.Name = "User Settings";

            string? settingsDirectory = Path.GetDirectoryName(userSettingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            Assets.EnsureTracked(UserSettings);
            UserSettings.MarkDirty();
        }

        /// <summary>
        /// Loads the build settings from the current project directory.
        /// </summary>
        private static void LoadProjectBuildSettings()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.BuildSettingsPath is null)
            {
                LoadSandboxBuildSettings();
                return;
            }

            if (File.Exists(CurrentProject.BuildSettingsPath))
            {
                var settings = Assets.Load<BuildSettings>(CurrentProject.BuildSettingsPath);
                if (settings is not null)
                {
                    BuildSettings = settings;
                    if (GameSettings is not null)
                        GameSettings.BuildSettings = BuildSettings;
                    Debug.Out("Loaded project build settings.");
                }
            }
        }

        private static void LoadSandboxBuildSettings()
        {
            string? buildSettingsPath = GetSandboxBuildSettingsPath();
            if (string.IsNullOrWhiteSpace(buildSettingsPath) || Assets is null)
                return;

            if (File.Exists(buildSettingsPath))
            {
                var settings = Assets.Load<BuildSettings>(buildSettingsPath);
                if (settings is not null)
                {
                    settings.FilePath = buildSettingsPath;
                    settings.Name = "Build Settings";
                    Assets.EnsureTracked(settings);
                    BuildSettings = settings;
                    if (GameSettings is not null)
                        GameSettings.BuildSettings = BuildSettings;
                    Debug.Out("Loaded sandbox build settings.");
                }
                return;
            }

            var created = BuildSettings ?? new BuildSettings();
            created.FilePath = buildSettingsPath;
            created.Name = "Build Settings";
            Assets.EnsureTracked(created);
            BuildSettings = created;
            if (GameSettings is not null)
                GameSettings.BuildSettings = BuildSettings;
        }

        /// <summary>
        /// Saves the global editor preferences to the user profile.
        /// </summary>
        public static void SaveGlobalEditorPreferences()
        {
            if (Assets is null)
                return;

            var settings = GlobalEditorPreferences;
            if (settings is null)
                return;

            string? settingsPath = GetGlobalEditorPreferencesPath();
            if (string.IsNullOrWhiteSpace(settingsPath))
                return;

            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            settings.FilePath = settingsPath;
            settings.Name = "Global Editor Preferences";
            Assets.Save(settings);
            Debug.Out("Saved global editor preferences.");
        }

        /// <summary>
        /// Saves the editor preference overrides to the current project directory.
        /// </summary>
        public static void SaveProjectEditorPreferencesOverrides()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.ProjectDirectory is null)
            {
                SaveSandboxEditorPreferencesOverrides();
                return;
            }

            var settings = EditorPreferencesOverrides;
            if (settings is null)
                return;

            if (CurrentProject.EngineSettingsPath is null)
                return;

            string settingsPath = CurrentProject.EngineSettingsPath;
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            settings.FilePath = settingsPath;
            settings.Name = "Editor Preferences Overrides";
            Assets.Save(settings);
            Debug.Out("Saved project editor preference overrides.");
        }

        public static void SaveSandboxEditorPreferencesOverrides()
        {
            if (Assets is null)
                return;

            var settings = EditorPreferencesOverrides;
            if (settings is null)
                return;

            string? settingsPath = GetSandboxEditorPreferencesOverridePath();
            if (string.IsNullOrWhiteSpace(settingsPath))
                return;

            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            settings.FilePath = settingsPath;
            settings.Name = "Editor Preferences Overrides";
            Assets.Save(settings);
            Debug.Out("Saved sandbox editor preference overrides.");
        }

        /// <summary>
        /// Saves the user settings to the current project directory.
        /// </summary>
        public static void SaveProjectUserSettings()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.ProjectDirectory is null)
            {
                SaveSandboxUserSettings();
                return;
            }

            if (CurrentProject.UserSettingsPath is null)
                return;

            string userSettingsPath = CurrentProject.UserSettingsPath;

            UserSettings.FilePath = userSettingsPath;
            UserSettings.Name = "User Settings";
            Assets.EnsureTracked(UserSettings);

            string? settingsDirectory = Path.GetDirectoryName(userSettingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);
            
            Assets.Save(UserSettings);
            Debug.Out("Saved project user settings.");
        }

        public static void SaveSandboxUserSettings()
        {
            if (Assets is null)
                return;

            string? userSettingsPath = GetSandboxUserSettingsPath();
            if (string.IsNullOrWhiteSpace(userSettingsPath))
                return;

            UserSettings.FilePath = userSettingsPath;
            UserSettings.Name = "User Settings";
            Assets.EnsureTracked(UserSettings);

            string? settingsDirectory = Path.GetDirectoryName(userSettingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            Assets.Save(UserSettings);
            Debug.Out("Saved sandbox user settings.");
        }

        /// <summary>
        /// Saves both engine and user settings to the current project directory.
        /// </summary>
        public static void SaveProjectSettings()
        {
            SaveProjectEditorPreferencesOverrides();
            SaveProjectUserSettings();
            SaveProjectBuildSettings();
        }

        /// <summary>
        /// Saves the build settings to the current project directory.
        /// </summary>
        public static void SaveProjectBuildSettings()
        {
            if (Assets is null)
                return;

            if (CurrentProject?.ProjectDirectory is null)
            {
                SaveSandboxBuildSettings();
                return;
            }

            if (CurrentProject.BuildSettingsPath is null)
                return;

            string settingsPath = CurrentProject.BuildSettingsPath;
            string? directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var settings = BuildSettings ?? new BuildSettings();
            settings.FilePath = settingsPath;
            settings.Name = "Build Settings";

            Assets.Save(settings);
            Debug.Out("Saved project build settings.");
        }

        public static void SaveSandboxBuildSettings()
        {
            if (Assets is null)
                return;

            var settings = BuildSettings ?? new BuildSettings();

            string? settingsPath = GetSandboxBuildSettingsPath();
            if (string.IsNullOrWhiteSpace(settingsPath))
                return;

            string? directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            settings.FilePath = settingsPath;
            settings.Name = "Build Settings";

            Assets.Save(settings);
            Debug.Out("Saved sandbox build settings.");
        }

        /// <summary>
        /// Creates a new project at the specified directory.
        /// </summary>
        /// <param name="projectDirectoryPath">The directory where the project will be created.</param>
        /// <param name="projectName">The name of the project.</param>
        /// <returns>The created project, or null if creation failed.</returns>
        public static XRProject? CreateProject(string projectDirectoryPath, string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectDirectoryPath) || string.IsNullOrWhiteSpace(projectName))
                return null;

            try
            {
                var project = XRProject.CreateNew(projectDirectoryPath, projectName);
                
                // Save the project file
                if (Assets is not null && !string.IsNullOrWhiteSpace(project.FilePath))
                {
                    Assets.Save(project);
                }

                return project;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, $"Failed to create project at: {projectDirectoryPath}");
                return null;
            }
        }

        /// <summary>
        /// Creates a new project and loads it.
        /// </summary>
        /// <param name="projectDirectoryPath">The directory where the project will be created.</param>
        /// <param name="projectName">The name of the project.</param>
        /// <returns>True if the project was created and loaded successfully.</returns>
        public static bool CreateAndLoadProject(string projectDirectoryPath, string projectName)
        {
            var project = CreateProject(projectDirectoryPath, projectName);
            if (project is null)
                return false;

            return LoadProject(project);
        }

        private static void ConfigureProjectDirectories(XRProject project)
        {
            if (Assets is null)
                return;

            static void EnsureDirectory(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                Directory.CreateDirectory(path);
            }

            EnsureDirectory(project.AssetsDirectory);
            EnsureDirectory(project.MetadataDirectory);
            EnsureDirectory(project.PackagesDirectory);
            EnsureDirectory(project.IntermediateDirectory);
            EnsureDirectory(project.BuildDirectory);
            EnsureDirectory(project.ConfigDirectory);
            EnsureDirectory(project.CacheDirectory);

            if (project.AssetsDirectory is not null)
                Assets.GameAssetsPath = project.AssetsDirectory;
            if (project.MetadataDirectory is not null)
                Assets.GameMetadataPath = project.MetadataDirectory;
            if (project.PackagesDirectory is not null)
                Assets.PackagesPath = project.PackagesDirectory;
            if (project.IntermediateDirectory is not null)
                Assets.LibrariesPath = project.IntermediateDirectory;
            Assets.GameCachePath = project.CacheDirectory;
        }

        private static void LogUnexpectedProjectEntries(XRProject project)
        {
            var unexpectedEntries = project.GetUnexpectedRootEntries();
            if (unexpectedEntries.Count == 0)
                return;

            string message = string.Join(Environment.NewLine, unexpectedEntries.Select(entry => $" - {entry}"));
            Debug.LogWarning($"Unexpected items found in project root '{project.ProjectDirectory}':{Environment.NewLine}{message}");
        }
    }
}
