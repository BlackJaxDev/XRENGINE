using System.IO;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Represents an XREngine project file (.xrproj).
    /// Contains references to engine settings, user settings, and project configuration.
    /// The project file is stored in the root of the project directory, alongside the Assets folder.
    /// </summary>
    public class XRProject : XRAsset
    {
        public const string ProjectExtension = "xrproj";
        public const string EngineSettingsFileName = "engine_settings.asset";
        public const string UserSettingsFileName = "user_settings.asset";
        public const string AssetsDirectoryName = "Assets";

        private string _projectName = "New Project";
        private string _projectVersion = "1.0.0";
        private string _engineVersion = "1.0.0";
        private string _description = string.Empty;
        private string _author = string.Empty;
        private string _startupScenePath = string.Empty;

        public XRProject() { }

        public XRProject(string projectName)
        {
            _projectName = projectName;
            Name = projectName;
        }

        /// <summary>
        /// The display name of the project.
        /// </summary>
        public string ProjectName
        {
            get => _projectName;
            set => SetField(ref _projectName, value);
        }

        /// <summary>
        /// The version of the project.
        /// </summary>
        public string ProjectVersion
        {
            get => _projectVersion;
            set => SetField(ref _projectVersion, value);
        }

        /// <summary>
        /// The version of XREngine this project was created with.
        /// </summary>
        public string EngineVersion
        {
            get => _engineVersion;
            set => SetField(ref _engineVersion, value);
        }

        /// <summary>
        /// A description of the project.
        /// </summary>
        public string Description
        {
            get => _description;
            set => SetField(ref _description, value);
        }

        /// <summary>
        /// The author of the project.
        /// </summary>
        public string Author
        {
            get => _author;
            set => SetField(ref _author, value);
        }

        /// <summary>
        /// The relative path to the startup scene within the Assets directory.
        /// </summary>
        public string StartupScenePath
        {
            get => _startupScenePath;
            set => SetField(ref _startupScenePath, value);
        }

        /// <summary>
        /// Gets the directory containing the project file.
        /// </summary>
        public string? ProjectDirectory => string.IsNullOrWhiteSpace(FilePath) 
            ? null 
            : Path.GetDirectoryName(FilePath);

        /// <summary>
        /// Gets the Assets directory path for this project.
        /// </summary>
        public string? AssetsDirectory => ProjectDirectory is null 
            ? null 
            : Path.Combine(ProjectDirectory, AssetsDirectoryName);

        /// <summary>
        /// Gets the path to the engine settings file for this project.
        /// </summary>
        public string? EngineSettingsPath => ProjectDirectory is null 
            ? null 
            : Path.Combine(ProjectDirectory, EngineSettingsFileName);

        /// <summary>
        /// Gets the path to the user settings file for this project.
        /// </summary>
        public string? UserSettingsPath => ProjectDirectory is null 
            ? null 
            : Path.Combine(ProjectDirectory, UserSettingsFileName);

        /// <summary>
        /// Creates a new project directory structure at the specified path.
        /// </summary>
        /// <param name="projectDirectoryPath">The path where the project directory should be created.</param>
        /// <param name="projectName">The name of the project.</param>
        /// <returns>The created XRProject instance.</returns>
        public static XRProject CreateNew(string projectDirectoryPath, string projectName)
        {
            // Ensure the project directory exists
            Directory.CreateDirectory(projectDirectoryPath);
            
            // Create the Assets subdirectory
            string assetsPath = Path.Combine(projectDirectoryPath, AssetsDirectoryName);
            Directory.CreateDirectory(assetsPath);

            // Create the project file
            var project = new XRProject(projectName)
            {
                FilePath = Path.Combine(projectDirectoryPath, $"{projectName}.{ProjectExtension}")
            };

            return project;
        }

        /// <summary>
        /// Loads a project from the specified .xrproj file path.
        /// </summary>
        /// <param name="projectFilePath">The path to the .xrproj file.</param>
        /// <returns>The loaded XRProject, or null if loading failed.</returns>
        public static XRProject? Load(string projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
                return null;

            return Engine.Assets?.Load<XRProject>(projectFilePath);
        }

        /// <summary>
        /// Saves the project file to disk.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrWhiteSpace(FilePath))
                return;

            Engine.Assets?.Save(this);
        }
    }
}
