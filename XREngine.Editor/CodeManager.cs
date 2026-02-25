using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using DiagnosticsProcess = System.Diagnostics.Process;
using DiagnosticsProcessStartInfo = System.Diagnostics.ProcessStartInfo;
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

        // Place launcher first so IDEs default to running the generated launcher entrypoint.
        CreateSolutionFile(builds, platforms, (exeProjPath, Guid.NewGuid()), (dllProjPath, Guid.NewGuid()));

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
                new XElement("EnableDefaultCompileItems", "false"),
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
                            solutionContent.AppendLine($"\t\t{projectGuid}.{build}|{platform}.ActiveCfg = {build}|{platform}");
                            solutionContent.AppendLine($"\t\t{projectGuid}.{build}|{platform}.Build.0 = {build}|{platform}");
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
            Loggers = [stringLogger]
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

        BuildResult? buildResult = null;
        bool inProcessBuildSucceeded;
        try
        {
            BuildRequestData buildRequest = new(solutionPath, props, null, ["Build"], null);
            buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequest);
            inProcessBuildSucceeded = buildResult.OverallResult == BuildResultCode.Success;
        }
        catch (Exception ex)
        {
            Debug.Out($"In-process build threw an exception: {ex.Message}");
            inProcessBuildSucceeded = false;
        }

        if (inProcessBuildSucceeded)
        {
            _isGameClientInvalid = false;
            Debug.Out("Build succeeded.");
            GameCSProjLoader.Unload("GAME");
            GameCSProjLoader.LoadFromPath("GAME", GetBinaryPath(config, platform));
            return true;
        }

        Debug.Out("In-process build failed. Retrying with dotnet msbuild...");
        if (BuildProjectFile(solutionPath, config, platform, out string? cliBuildLog))
        {
            _isGameClientInvalid = false;
            Debug.Out("Build succeeded.");
            GameCSProjLoader.Unload("GAME");
            GameCSProjLoader.LoadFromPath("GAME", GetBinaryPath(config, platform));
            return true;
        }

        Debug.Out("Build failed. Details below:");
        // Display all log messages from the in-process attempt
        string log = stringLogger.GetFullLog();
        Debug.Out(log);
        // Display fallback CLI logs if available
        if (!string.IsNullOrWhiteSpace(cliBuildLog))
            Debug.Out(cliBuildLog);

        // Extract and display errors from build result
        if (buildResult is not null)
        {
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
                        Debug.Out($"  at {file}:{line},{column}");
                }
            }
        }

        return false;
    }

    public string GetBinaryPath(string config = Config_Debug, string platform = Platform_AnyCPU)
    {
        string projectName = GetProjectName();
        string dllName = $"{projectName}.dll";
        string buildRoot = Path.Combine(Engine.Assets.LibrariesPath, projectName, "Build");

        string[] candidatePaths =
        [
            Path.Combine(buildRoot, config, platform, TargetFramework, dllName),
            Path.Combine(buildRoot, platform, config, TargetFramework, dllName),
            Path.Combine(buildRoot, config, TargetFramework, dllName),
            Path.Combine(buildRoot, platform, TargetFramework, dllName)
        ];

        foreach (string candidatePath in candidatePaths)
        {
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        if (Directory.Exists(buildRoot))
        {
            string refSegment = $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}";
            string[] matches = Directory.GetFiles(buildRoot, dllName, SearchOption.AllDirectories);
            string[] preferred = [.. matches.Where(path => !path.Contains(refSegment, StringComparison.OrdinalIgnoreCase))];

            string[] selected = preferred.Length > 0 ? preferred : matches;
            if (selected.Length > 0)
            {
                Array.Sort(selected, (left, right) =>
                    File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
                return selected[0];
            }
        }

        return Path.Combine(buildRoot, config, platform, TargetFramework, dllName);
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
            includeGameProject: gameProjectPath);

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
                throw new InvalidOperationException($"Failed to publish launcher executable. {publishLog}");
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
                throw new InvalidOperationException($"Failed to build launcher executable. {buildLog}");
            }

            return ResolveBuiltLauncherExecutablePath(launcherRoot, launcherAssemblyName, configuration, platform);
        }
    }

    private static string ResolveBuiltLauncherExecutablePath(
        string launcherRoot,
        string launcherAssemblyName,
        string configuration,
        string platform)
    {
        string fileName = $"{launcherAssemblyName}.exe";
        string buildRoot = Path.Combine(launcherRoot, "Build");

        string[] candidatePaths =
        [
            Path.Combine(buildRoot, platform, configuration, TargetFramework, "win-x64", fileName),
            Path.Combine(buildRoot, configuration, platform, TargetFramework, "win-x64", fileName),
            Path.Combine(buildRoot, platform, configuration, TargetFramework, fileName),
            Path.Combine(buildRoot, configuration, platform, TargetFramework, fileName),
            Path.Combine(buildRoot, configuration, TargetFramework, fileName),
            Path.Combine(buildRoot, TargetFramework, fileName)
        ];

        foreach (string candidatePath in candidatePaths)
        {
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        if (Directory.Exists(buildRoot))
        {
            string[] matches = Directory.GetFiles(buildRoot, fileName, SearchOption.AllDirectories);
            if (matches.Length > 0)
            {
                Array.Sort(matches, (left, right) =>
                    File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
                return matches[0];
            }
        }

        throw new FileNotFoundException("Launcher executable was not produced by the build.", Path.Combine(buildRoot, fileName));
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
                    new XElement("EnableDefaultCompileItems", "false"),
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
        => BuildProjectFile(projectFilePath, configuration, platform, ["Build"], extraProperties: null, out log);

    private static bool BuildProjectFile(
        string projectFilePath,
        string configuration,
        string platform,
        string[] targets,
        IReadOnlyDictionary<string, string?>? extraProperties,
        out string? log)
    {
        var startInfo = new DiagnosticsProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projectFilePath) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectFilePath);
        startInfo.ArgumentList.Add("/restore");
        startInfo.ArgumentList.Add("/nologo");
        startInfo.ArgumentList.Add($"/t:{string.Join(";", targets)}");
        startInfo.ArgumentList.Add($"/p:Configuration={configuration}");
        startInfo.ArgumentList.Add($"/p:Platform={platform}");

        if (extraProperties is not null)
        {
            foreach (var kvp in extraProperties)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                    continue;

                string value = kvp.Value ?? string.Empty;
                startInfo.ArgumentList.Add($"/p:{kvp.Key}={value}");
            }
        }

        using DiagnosticsProcess process = DiagnosticsProcess.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start dotnet msbuild for '{projectFilePath}'.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        log = string.Concat(stdout, string.IsNullOrWhiteSpace(stderr) ? string.Empty : Environment.NewLine + stderr);
        return process.ExitCode == 0;
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
        string contentFolder = string.IsNullOrWhiteSpace(settings.ContentOutputFolder)
            ? string.Empty
            : settings.ContentOutputFolder;
        string contentArchive = string.IsNullOrWhiteSpace(settings.ContentArchiveName)
            ? "GameContent.pak"
            : settings.ContentArchiveName;
        const string commonAssetsArchive = "CommonAssets.pak";

        static string EscapeForLiteral(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        string escapedConfigFolder = EscapeForLiteral(configFolder);
        string escapedConfigArchive = EscapeForLiteral(configArchive);
        string escapedContentFolder = EscapeForLiteral(contentFolder);
        string escapedContentArchive = EscapeForLiteral(contentArchive);
        string escapedCommonAssetsArchive = EscapeForLiteral(commonAssetsArchive);
        string escapedStartup = EscapeForLiteral(startupAssetName);
        string escapedEngine = EscapeForLiteral(engineSettingsAssetName);
        string escapedUser = EscapeForLiteral(userSettingsAssetName);

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.IO;");
        sb.AppendLine("using System.Security.Cryptography;");
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
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedConfigArchive}\") :");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedConfigFolder}\", \"{escapedConfigArchive}\");");
        sb.AppendLine();
        sb.AppendLine($"        string contentArchivePath = string.IsNullOrWhiteSpace(\"{escapedContentFolder}\") ?");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedContentArchive}\") :");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedContentFolder}\", \"{escapedContentArchive}\");");
        sb.AppendLine();
        sb.AppendLine($"        string commonAssetsArchivePath = string.IsNullOrWhiteSpace(\"{escapedContentFolder}\") ?");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedCommonAssetsArchive}\") :");
        sb.AppendLine($"            Path.Combine(AppContext.BaseDirectory, \"{escapedContentFolder}\", \"{escapedCommonAssetsArchive}\");");
        sb.AppendLine();
        sb.AppendLine("        if (!File.Exists(archivePath))");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine($\"Config archive '{archivePath}' not found.\");");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        var runMode = ResolveRunMode(args);");
        sb.AppendLine("        ConfigureAssetRoots(runMode, contentArchivePath, commonAssetsArchivePath);");
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
        sb.AppendLine("            Engine.GlobalEditorPreferences = editorPreferences;");
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
        sb.AppendLine("            byte[] cookedBytes = AssetPacker.GetAsset(archivePath, assetPath);");
        sb.AppendLine("            return CookedAssetReader.LoadAsset<T>(cookedBytes);");
        sb.AppendLine("        }");
        sb.AppendLine("        catch (Exception ex)");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine($\"Failed to load '{assetPath}': {ex.Message}\");");
        sb.AppendLine("            return default;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private enum AssetRunMode");
        sb.AppendLine("    {");
        sb.AppendLine("        Dev,");
        sb.AppendLine("        Publish");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static AssetRunMode ResolveRunMode(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        for (int i = 0; i < args.Length; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            string arg = args[i];");
        sb.AppendLine("            if (!string.Equals(arg, \"--mode\", StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("                continue;");
        sb.AppendLine();
        sb.AppendLine("            if (i + 1 >= args.Length)");
        sb.AppendLine("                break;");
        sb.AppendLine();
        sb.AppendLine("            return ParseRunMode(args[i + 1]);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        string? envMode = Environment.GetEnvironmentVariable(\"XRE_GAME_MODE\");");
        sb.AppendLine("        if (!string.IsNullOrWhiteSpace(envMode))");
        sb.AppendLine("            return ParseRunMode(envMode);");
        sb.AppendLine();
        sb.AppendLine("        return AssetRunMode.Publish;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static AssetRunMode ParseRunMode(string value)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.Equals(value, \"dev\", StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("            return AssetRunMode.Dev;");
        sb.AppendLine();
        sb.AppendLine("        if (string.Equals(value, \"publish\", StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("            return AssetRunMode.Publish;");
        sb.AppendLine();
        sb.AppendLine("        return AssetRunMode.Publish;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void ConfigureAssetRoots(AssetRunMode runMode, string gameArchivePath, string engineArchivePath)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (runMode)");
        sb.AppendLine("        {");
        sb.AppendLine("            case AssetRunMode.Dev:");
        sb.AppendLine("                ConfigureDevAssetRoots();");
        sb.AppendLine("                break;");
        sb.AppendLine();
        sb.AppendLine("            default:");
        sb.AppendLine("                ConfigurePublishAssetRoots(gameArchivePath, engineArchivePath);");
        sb.AppendLine("                break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void ConfigureDevAssetRoots()");
        sb.AppendLine("    {");
        sb.AppendLine("        Environment.SetEnvironmentVariable(\"XRE_ASSET_RUNTIME_MODE\", \"dev\");");
        sb.AppendLine();
        sb.AppendLine("        if (TryFindNearestDirectoryWithAssets(AppContext.BaseDirectory, out string? gameAssetsPath))");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_GAME_ASSETS_PATH\", gameAssetsPath);");
        sb.AppendLine("        else");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_GAME_ASSETS_PATH\", null);");
        sb.AppendLine();
        sb.AppendLine("        if (TryFindNearestDirectoryWithEngineAssets(AppContext.BaseDirectory, out string? engineAssetsPath))");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_ENGINE_ASSETS_PATH\", engineAssetsPath);");
        sb.AppendLine("        else");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_ENGINE_ASSETS_PATH\", null);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void ConfigurePublishAssetRoots(string gameArchivePath, string engineArchivePath)");
        sb.AppendLine("    {");
        sb.AppendLine("        Environment.SetEnvironmentVariable(\"XRE_ASSET_RUNTIME_MODE\", \"publish\");");
        sb.AppendLine();
        sb.AppendLine("        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);");
        sb.AppendLine("        if (string.IsNullOrWhiteSpace(localAppData))");
        sb.AppendLine("            return;");
        sb.AppendLine();
        sb.AppendLine("        string cacheRoot = Path.Combine(localAppData, \"XREngine\", \"RuntimeArchiveCache\", ComputeStableToken(AppContext.BaseDirectory));");
        sb.AppendLine();
        sb.AppendLine("        if (File.Exists(gameArchivePath))");
        sb.AppendLine("        {");
        sb.AppendLine("            string gameAssetsRoot = Path.Combine(cacheRoot, \"GameAssets\");");
        sb.AppendLine("            ExtractArchiveIfNeeded(gameArchivePath, gameAssetsRoot);");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_GAME_ASSETS_PATH\", gameAssetsRoot);");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_GAME_ASSETS_PATH\", null);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (File.Exists(engineArchivePath))");
        sb.AppendLine("        {");
        sb.AppendLine("            string engineAssetsRoot = Path.Combine(cacheRoot, \"CommonAssets\");");
        sb.AppendLine("            ExtractArchiveIfNeeded(engineArchivePath, engineAssetsRoot);");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_ENGINE_ASSETS_PATH\", engineAssetsRoot);");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            Environment.SetEnvironmentVariable(\"XRE_ENGINE_ASSETS_PATH\", null);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static bool TryFindNearestDirectoryWithAssets(string startPath, out string? assetsPath)");
        sb.AppendLine("    {");
        sb.AppendLine("        var dir = new DirectoryInfo(startPath);");
        sb.AppendLine("        while (dir is not null)");
        sb.AppendLine("        {");
        sb.AppendLine("            string candidate = Path.Combine(dir.FullName, \"Assets\");");
        sb.AppendLine("            if (Directory.Exists(candidate))");
        sb.AppendLine("            {");
        sb.AppendLine("                assetsPath = candidate;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            dir = dir.Parent;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        assetsPath = null;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static bool TryFindNearestDirectoryWithEngineAssets(string startPath, out string? engineAssetsPath)");
        sb.AppendLine("    {");
        sb.AppendLine("        var dir = new DirectoryInfo(startPath);");
        sb.AppendLine("        while (dir is not null)");
        sb.AppendLine("        {");
        sb.AppendLine("            string candidate = Path.Combine(dir.FullName, \"Build\", \"CommonAssets\");");
        sb.AppendLine("            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, \"Shaders\")))");
        sb.AppendLine("            {");
        sb.AppendLine("                engineAssetsPath = candidate;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            dir = dir.Parent;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        engineAssetsPath = null;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void ExtractArchiveIfNeeded(string archivePath, string destinationRoot)");
        sb.AppendLine("    {");
        sb.AppendLine("        var archiveInfo = new FileInfo(archivePath);");
        sb.AppendLine("        string stamp = BuildArchiveStamp(archiveInfo);");
        sb.AppendLine("        string stampPath = Path.Combine(destinationRoot, \".archive.stamp\");");
        sb.AppendLine();
        sb.AppendLine("        if (Directory.Exists(destinationRoot) && File.Exists(stampPath))");
        sb.AppendLine("        {");
        sb.AppendLine("            string existingStamp = File.ReadAllText(stampPath);");
        sb.AppendLine("            if (string.Equals(existingStamp, stamp, StringComparison.Ordinal))");
        sb.AppendLine("                return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        if (Directory.Exists(destinationRoot))");
        sb.AppendLine("            Directory.Delete(destinationRoot, recursive: true);");
        sb.AppendLine();
        sb.AppendLine("        Directory.CreateDirectory(destinationRoot);");
        sb.AppendLine();
        sb.AppendLine("        var info = AssetPacker.ReadArchiveInfo(archivePath);");
        sb.AppendLine("        string destinationRootFullPath = Path.GetFullPath(destinationRoot);");
        sb.AppendLine();
        sb.AppendLine("        foreach (var entry in info.Entries)");
        sb.AppendLine("        {");
        sb.AppendLine("            string relativePath = entry.Path.Replace('/', Path.DirectorySeparatorChar)");
        sb.AppendLine("                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);");
        sb.AppendLine("            if (string.IsNullOrWhiteSpace(relativePath))");
        sb.AppendLine("                continue;");
        sb.AppendLine();
        sb.AppendLine("            string outputPath = Path.GetFullPath(Path.Combine(destinationRootFullPath, relativePath));");
        sb.AppendLine("            if (!outputPath.StartsWith(destinationRootFullPath, StringComparison.OrdinalIgnoreCase))");
        sb.AppendLine("                continue;");
        sb.AppendLine();
        sb.AppendLine("            string? outputDirectory = Path.GetDirectoryName(outputPath);");
        sb.AppendLine("            if (!string.IsNullOrWhiteSpace(outputDirectory))");
        sb.AppendLine("                Directory.CreateDirectory(outputDirectory);");
        sb.AppendLine();
        sb.AppendLine("            byte[] data = AssetPacker.DecompressEntry(archivePath, entry);");
        sb.AppendLine("            File.WriteAllBytes(outputPath, data);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        File.WriteAllText(stampPath, stamp);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static string BuildArchiveStamp(FileInfo archiveInfo)");
        sb.AppendLine("        => $\"{archiveInfo.Length}:{archiveInfo.LastWriteTimeUtc.Ticks}\";");
        sb.AppendLine();
        sb.AppendLine("    private static string ComputeStableToken(string value)");
        sb.AppendLine("    {");
        sb.AppendLine("        byte[] bytes = Encoding.UTF8.GetBytes(value);");
        sb.AppendLine("        byte[] hash = SHA256.HashData(bytes);");
        sb.AppendLine("        return Convert.ToHexString(hash, 0, 8);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
