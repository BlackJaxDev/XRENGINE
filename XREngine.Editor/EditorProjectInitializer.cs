using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using XREngine;
using XREngine.Core.Files;

internal static class EditorProjectInitializer
{
    #region Constants

    private const string StartupAssetFileName = "startup.asset";
    private const string SolutionFormatVersion = "12.00";
    private const string VisualStudioVersion = "17.4.33213.308";
    private const string MinimumVisualStudioVersion = "10.0.40219.1";
    private const string LegacyProjectGuid = "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";

    private static readonly string[] ProfileConfigurations =
    [
        "Development Debug",
        "Development Release",
        "Published Release"
    ];

    private const string ProfilePlatform = "Any CPU";

    #endregion

    #region Public API

    public static bool InitializeNewProject(string projectDirectory, string projectName, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            string normalizedDirectory = Path.GetFullPath(projectDirectory);
            if (string.IsNullOrWhiteSpace(projectName))
            {
                error.WriteLine("Project name cannot be empty.");
                return false;
            }

            string descriptorPath = Path.Combine(normalizedDirectory, $"{projectName}.{XRProject.ProjectExtension}");
            if (File.Exists(descriptorPath))
            {
                error.WriteLine($"A project already exists at '{descriptorPath}'.");
                return false;
            }

            output.WriteLine($"Creating XRProject '{projectName}' at '{normalizedDirectory}'...");
            XRProject? project = Engine.CreateProject(normalizedDirectory, projectName);
            if (project is null)
            {
                error.WriteLine("Failed to create the XRProject descriptor.");
                return false;
            }

            if (!Engine.LoadProject(project))
            {
                error.WriteLine("Project descriptor was created, but the project failed to load.");
                return false;
            }

            EnsureStartupAsset(projectName, output);

            GenerateProjectBuildFiles(normalizedDirectory, projectName, output);

            if (TryResolveRepositoryRoot(normalizedDirectory, out string? repoRoot) &&
                TryResolveSamplesRoot(repoRoot, out string? samplesRoot) &&
                IsPathInside(normalizedDirectory, samplesRoot))
            {
                UpdateSampleGamesSolution(samplesRoot, output);
            }

            return true;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Failed to initialize project: {ex}");
            return false;
        }
    }

    #endregion

    #region Project Initialization

    private static void EnsureStartupAsset(string projectName, TextWriter output)
    {
        Directory.CreateDirectory(Engine.Assets.GameAssetsPath);

        Engine.GameSettings ??= new GameStartupSettings();
        Engine.GameSettings.Name = projectName;
        Engine.GameSettings.FilePath ??= Path.Combine(Engine.Assets.GameAssetsPath, StartupAssetFileName);
        Engine.Assets.Save(Engine.GameSettings, bypassJobThread: true);

        output.WriteLine($"Created startup asset at '{Engine.GameSettings.FilePath}'.");
    }

    private static void GenerateProjectBuildFiles(string projectDirectory, string projectName, TextWriter output)
    {
        string csprojPath = Path.Combine(projectDirectory, $"{projectName}.csproj");
        string slnPath = Path.Combine(projectDirectory, $"{projectName}.sln");

        string? engineProjectPath = ResolveEngineProjectPath(projectDirectory);
        string csprojContents = BuildProjectCsproj(projectDirectory, projectName, engineProjectPath);
        File.WriteAllText(csprojPath, csprojContents, Encoding.UTF8);

        string projectGuid = CreateDeterministicGuid(csprojPath).ToString("B").ToUpperInvariant();
        string slnContents = BuildSolutionContents(projectDirectory, [(projectName, csprojPath, projectGuid)]);
        File.WriteAllText(slnPath, slnContents, Encoding.UTF8);

        output.WriteLine($"Generated project file at '{csprojPath}'.");
        output.WriteLine($"Generated solution at '{slnPath}'.");
    }

    #endregion

    #region Build File Generation

    private static string BuildProjectCsproj(string projectDirectory, string projectName, string? engineProjectPath)
    {
        string rootNamespace = SanitizeIdentifier(projectName, fallback: "GameProject");
        string? engineReference = engineProjectPath is null
            ? null
            : Path.GetRelativePath(projectDirectory, engineProjectPath).Replace('\\', '/');

        string publishedDefines = "$(DefineConstants);XRE_PUBLISHED";

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0-windows7.0</TargetFramework>");
        sb.AppendLine("    <OutputType>Library</OutputType>");
        sb.AppendLine($"    <RootNamespace>{EscapeXml(rootNamespace)}</RootNamespace>");
        sb.AppendLine($"    <AssemblyName>{EscapeXml(projectName)}</AssemblyName>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        sb.AppendLine("    <IsPackable>false</IsPackable>");
        sb.AppendLine("    <Configurations>Development Debug;Development Release;Published Release</Configurations>");
        sb.AppendLine("    <Platforms>AnyCPU</Platforms>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Development Debug'\">");
        sb.AppendLine("    <Optimize>false</Optimize>");
        sb.AppendLine("    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Development Release'\">");
        sb.AppendLine("    <Optimize>true</Optimize>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <PropertyGroup Condition=\"'$(Configuration)' == 'Published Release'\">");
        sb.AppendLine("    <Optimize>true</Optimize>");
        sb.AppendLine($"    <DefineConstants>{EscapeXml(publishedDefines)}</DefineConstants>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine();
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine("    <Compile Include=\"Assets\\**\\*.cs\" />");
        sb.AppendLine("  </ItemGroup>");

        if (!string.IsNullOrWhiteSpace(engineReference))
        {
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <ProjectReference Include=\"{EscapeXml(engineReference)}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private static void UpdateSampleGamesSolution(string samplesRoot, TextWriter output)
    {
        if (!Directory.Exists(samplesRoot))
            return;

        string[] csprojFiles = Directory
            .EnumerateFiles(samplesRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => IsPathInside(path, samplesRoot))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Intermediate{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var projects = new List<(string Name, string Path, string Guid)>();
        foreach (string csproj in csprojFiles)
        {
            string name = Path.GetFileNameWithoutExtension(csproj);
            string guid = CreateDeterministicGuid(csproj).ToString("B").ToUpperInvariant();
            projects.Add((name, csproj, guid));
        }

        projects.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        string slnPath = Path.Combine(samplesRoot, "XRE_SampleGames.sln");
        string slnContents = BuildSolutionContents(samplesRoot, projects);
        File.WriteAllText(slnPath, slnContents, Encoding.UTF8);
        output.WriteLine($"Updated sample solution at '{slnPath}'.");
    }

    private static string BuildSolutionContents(string solutionRoot, IEnumerable<(string Name, string Path, string Guid)> projects)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"Microsoft Visual Studio Solution File, Format Version {SolutionFormatVersion}");
        sb.AppendLine("# Visual Studio Version 17");
        sb.AppendLine($"VisualStudioVersion = {VisualStudioVersion}");
        sb.AppendLine($"MinimumVisualStudioVersion = {MinimumVisualStudioVersion}");

        foreach (var project in projects)
        {
            string relativePath = Path.GetRelativePath(solutionRoot, project.Path).Replace('\\', '/');
            sb.AppendLine($"Project(\"{{{LegacyProjectGuid}}}\") = \"{project.Name}\", \"{relativePath}\", \"{project.Guid}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        foreach (string configuration in ProfileConfigurations)
            sb.AppendLine($"\t\t{configuration}|{ProfilePlatform} = {configuration}|{ProfilePlatform}");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
        foreach (var project in projects)
        {
            foreach (string configuration in ProfileConfigurations)
            {
                sb.AppendLine($"\t\t{project.Guid}.{configuration}|{ProfilePlatform}.ActiveCfg = {configuration}|AnyCPU");
                sb.AppendLine($"\t\t{project.Guid}.{configuration}|{ProfilePlatform}.Build.0 = {configuration}|AnyCPU");
            }
        }
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(SolutionProperties) = preSolution");
        sb.AppendLine("\t\tHideSolutionNode = FALSE");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");
        return sb.ToString();
    }

    #endregion

    #region Path Resolution

    private static bool TryResolveRepositoryRoot(string startDirectory, out string? repositoryRoot)
    {
        repositoryRoot = null;
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
            {
                repositoryRoot = dir.FullName;
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }

    private static bool TryResolveSamplesRoot(string repositoryRoot, out string? samplesRoot)
    {
        samplesRoot = Path.Combine(repositoryRoot, "Samples");
        if (Directory.Exists(samplesRoot))
            return true;

        samplesRoot = null;
        return false;
    }

    private static bool IsPathInside(string path, string root)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveEngineProjectPath(string projectDirectory)
    {
        if (!TryResolveRepositoryRoot(projectDirectory, out string? repositoryRoot) || string.IsNullOrWhiteSpace(repositoryRoot))
            return null;

        string engineProjectPath = Path.Combine(repositoryRoot, "XRENGINE", "XREngine.csproj");
        return File.Exists(engineProjectPath) ? engineProjectPath : null;
    }

    #endregion

    #region Utilities

    private static Guid CreateDeterministicGuid(string input)
    {
        byte[] data = Encoding.UTF8.GetBytes(Path.GetFullPath(input).ToLowerInvariant());
        byte[] hash = MD5.HashData(data);
        return new Guid(hash);
    }

    private static string SanitizeIdentifier(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var sb = new StringBuilder(value.Length);
        if (!char.IsLetter(value[0]) && value[0] != '_')
            sb.Append('_');

        foreach (char c in value)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

        return sb.Length == 0 ? fallback : sb.ToString();
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    #endregion
}
