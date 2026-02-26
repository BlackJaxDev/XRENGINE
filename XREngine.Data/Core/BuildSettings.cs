using MemoryPack;
using System;
using System.ComponentModel;
using XREngine.Core.Files;

namespace XREngine
{
    [Serializable]
    [MemoryPackable]
    public partial class BuildSettings : XRAsset
    {
        public const int DefaultArchiveCopyBufferBytes = 1024 * 1024;
        public const int MinArchiveCopyBufferBytes = 64 * 1024;
        public const int MaxArchiveCopyBufferBytes = 16 * 1024 * 1024;

        private EBuildConfiguration _configuration = EBuildConfiguration.Development;
        private EBuildPlatform _platform = EBuildPlatform.Windows64;
        private string _outputSubfolder = "Game";
        private bool _cleanOutputDirectory = true;
        private bool _cookContent = true;
        private bool _buildManagedAssemblies = true;
        private bool _copyGameAssemblies = true;
        private bool _copyEngineBinaries = true;
        private bool _includePdbFiles = false;
        private bool _buildLauncherExecutable = true;
        private bool _publishLauncherAsNativeAot = false;
        private bool _generateConfigArchive = true;
        private bool _saveSettingsBeforeBuild = true;
        private string _contentArchiveName = "GameContent.pak";
        private string _configArchiveName = "GameConfig.pak";
        private string _contentOutputFolder = "Content";
        private string _configOutputFolder = "Config";
        private string _binariesOutputFolder = "Binaries";
        private string _launcherExecutableName = "Game.exe";
        private string _launcherDefineConstants = string.Empty;
        private int _archiveCopyBufferBytes = DefaultArchiveCopyBufferBytes;

        [Category("Build Target")]
        public EBuildConfiguration Configuration
        {
            get => _configuration;
            set => SetField(ref _configuration, value);
        }

        [Category("Build Target")]
        public EBuildPlatform Platform
        {
            get => _platform;
            set => SetField(ref _platform, value);
        }

        [Category("Build Target")]
        public string OutputSubfolder
        {
            get => _outputSubfolder;
            set => SetField(ref _outputSubfolder, value ?? string.Empty);
        }

        [Category("Build Steps")]
        public bool CleanOutputDirectory
        {
            get => _cleanOutputDirectory;
            set => SetField(ref _cleanOutputDirectory, value);
        }

        [Category("Build Steps")]
        public bool CookContent
        {
            get => _cookContent;
            set => SetField(ref _cookContent, value);
        }

        [Category("Build Steps")]
        public bool BuildManagedAssemblies
        {
            get => _buildManagedAssemblies;
            set => SetField(ref _buildManagedAssemblies, value);
        }

        [Category("Build Steps")]
        public bool CopyGameAssemblies
        {
            get => _copyGameAssemblies;
            set => SetField(ref _copyGameAssemblies, value);
        }

        [Category("Build Steps")]
        public bool CopyEngineBinaries
        {
            get => _copyEngineBinaries;
            set => SetField(ref _copyEngineBinaries, value);
        }

        [Category("Build Steps")]
        public bool IncludePdbFiles
        {
            get => _includePdbFiles;
            set => SetField(ref _includePdbFiles, value);
        }

        [Category("Build Steps")]
        public bool BuildLauncherExecutable
        {
            get => _buildLauncherExecutable;
            set => SetField(ref _buildLauncherExecutable, value);
        }

        [Category("Build Steps")]
        [Description("When enabled, the launcher EXE is produced via MSBuild Publish with PublishAot=true (NativeAOT). This is intended for shipping/cooked builds; keep disabled for editor/dev hot-reload workflows.")]
        public bool PublishLauncherAsNativeAot
        {
            get => _publishLauncherAsNativeAot;
            set => SetField(ref _publishLauncherAsNativeAot, value);
        }

        [Category("Build Steps")]
        public bool GenerateConfigArchive
        {
            get => _generateConfigArchive;
            set => SetField(ref _generateConfigArchive, value);
        }

        [Category("Build Steps")]
        public bool SaveSettingsBeforeBuild
        {
            get => _saveSettingsBeforeBuild;
            set => SetField(ref _saveSettingsBeforeBuild, value);
        }

        [Category("Output")]
        public string ContentArchiveName
        {
            get => _contentArchiveName;
            set => SetField(ref _contentArchiveName, value ?? string.Empty);
        }

        [Category("Output")]
        public string ConfigArchiveName
        {
            get => _configArchiveName;
            set => SetField(ref _configArchiveName, value ?? string.Empty);
        }

        [Category("Output")]
        public string ContentOutputFolder
        {
            get => _contentOutputFolder;
            set => SetField(ref _contentOutputFolder, value ?? string.Empty);
        }

        [Category("Output")]
        public string ConfigOutputFolder
        {
            get => _configOutputFolder;
            set => SetField(ref _configOutputFolder, value ?? string.Empty);
        }

        [Category("Output")]
        public string BinariesOutputFolder
        {
            get => _binariesOutputFolder;
            set => SetField(ref _binariesOutputFolder, value ?? string.Empty);
        }

        [Category("Output")]
        public string LauncherExecutableName
        {
            get => _launcherExecutableName;
            set => SetField(ref _launcherExecutableName, string.IsNullOrWhiteSpace(value) ? "Game.exe" : value);
        }

        [Category("Build Target")]
        [Description("Semicolon-delimited compile constants passed when building the generated launcher project.")]
        public string LauncherDefineConstants
        {
            get => _launcherDefineConstants;
            set => SetField(ref _launcherDefineConstants, value ?? string.Empty);
        }

        [Category("Build Target")]
        [Description("Buffer size in bytes used when repacking/compacting archives to copy existing payload data in chunks. Lower values reduce peak RAM usage; higher values can improve throughput.")]
        public int ArchiveCopyBufferBytes
        {
            get => _archiveCopyBufferBytes;
            set => SetField(
                ref _archiveCopyBufferBytes,
                Math.Clamp(value, MinArchiveCopyBufferBytes, MaxArchiveCopyBufferBytes));
        }
    }
}
