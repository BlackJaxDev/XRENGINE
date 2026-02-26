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
    #region Constants

    public const string SolutionFormatVersion = "12.00";
    public const string VisualStudioVersion = "17.4.33213.308";
    public const string MinimumVisualStudioVersion = "10.0.40219.1";
    public const string LegacyProjectGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
    public const string ModernProjectGuid = "9A19103F-16F7-4668-BE54-9A1E7A4F7556";
    public const string TargetFramework = "net10.0-windows7.0";

    public const string Config_Debug = "Debug";
    public const string Config_Release = "Release";

    public const string Platform_AnyCPU = "Any CPU";
    public const string Platform_x64 = "x64";
    public const string Platform_x86 = "x86";

    #endregion

    #region State & Properties

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

    #endregion

    #region File Monitoring

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

    #endregion

    #region Path Resolution

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
        => Path.Combine(GetManagedProjectRootPath(), $"{GetProjectName()}.sln");

    private string GetManagedProjectRootPath()
        => Engine.CurrentProject?.ProjectDirectory ?? Engine.Assets.LibrariesPath;

    private string GetManagedGameProjectPath()
        => Path.Combine(GetManagedProjectRootPath(), $"{GetProjectName()}.csproj");

    #endregion

    #region Solution Generation

    /// <summary>
    /// Regenerates project files and optionally compiles the game client as a DLL.
    /// DLL builds are for being loaded dynamically by the editor or for 3rd party developers.
    /// </summary>
    /// <param name="compileNow"></param>
    public void RemakeSolutionAsDLL(bool compileNow = true)
    {
        string sourceRootFolder = Engine.Assets.GameAssetsPath;
        string projectName = GetProjectName();

        string dllProjPath = GetManagedGameProjectPath();

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
        string projectName = GetProjectName();

        string dllProjPath = GetManagedGameProjectPath();
        string exeProjPath = Path.Combine(GetManagedProjectRootPath(), $"{projectName}.exe.csproj");

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

    #endregion

    #region Project & Solution File Creation

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

    #endregion

    #region Compilation

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
        string primaryBuildRoot = Path.Combine(GetManagedProjectRootPath(), "Build");
        string legacyBuildRoot = Path.Combine(Engine.Assets.LibrariesPath, projectName, "Build");

        foreach (string buildRoot in new[] { primaryBuildRoot, legacyBuildRoot })
        {
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
        }

        return Path.Combine(primaryBuildRoot, config, platform, TargetFramework, dllName);
    }

    #endregion

    #region Launcher Building

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

        string gameProjectPath = GetManagedGameProjectPath();

        string programPath = Path.Combine(launcherRoot, "Program.cs");
        string launcherAssemblyName = $"{projectName}.Launcher";
        string launcherProjectPath = Path.Combine(launcherRoot, $"{launcherAssemblyName}.csproj");
        string defineConstants = settings.LauncherDefineConstants?.Trim() ?? string.Empty;

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

            if (!string.IsNullOrWhiteSpace(defineConstants))
                extraProps["DefineConstants"] = defineConstants;

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
            Dictionary<string, string?>? extraProps = null;
            if (!string.IsNullOrWhiteSpace(defineConstants))
            {
                extraProps = new Dictionary<string, string?>
                {
                    ["DefineConstants"] = defineConstants
                };
            }

            if (!BuildProjectFile(launcherProjectPath, configuration, platform, ["Build"], extraProps, out string? buildLog))
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

    #endregion

    #region Build Process

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

    #endregion

    #region Launcher Code Generation

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
        sb.AppendLine("using XREngine;");
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
        sb.AppendLine("#if XRE_PUBLISHED");
        sb.AppendLine("        if (!File.Exists(archivePath))");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine($\"Config archive '{archivePath}' not found.\");");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        AssetManager.ConfigurePublishedArchives(");
        sb.AppendLine("            archivePath,");
        sb.AppendLine("            File.Exists(contentArchivePath) ? contentArchivePath : null,");
        sb.AppendLine("            File.Exists(commonAssetsArchivePath) ? commonAssetsArchivePath : null);");
        sb.AppendLine("#else");
        sb.AppendLine("        AssetManager.ConfigurePublishedArchives(null, null, null);");
        sb.AppendLine("#endif");
        sb.AppendLine();
        sb.AppendLine($"        var startup = Engine.Assets.Load<GameStartupSettings>(\"{escapedStartup}\") ?? Engine.Assets.LoadGameAsset<GameStartupSettings>(\"{escapedStartup}\");");
        sb.AppendLine("        if (startup is null)");
        sb.AppendLine("        {");
        sb.AppendLine("            Console.Error.WriteLine(\"Failed to load startup settings.\");");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        var editorPreferences = Engine.Assets.Load<EditorPreferences>(\"{escapedEngine}\");");
        sb.AppendLine("        if (editorPreferences is not null)");
        sb.AppendLine("            Engine.GlobalEditorPreferences = editorPreferences;");
        sb.AppendLine();
        sb.AppendLine($"        var userSettings = Engine.Assets.Load<UserSettings>(\"{escapedUser}\");");
        sb.AppendLine("        if (userSettings is not null)");
        sb.AppendLine("            Engine.UserSettings = userSettings;");
        sb.AppendLine();
        sb.AppendLine("        Engine.Run(startup, Engine.LoadOrGenerateGameState());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    #endregion
}
