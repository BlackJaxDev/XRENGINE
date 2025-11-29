using System;
using System.Collections.Generic;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine
{
    /// <summary>
    /// Represents an XREngine project file (.xrproj).
    /// Contains references to engine settings, user settings, and project configuration.
    /// The project root (directory containing the .xrproj) must only contain the descriptor file and
    /// the standard project folders: Assets, Intermediate, Build, Packages, and Config.
    /// </summary>
    public class XRProject : XRAsset
    {
        public const string ProjectExtension = "xrproj";
        public const string EngineSettingsFileName = "engine_settings.asset";
        public const string UserSettingsFileName = "user_settings.asset";
        public const string AssetsDirectoryName = "Assets";
        public const string IntermediateDirectoryName = "Intermediate";
        public const string BuildDirectoryName = "Build";
        public const string PackagesDirectoryName = "Packages";
        public const string ConfigDirectoryName = "Config";

        private static readonly string[] RequiredDirectoryNames =
        [
            AssetsDirectoryName,
            IntermediateDirectoryName,
            BuildDirectoryName,
            PackagesDirectoryName,
            ConfigDirectoryName
        ];

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
        /// Gets the Intermediate directory path for generated outputs (solutions, DLLs, caches).
        /// </summary>
        public string? IntermediateDirectory => ProjectDirectory is null
            ? null
            : Path.Combine(ProjectDirectory, IntermediateDirectoryName);

        /// <summary>
        /// Gets the Build directory path for cooked builds.
        /// </summary>
        public string? BuildDirectory => ProjectDirectory is null
            ? null
            : Path.Combine(ProjectDirectory, BuildDirectoryName);

        /// <summary>
        /// Gets the Packages directory path for third-party content.
        /// </summary>
        public string? PackagesDirectory => ProjectDirectory is null
            ? null
            : Path.Combine(ProjectDirectory, PackagesDirectoryName);

        /// <summary>
        /// Gets the Config directory path which stores engine/user settings per project.
        /// </summary>
        public string? ConfigDirectory => ProjectDirectory is null
            ? null
            : Path.Combine(ProjectDirectory, ConfigDirectoryName);

        /// <summary>
        /// Gets the path to the engine settings file for this project.
        /// </summary>
        public string? EngineSettingsPath => ConfigDirectory is null
            ? null
            : Path.Combine(ConfigDirectory, EngineSettingsFileName);

        /// <summary>
        /// Gets the path to the user settings file for this project.
        /// </summary>
        public string? UserSettingsPath => ConfigDirectory is null
            ? null
            : Path.Combine(ConfigDirectory, UserSettingsFileName);

        /// <summary>
        /// Creates a new project directory structure at the specified path.
        /// </summary>
        /// <param name="projectDirectoryPath">The path where the project directory should be created.</param>
        /// <param name="projectName">The name of the project.</param>
        /// <returns>The created XRProject instance.</returns>
        public static XRProject CreateNew(string projectDirectoryPath, string projectName)
        {
            EnsureProjectDirectory(projectDirectoryPath);

            // Create the project file
            var project = new XRProject(projectName)
            {
                FilePath = Path.Combine(projectDirectoryPath, $"{projectName}.{ProjectExtension}")
            };

            project.EnsureStructure();

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

            var project = Engine.Assets?.Load<XRProject>(projectFilePath);
            project?.EnsureStructure();
            return project;
        }

        /// <summary>
        /// Ensures the standard directory structure exists for this project.
        /// </summary>
        public void EnsureStructure()
        {
            if (ProjectDirectory is null)
                return;

            EnsureProjectDirectory(ProjectDirectory);
        }

        private static void EnsureProjectDirectory(string projectDirectoryPath)
        {
            Directory.CreateDirectory(projectDirectoryPath);

            foreach (string folder in RequiredDirectoryNames)
            {
                Directory.CreateDirectory(Path.Combine(projectDirectoryPath, folder));
            }
        }

        /// <summary>
        /// Returns any unexpected files or directories found at the project root (besides the .xrproj and required folders).
        /// </summary>
        public IReadOnlyList<string> GetUnexpectedRootEntries()
        {
            if (ProjectDirectory is null)
                return Array.Empty<string>();

            string? projectFileName = string.IsNullOrWhiteSpace(FilePath)
                ? null
                : Path.GetFileName(FilePath);

            HashSet<string> allowedEntries = new(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in RequiredDirectoryNames)
                allowedEntries.Add(folder);
            if (projectFileName is not null)
                allowedEntries.Add(projectFileName);

            List<string> unexpected = [];
            foreach (string entry in Directory.EnumerateFileSystemEntries(ProjectDirectory))
            {
                string? name = Path.GetFileName(entry);
                if (name is null)
                    continue;

                if (!allowedEntries.Contains(name))
                    unexpected.Add(entry);
            }

            return unexpected;
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
