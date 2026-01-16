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

internal partial class CodeManager : XRSingleton<CodeManager>
{
    public const string SolutionFormatVersion = "12.00";
    public const string VisualStudioVersion = "17.4.33213.308";
    public const string MinimumVisualStudioVersion = "10.0.40219.1";
    public const string LegacyProjectGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    public const string ModernProjectGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";
    public const string TargetFramework = "net10.0-windows7.0";

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
            _ = CompileSolution();
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
            _ = CompileSolution(Config_Debug, Platform_AnyCPU);
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
            _ = CompileSolution(Config_Release, Platform_x64);
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
                new XElement("TargetFramework", TargetFramework),
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

    public bool CompileSolution(string config = Config_Debug, string platform = Platform_AnyCPU)
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
            return false;
        }

        BuildRequestData buildRequest = new(GetSolutionPath(), props, null, ["Build"], null);
        BuildResult buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequest);

        _isGameClientInvalid = false;

        if (buildResult.OverallResult == BuildResultCode.Success)
        {
            Debug.Out("Build succeeded.");
            GameCSProjLoader.Unload("GAME");
            GameCSProjLoader.LoadFromPath("GAME", GetBinaryPath(config, platform));
            return true;
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

            return false;
        }
    }

    public string GetBinaryPath(string config = Config_Debug, string platform = Platform_AnyCPU)
    {
        string projectName = GetProjectName();
        // Output path matches: <ProjectFolder>/Build/<Config>/<Platform>/net10.0-windows7.0/<ProjectName>.dll
        string outputPath = Path.Combine(Engine.Assets.LibrariesPath, projectName, "Build", config, platform, TargetFramework);
        return Path.Combine(outputPath, $"{projectName}.dll");
    }

    public string BuildLauncherExecutable(
        BuildSettings settings,
        string configuration,
        string platform,
        string startupAssetName,
        string engineSettingsAssetName,
        string userSettingsAssetName)
    {
        string projectName = GetProjectName();
        string launcherRoot = Path.Combine(Engine.Assets.LibrariesPath, projectName, "Launcher");
        Directory.CreateDirectory(launcherRoot);

        string gameProjectPath = Path.Combine(Engine.Assets.LibrariesPath, projectName, $"{projectName}.csproj");

        string programPath = Path.Combine(launcherRoot, "Program.cs");
        string launcherAssemblyName = $"{projectName}.Launcher";
        string launcherProjectPath = Path.Combine(launcherRoot, $"{launcherAssemblyName}.csproj");

        string programSource = BuildLauncherProgramSource(
            settings,
            startupAssetName,
            engineSettingsAssetName,
            userSettingsAssetName);
        File.WriteAllText(programPath, programSource, Encoding.UTF8);

        CreateLauncherProject(
            launcherProjectPath,
            programPath,
            launcherAssemblyName,
            platform,
            GetEngineAssemblyPaths(),
            includeGameProject: settings.PublishLauncherAsNativeAot ? gameProjectPath : null);

        if (settings.PublishLauncherAsNativeAot)
        {
            string publishDirectory = Path.Combine(launcherRoot, "Publish", configuration, platform, TargetFramework);
            Directory.CreateDirectory(publishDirectory);

            var extraProps = new Dictionary<string, string?>
            {
                ["PublishDir"] = EnsureTrailingSlash(publishDirectory),
                ["PublishAot"] = "true",
                ["SelfContained"] = "true",
                ["RuntimeIdentifier"] = "win-x64"
            };

            if (!BuildProjectFile(launcherProjectPath, configuration, platform, ["Publish"], extraProps, out string? publishLog))
            {
                Debug.Out(publishLog ?? "Failed to publish launcher project.");
                throw new InvalidOperationException("Failed to publish launcher executable. Check build output for details.");
            }

            string launcherExePath = Path.Combine(publishDirectory, $"{launcherAssemblyName}.exe");
            if (!File.Exists(launcherExePath))
                throw new FileNotFoundException("Launcher executable was not produced by publish.", launcherExePath);

            return launcherExePath;
        }
        else
        {
            if (!BuildProjectFile(launcherProjectPath, configuration, platform, out string? buildLog))
            {
                Debug.Out(buildLog ?? "Failed to build launcher project.");
                throw new InvalidOperationException("Failed to build launcher executable. Check build output for details.");
            }

            string outputDirectory = Path.Combine(launcherRoot, "Build", configuration, platform, TargetFramework);
            string launcherExePath = Path.Combine(outputDirectory, $"{launcherAssemblyName}.exe");
            if (!File.Exists(launcherExePath))
                throw new FileNotFoundException("Launcher executable was not produced by the build.", launcherExePath);

            return launcherExePath;
        }
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

    private static void CreateLauncherProject(
        string projectFilePath,
        string programFilePath,
        string assemblyName,
        string platform,
        IReadOnlyCollection<string> engineAssemblyPaths,
        string? includeGameProject)
    {
        string projectDirectory = Path.GetDirectoryName(projectFilePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(projectDirectory);

        string relativeProgramPath = Path.GetRelativePath(projectDirectory, programFilePath);

        var referenceElements = new List<XElement>();
        foreach (string assemblyPath in engineAssemblyPaths)
        {
            referenceElements.Add(new XElement("Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(assemblyPath)),
                new XElement("HintPath", assemblyPath)));
        }

        var project = new XDocument(
            new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("OutputType", "WinExe"),
                    new XElement("TargetFramework", TargetFramework),
                    new XElement("ImplicitUsings", "enable"),
                    new XElement("Nullable", "enable"),
                    new XElement("AllowUnsafeBlocks", "true"),
                    new XElement("PublishAot", "false"),
                    new XElement("SelfContained", "false"),
                    new XElement("Platforms", platform),
                    new XElement("RuntimeIdentifier", "win-x64"),
                    new XElement("AssemblyName", assemblyName),
                    new XElement("RootNamespace", assemblyName.Replace('.', '_')),
                    new XElement("BaseOutputPath", "Build")
                ),
                new XElement("ItemGroup",
                    new XElement("Compile", new XAttribute("Include", relativeProgramPath)))
            ));

        if (!string.IsNullOrWhiteSpace(includeGameProject))
        {
            string relativeGameProjectPath = Path.GetRelativePath(projectDirectory, includeGameProject);
            project.Root?.Add(
                new XElement("ItemGroup",
                    new XElement("ProjectReference", new XAttribute("Include", relativeGameProjectPath))));
        }

        if (referenceElements.Count > 0)
            project.Root?.Add(new XElement("ItemGroup", referenceElements));

        project.Save(projectFilePath);
    }

    private static bool BuildProjectFile(string projectFilePath, string configuration, string platform, out string? log)
    {
        var stringLogger = new StringLogger(LoggerVerbosity.Minimal);
        var projectCollection = new ProjectCollection();
        var buildParameters = new BuildParameters(projectCollection)
        {
            Loggers = [new ConsoleLogger(LoggerVerbosity.Minimal), stringLogger]
        };

        Dictionary<string, string?> props = new()
        {
            ["Configuration"] = configuration,
            ["Platform"] = platform
        };

        var request = new BuildRequestData(projectFilePath, props, null, ["Build"], null);
        BuildResult result = BuildManager.DefaultBuildManager.Build(buildParameters, request);
        log = stringLogger.GetFullLog();
        return result.OverallResult == BuildResultCode.Success;
    }

    private static bool BuildProjectFile(
        string projectFilePath,
        string configuration,
        string platform,
        string[] targets,
        IReadOnlyDictionary<string, string?>? extraProperties,
        out string? log)
    {
        var stringLogger = new StringLogger(LoggerVerbosity.Minimal);
        var projectCollection = new ProjectCollection();
        var buildParameters = new BuildParameters(projectCollection)
        {
            Loggers = [new ConsoleLogger(LoggerVerbosity.Minimal), stringLogger]
        };

        Dictionary<string, string?> props = new()
        {
            ["Configuration"] = configuration,
            ["Platform"] = platform
        };

        if (extraProperties is not null)
        {
            foreach (var kvp in extraProperties)
                props[kvp.Key] = kvp.Value;
        }

        var request = new BuildRequestData(projectFilePath, props, null, targets, null);
        BuildResult result = BuildManager.DefaultBuildManager.Build(buildParameters, request);
        log = stringLogger.GetFullLog();
        return result.OverallResult == BuildResultCode.Success;
    }

    private static string EnsureTrailingSlash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        char last = path[^1];
        if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
            return path;

        return path + Path.DirectorySeparatorChar;
    }

    private static string BuildLauncherProgramSource(
        BuildSettings settings,
        string startupAssetName,
        string engineSettingsAssetName,
        string userSettingsAssetName)
    {
        string configFolder = string.IsNullOrWhiteSpace(settings.ConfigOutputFolder)
            ? string.Empty
            : settings.ConfigOutputFolder;
        string configArchive = string.IsNullOrWhiteSpace(settings.ConfigArchiveName)
            ? "GameConfig.pak"
            : settings.ConfigArchiveName;

        static string EscapeForLiteral(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        string escapedConfigFolder = EscapeForLiteral(configFolder);
        string escapedArchive = EscapeForLiteral(configArchive);
        string escapedStartup = EscapeForLiteral(startupAssetName);
        string escapedEngine = EscapeForLiteral(engineSettingsAssetName);
        string escapedUser = EscapeForLiteral(userSettingsAssetName);

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using XREngine;");
        sb.AppendLine("using XREngine.Core.Files;");
        sb.AppendLine();
        sb.AppendLine("namespace GeneratedLauncher;");
        sb.AppendLine();
        sb.AppendLine("internal static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    [STAThread]");
        sb.AppendLine("    private static void Main(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string archivePath = string.IsNullOrWhiteSpace(\"{escapedConfigFolder}\") ?");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedArchive}\") :");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedConfigFolder}\", \"{escapedArchive}\");");
        sb.AppendLine();
        sb.AppendLine("        if (!File.Exists(archivePath))");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine($\"Config archive '{archivePath}' not found.\");");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        var startup = LoadAsset<GameStartupSettings>(archivePath, \"{escapedStartup}\");");
        sb.AppendLine("        if (startup is null)");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine(\"Failed to load startup settings from archive.\");");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        var editorPreferences = LoadAsset<EditorPreferences>(archivePath, \"{escapedEngine}\");");
        sb.AppendLine("        if (editorPreferences is not null)");
        sb.AppendLine("            Engine.EditorPreferences = editorPreferences;");
        sb.AppendLine();
        sb.AppendLine($"        var userSettings = LoadAsset<UserSettings>(archivePath, \"{escapedUser}\");");
        sb.AppendLine("        if (userSettings is not null)");
        sb.AppendLine("            Engine.UserSettings = userSettings;");
        sb.AppendLine();
        sb.AppendLine("        Engine.Run(startup, Engine.LoadOrGenerateGameState());");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static T? LoadAsset<T>(string archivePath, string assetPath)");
        sb.AppendLine("    {");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            byte[] yamlBytes = AssetPacker.GetAsset(archivePath, assetPath);");
        sb.AppendLine("            string yaml = Encoding.UTF8.GetString(yamlBytes);");
        sb.AppendLine("            return AssetManager.Deserializer.Deserialize<T>(yaml);");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine($\"Failed to load '{assetPath}': {ex.Message}\");");
        sb.AppendLine("            return default;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
