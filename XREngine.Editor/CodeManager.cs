using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using System.Text;
using System.Xml.Linq;
using XREngine;
using XREngine.Components.Scripting;
using XREngine.Core;
using XREngine.Rendering;

internal class CodeManager : XRSingleton<CodeManager>
{
    public const string SolutionFormatVersion = "12.00";
    public const string VisualStudioVersion = "17.4.33213.308";
    public const string MinimumVisualStudioVersion = "10.0.40219.1";
    public const string LegacyProjectGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    public const string ModernProjectGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";

    private bool _isGameClientInvalid = true;
    private bool _gameFilesChanged = true;

    private bool _compileOnChange = false;
    /// <summary>
    /// If true, the game client will be recompiled whenever a .cs file is changed and the editor is re-focused.
    /// </summary>
    public bool CompileOnChange
    {
        get => _compileOnChange;
        set => SetField(ref _compileOnChange, value);
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(CompileOnChange):
                if (CompileOnChange)
                    BeginMonitoring();
                else
                    EndMonitoring();
                break;
        }
    }

    private void BeginMonitoring()
    {
        Engine.Assets.GameFileChanged += VerifyCodeFileModified;
        Engine.Assets.GameFileCreated += VerifyCodeAssetsChanged;
        Engine.Assets.GameFileDeleted += VerifyCodeAssetsChanged;
        Engine.Assets.GameFileRenamed += VerifyCodeAssetsChanged;
        XRWindow.AnyWindowFocusChanged += AnyWindowFocusChanged;
    }

    private void EndMonitoring()
    {
        Engine.Assets.GameFileChanged -= VerifyCodeFileModified;
        Engine.Assets.GameFileCreated -= VerifyCodeAssetsChanged;
        Engine.Assets.GameFileDeleted -= VerifyCodeAssetsChanged;
        Engine.Assets.GameFileRenamed -= VerifyCodeAssetsChanged;
        XRWindow.AnyWindowFocusChanged -= AnyWindowFocusChanged;
    }

    /// <summary>
    /// Called when any window focus changes to check if the game client needs to be recompiled.
    /// </summary>
    /// <param name="window"></param>
    /// <param name="focused"></param>
    private void AnyWindowFocusChanged(XRWindow window, bool focused)
    {
        if (!focused || !_isGameClientInvalid)
            return;
        
        if (_gameFilesChanged)
            RemakeSolutionAsDLL();
        else
            CompileSolution();
    }
    /// <summary>
    /// Called when a code file is modified to mark the game client as invalid.
    /// ONLY compiles the game client, does not regenerate project files.
    /// </summary>
    /// <param name="e"></param>
    private void VerifyCodeFileModified(FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".cs"))
            _isGameClientInvalid = true;
    }
    /// <summary>
    /// Called when a code file is created, deleted, or renamed to mark the game client as invalid.
    /// Will regenerate project files and compile the game client.
    /// </summary>
    /// <param name="e"></param>
    private void VerifyCodeAssetsChanged(FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".cs"))
            return;
        
        _isGameClientInvalid = true;
        _gameFilesChanged = true;
    }

    public const string Config_Debug = "Debug";
    public const string Config_Release = "Release";

    public const string Platform_AnyCPU = "Any CPU";
    public const string Platform_x64 = "x64";
    public const string Platform_x86 = "x86";

    /// <summary>
    /// Gets the name of the project from the game settings or returns a default name.
    /// </summary>
    /// <returns></returns>
    public string GetProjectName()
        => Engine.GameSettings.Name ?? "GeneratedProject";

    /// <summary>
    /// Gets the path to the intermediate solution file for the game client.
    /// </summary>
    /// <returns></returns>
    public string GetSolutionPath()
        => Path.Combine(Engine.Assets.LibrariesPath, $"{GetProjectName()}.sln");

    /// <summary>
    /// Regenerates project files and optionally compiles the game client as a DLL.
    /// DLL builds are for being loaded dynamically by the editor or for 3rd party developers.
    /// </summary>
    /// <param name="compileNow"></param>
    public void RemakeSolutionAsDLL(bool compileNow = true)
    {
        string sourceRootFolder = Engine.Assets.GameAssetsPath;
        string outputFolder = Engine.Assets.LibrariesPath;
        string projectName = GetProjectName();

        string dllProjPath = Path.Combine(outputFolder, projectName, $"{projectName}.csproj");

        string[] builds = [Config_Debug, Config_Release];
        string[] platforms = [Platform_AnyCPU, Platform_x64];

        // Get the engine assembly references so the game code can access engine types
        string[] engineAssemblies = GetEngineAssemblyPaths();

        CreateCSProj(sourceRootFolder, dllProjPath, projectName, false, true, true, true, false, true, true, builds, platforms,
            assemblyReferencePaths: engineAssemblies);

        CreateSolutionFile(builds, platforms, (dllProjPath, Guid.NewGuid()));

        if (compileNow)
            CompileSolution(Config_Debug, Platform_AnyCPU);
    }

    /// <summary>
    /// Regenerates project files and optionally compiles the game client as an production-ready executable.
    /// Autogenerates code for launcher exe and packages dll into it as a single file.
    /// </summary>
    /// <param name="compileNow"></param>
    public void RemakeSolutionAsExe(bool compileNow = true)
    {
        string sourceRootFolder = Engine.Assets.GameAssetsPath;
        string outputFolder = Engine.Assets.LibrariesPath;
        string projectName = GetProjectName();

        string dllProjPath = Path.Combine(outputFolder, projectName, $"{projectName}.csproj");
        string exeProjPath = Path.Combine(outputFolder, projectName, $"{projectName}.exe.csproj");

        string[] builds = [Config_Release];
        string[] platforms = [Platform_x64];

        // Get the engine assembly references so the game code can access engine types
        string[] engineAssemblies = GetEngineAssemblyPaths();

        CreateCSProj(sourceRootFolder, dllProjPath, projectName, false, true, true, true, true, true, true, builds, platforms,
            assemblyReferencePaths: engineAssemblies);
        CreateCSProj(sourceRootFolder, exeProjPath, projectName, true, true, true, true, true, true, true, builds, platforms, 
            includedProjectPaths: [dllProjPath]);

        CreateSolutionFile(builds, platforms, (dllProjPath, Guid.NewGuid()), (exeProjPath, Guid.NewGuid()));

        if (compileNow)
            CompileSolution(Config_Release, Platform_x64);
    }

    private static void CreateCSProj(
        string sourceRootFolder,
        string projectFilePath,
        string rootNamespace,
        bool executable,
        bool allowUnsafeBlocks,
        bool nullableEnable,
        bool implicitUsings,
        bool aot,
        bool publishSingleFile,
        bool selfContained,
        string[] builds,
        string[] platforms,
        string? languageVersion = "14.0",
        (string name, string version)[]? packageReferences = null,
        string[]? includedProjectPaths = null,
        string[]? assemblyReferencePaths = null)
    {
        List<object> content =
        [
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("OutputType", executable ? "Exe" : "Library"),
                new XElement("TargetFramework", "net10.0-windows7.0"),
                new XElement("RootNamespace", rootNamespace),
                new XElement("AssemblyName", Path.GetFileNameWithoutExtension(projectFilePath)),
                new XElement("ImplicitUsings", implicitUsings ? "enable" : "disable"),
                new XElement("AllowUnsafeBlocks", allowUnsafeBlocks ? "true" : "false"),
                new XElement("PublishAot", aot ? "true" : "false"),
                new XElement("LangVersion", languageVersion), //https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version
                new XElement("Nullable", nullableEnable ? "enable" : "disable"),
                new XElement("Platforms", string.Join(";", platforms)),
                new XElement("PublishSingleFile", publishSingleFile ? "true" : "false"), //https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview?tabs=cli
                new XElement("SelfContained", selfContained ? "true" : "false"),
                new XElement("RuntimeIdentifier", "win-x64"),
                new XElement("BaseOutputPath", "Build")
            ),
        ];

        foreach (var build in builds)
            foreach (var platform in platforms)
            {
                content.Add(new XElement("PropertyGroup",
                    new XAttribute("Condition", $" '$(Configuration)|$(Platform)' == '{build}|{platform}' "),
                    new XElement("IsTrimmable", "True"),
                    new XElement("IsAotCompatible", "True"),
                    new XElement("Optimize", "False"),
                    new XElement("DebugType", "embedded")
                ));
            }
        
        content.Add(new XElement("ItemGroup",
            Directory.GetFiles(sourceRootFolder, "*.cs", SearchOption.AllDirectories)
                .Select(file => new XElement("Compile", new XAttribute("Include", file)))
        ));

        if (packageReferences is not null)
            content.Add(new XElement("ItemGroup",
                packageReferences.Select(x => new XElement("PackageReference",
                    new XAttribute("Include", x.name),
                    new XAttribute("Version", x.version)
                ))
            ));

        if (includedProjectPaths is not null)
            content.Add(new XElement("ItemGroup",
                includedProjectPaths.Select(x => new XElement("ProjectReference",
                    new XAttribute("Include", x)
                ))
            ));

        if (assemblyReferencePaths is not null)
            content.Add(new XElement("ItemGroup",
                assemblyReferencePaths.Select(x => new XElement("Reference",
                    new XAttribute("Include", Path.GetFileNameWithoutExtension(x)),
                    new XElement("HintPath", x)
                ))
            ));

        var project = new XDocument(new XElement("Project", content));
        Utility.EnsureDirPathExists(projectFilePath);
        project.Save(projectFilePath);
    }

    private void CreateSolutionFile(string[] builds, string[] platforms, params (string projectFilePath, Guid csprojGuid)[] projects)
    {
        var solutionContent = new StringBuilder();
        solutionContent.AppendLine($"Microsoft Visual Studio Solution File, Format Version {SolutionFormatVersion}");
        solutionContent.AppendLine($"# Visual Studio Version {VisualStudioVersion[..VisualStudioVersion.IndexOf('.')]}");
        solutionContent.AppendLine($"VisualStudioVersion = {VisualStudioVersion}");
        solutionContent.AppendLine($"MinimumVisualStudioVersion = {MinimumVisualStudioVersion}");

        foreach (var (projectFilePath, csprojGuid) in projects)
        {
            string projectGuid = csprojGuid.ToString("B").ToUpper();
            solutionContent.AppendLine($"Project(\"{{{ModernProjectGuid}}}\") = \"{Path.GetFileNameWithoutExtension(projectFilePath)}\", \"{projectFilePath}\", \"{projectGuid}\"");
            solutionContent.AppendLine("EndProject");
        }

        solutionContent.AppendLine("Global");
        {
            solutionContent.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            {
                foreach (var build in builds)
                    foreach (var platform in platforms)
                        solutionContent.AppendLine($"\t\t{build}|{platform} = {build}|{platform}");
            }
            solutionContent.AppendLine("\tEndGlobalSection");

            solutionContent.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            {
                foreach (var (projectFilePath, csprojGuid) in projects)
                {
                    string projectGuid = csprojGuid.ToString("B").ToUpper();
                    foreach (var build in builds)
                        foreach (var platform in platforms)
                        {
                            solutionContent.AppendLine($"\t\t{{{projectGuid}}}.{build}|{platform}.ActiveCfg = {build}|{platform}");
                            solutionContent.AppendLine($"\t\t{{{projectGuid}}}.{build}|{platform}.Build.0 = {build}|{platform}");
                        }
                }
            }
            solutionContent.AppendLine("\tEndGlobalSection");
        }
        solutionContent.AppendLine("EndGlobal");

        File.WriteAllText(GetSolutionPath(), solutionContent.ToString());
        _gameFilesChanged = false;
    }

    public void CompileSolution(string config = Config_Debug, string platform = Platform_AnyCPU)
    {
        // Create a custom string logger to capture all output
        var stringLogger = new StringLogger(LoggerVerbosity.Diagnostic);

        var projectCollection = new ProjectCollection();
        var buildParameters = new BuildParameters(projectCollection)
        {
            Loggers = [new ConsoleLogger(LoggerVerbosity.Detailed), stringLogger]
        };

        Dictionary<string, string?> props = new()
        {
            ["Configuration"] = config,
            ["Platform"] = platform
        };

        string solutionPath = GetSolutionPath();
        if (!File.Exists(solutionPath))
        {
            Debug.Out($"Solution file not found: {solutionPath}");
            return;
        }

        BuildRequestData buildRequest = new(GetSolutionPath(), props, null, ["Build"], null);
        BuildResult buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequest);

        _isGameClientInvalid = false;

        if (buildResult.OverallResult == BuildResultCode.Success)
        {
            Debug.Out("Build succeeded.");
            GameCSProjLoader.Unload("GAME");
            GameCSProjLoader.LoadFromPath("GAME", GetBinaryPath(config, platform));
        }
        else
        {
            Debug.Out("Build failed. Details below:");

            // Display all log messages from the string logger
            string log = stringLogger.GetFullLog();
            Debug.Out(log);

            // Extract and display errors from build result
            foreach (var target in buildResult.ResultsByTarget.Values)
            {
                foreach (var item in target.Items)
                {
                    if (item.GetMetadata("Code") is null)
                        continue;
                    
                    Debug.Out($"{item.GetMetadata("Code")}: {item.GetMetadata("Message")}");

                    // Include file and line information if available
                    string file = item.GetMetadata("File") ?? string.Empty;
                    string line = item.GetMetadata("Line") ?? string.Empty;
                    string column = item.GetMetadata("Column") ?? string.Empty;

                    if (!string.IsNullOrEmpty(file))
                    {
                        Debug.Out($"  at {file}:{line},{column}");
                    }
                }
            }
        }
    }

    // Custom logger that captures all output to a string
    private class StringLogger(LoggerVerbosity verbosity) : ILogger
    {
        private readonly StringBuilder _log = new();

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (sender, e) =>
                _log.AppendLine($"ERROR {e.Code}: {e.Message} ({e.File}:{e.LineNumber},{e.ColumnNumber})");

            eventSource.WarningRaised += (sender, e) =>
                _log.AppendLine($"WARNING {e.Code}: {e.Message} ({e.File}:{e.LineNumber},{e.ColumnNumber})");

            eventSource.MessageRaised += (sender, e) =>
                _log.AppendLine($"{e.Message}");
        }

        public string GetFullLog() => _log.ToString();

        public void Shutdown() { }

        public LoggerVerbosity Verbosity
        {
            get => verbosity;
            set => verbosity = value;
        }

        private string? _parameters = null;
        public string? Parameters
        {
            get => _parameters;
            set => _parameters = value;
        }
    }

    public string GetBinaryPath(string config = Config_Debug, string platform = Platform_AnyCPU)
    {
        string projectName = GetProjectName();
        // Output path matches: <ProjectFolder>/Build/<Config>/<Platform>/net10.0-windows7.0/<ProjectName>.dll
        string outputPath = Path.Combine(Engine.Assets.LibrariesPath, projectName, "Build", config, platform, "net10.0-windows7.0");
        return Path.Combine(outputPath, $"{projectName}.dll");
    }

    /// <summary>
    /// Gets the paths to all engine assemblies that should be referenced by game projects.
    /// This allows game code to use engine types like XRComponent, XRMenuItem, etc.
    /// </summary>
    /// <returns>Array of absolute paths to engine assembly DLLs</returns>
    private static string[] GetEngineAssemblyPaths()
    {
        // Get the directory where the editor is running from - this contains all engine assemblies
        string editorDir = AppContext.BaseDirectory;
        
        // Core engine assemblies that game code typically needs
        string[] assemblyNames =
        [
            "XREngine.dll",
            "XREngine.Data.dll",
            "XREngine.Extensions.dll",
            "XREngine.Animation.dll",
            "XREngine.Audio.dll",
            "XREngine.Input.dll",
            "XREngine.Modeling.dll"
        ];

        List<string> validPaths = [];
        foreach (string assemblyName in assemblyNames)
        {
            string path = Path.Combine(editorDir, assemblyName);
            if (File.Exists(path))
                validPaths.Add(path);
        }

        return [.. validPaths];
    }
}
