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

            // Load project-specific engine settings
            LoadProjectEngineSettings();

            // Load project-specific user settings
            LoadProjectUserSettings();

            // Load project-specific build settings
            LoadProjectBuildSettings();

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
        /// Loads the engine settings from the current project directory.
        /// </summary>
        private static void LoadProjectEngineSettings()
        {
            if (CurrentProject?.EngineSettingsPath is null || Assets is null)
                return;

            if (File.Exists(CurrentProject.EngineSettingsPath))
            {
                var settings = Assets.Load<EngineSettings>(CurrentProject.EngineSettingsPath);
                if (settings is not null)
                {
                    Rendering.Settings = settings;
                    Debug.Out("Loaded project engine settings.");
                }
            }
        }

        /// <summary>
        /// Loads the user settings from the current project directory.
        /// </summary>
        private static void LoadProjectUserSettings()
        {
            if (CurrentProject?.UserSettingsPath is null || Assets is null)
                return;

            if (File.Exists(CurrentProject.UserSettingsPath))
            {
                var projectSettings = Assets.Load<ProjectUserSettings>(CurrentProject.UserSettingsPath);
                if (projectSettings?.Settings is not null)
                {
                    UserSettings = projectSettings.Settings;
                    Debug.Out("Loaded project user settings.");
                }
            }
        }

        /// <summary>
        /// Loads the build settings from the current project directory.
        /// </summary>
        private static void LoadProjectBuildSettings()
        {
            if (CurrentProject?.BuildSettingsPath is null || Assets is null)
                return;

            if (File.Exists(CurrentProject.BuildSettingsPath))
            {
                var settings = Assets.Load<BuildSettings>(CurrentProject.BuildSettingsPath);
                if (settings is not null)
                {
                    BuildSettings = settings;
                    Debug.Out("Loaded project build settings.");
                }
            }
        }

        /// <summary>
        /// Saves the engine settings to the current project directory.
        /// </summary>
        public static void SaveProjectEngineSettings()
        {
            if (CurrentProject?.ProjectDirectory is null || Assets is null)
                return;

            var settings = Rendering.Settings;
            if (settings is null)
                return;

            if (CurrentProject.EngineSettingsPath is null)
                return;

            string settingsPath = CurrentProject.EngineSettingsPath;
            string? settingsDirectory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);

            settings.FilePath = settingsPath;
            Assets.Save(settings);
            Debug.Out("Saved project engine settings.");
        }

        /// <summary>
        /// Saves the user settings to the current project directory.
        /// </summary>
        public static void SaveProjectUserSettings()
        {
            if (CurrentProject?.ProjectDirectory is null || Assets is null)
                return;

            if (CurrentProject.UserSettingsPath is null)
                return;

            string userSettingsPath = CurrentProject.UserSettingsPath;
            var projectSettings = new ProjectUserSettings(UserSettings)
            {
                FilePath = userSettingsPath,
                Name = "User Settings"
            };

            string? settingsDirectory = Path.GetDirectoryName(userSettingsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
                Directory.CreateDirectory(settingsDirectory);
            
            Assets.Save(projectSettings);
            Debug.Out("Saved project user settings.");
        }

        /// <summary>
        /// Saves both engine and user settings to the current project directory.
        /// </summary>
        public static void SaveProjectSettings()
        {
            SaveProjectEngineSettings();
            SaveProjectUserSettings();
            SaveProjectBuildSettings();
        }

        /// <summary>
        /// Saves the build settings to the current project directory.
        /// </summary>
        public static void SaveProjectBuildSettings()
        {
            if (CurrentProject?.ProjectDirectory is null || Assets is null)
                return;

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
            EnsureDirectory(project.PackagesDirectory);
            EnsureDirectory(project.IntermediateDirectory);
            EnsureDirectory(project.BuildDirectory);
            EnsureDirectory(project.ConfigDirectory);

            if (project.AssetsDirectory is not null)
                Assets.GameAssetsPath = project.AssetsDirectory;
            if (project.PackagesDirectory is not null)
                Assets.PackagesPath = project.PackagesDirectory;
            if (project.IntermediateDirectory is not null)
                Assets.LibrariesPath = project.IntermediateDirectory;
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
