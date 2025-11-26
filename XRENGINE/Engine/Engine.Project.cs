using System.IO;
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

            // Unload any existing project
            UnloadProject();

            CurrentProject = project;

            // Set the game assets path to the project's Assets directory
            if (Assets is not null && project.AssetsDirectory is not null)
            {
                if (!Directory.Exists(project.AssetsDirectory))
                    Directory.CreateDirectory(project.AssetsDirectory);
                
                Assets.GameAssetsPath = project.AssetsDirectory;
            }

            // Load project-specific engine settings
            LoadProjectEngineSettings();

            // Load project-specific user settings
            LoadProjectUserSettings();

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
        /// Saves the engine settings to the current project directory.
        /// </summary>
        public static void SaveProjectEngineSettings()
        {
            if (CurrentProject?.ProjectDirectory is null || Assets is null)
                return;

            var settings = Rendering.Settings;
            if (settings is null)
                return;

            settings.FilePath = CurrentProject.EngineSettingsPath;
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

            var projectSettings = new ProjectUserSettings(UserSettings)
            {
                FilePath = CurrentProject.UserSettingsPath,
                Name = "User Settings"
            };
            
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
    }
}
