using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using MemoryPack;
using XREngine;
using XREngine.Core.Files;
using XREngine.Diagnostics;

namespace XREngine.Editor;

internal static class ProjectBuilder
{
    private const string StartupAssetName = "startup.asset";

    private sealed record BuildContext(
        XRProject Project,
        BuildSettings Settings,
        string AssetsDirectory,
        string IntermediateDirectory,
        string BuildRoot,
        string ContentOutputDirectory,
        string ConfigOutputDirectory,
        string BinariesOutputDirectory,
        string ConfigStagingDirectory,
        string ContentArchivePath,
        string ConfigArchivePath);

    private sealed record BuildStep(string Description, Action Action);

    private static readonly object _buildLock = new();
    private static Job? _activeJob;

    public static bool IsBuilding => _activeJob is { IsRunning: true };

    public static void RequestBuild()
    {
        if (Engine.CurrentProject is null)
        {
            Debug.LogWarning("Cannot build: no project is loaded.");
            return;
        }

        lock (_buildLock)
        {
            if (_activeJob is { IsRunning: true })
            {
                Debug.LogWarning("A project build is already running.");
                return;
            }

            var job = Engine.Jobs.Schedule(RunBuildRoutine);
            job.Completed += OnJobFinished;
            job.Canceled += OnJobFinished;
            job.Faulted += (j, ex) =>
            {
                Debug.LogException(ex, "Project build failed.");
                OnJobFinished(j);
            };

            EditorJobTracker.Track(job, "Build Project", payload => payload as string);
            _activeJob = job;
        }
    }

    private static void OnJobFinished(Job job)
    {
        lock (_buildLock)
        {
            if (_activeJob == job)
                _activeJob = null;
        }
    }

    private static IEnumerable RunBuildRoutine()
    {
        yield return new JobProgress(0f, "Starting build...");

        var project = EnsureProjectLoaded();
        var settings = SnapshotSettings();
        var context = CreateBuildContext(project, settings);
        var steps = CreateSteps(settings, context);

        if (steps.Count == 0)
        {
            yield return new JobProgress(1f, "Nothing to build.");
            yield break;
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            Debug.Out(step.Description);
            step.Action();
            float progress = (i + 1f) / steps.Count;
            yield return new JobProgress(progress, step.Description);
        }

        yield return new JobProgress(1f, "Build completed");
    }

    private static XRProject EnsureProjectLoaded()
        => Engine.CurrentProject ?? throw new InvalidOperationException("No project is currently loaded.");

    private static BuildSettings SnapshotSettings()
    {
        var current = Engine.BuildSettings ?? new BuildSettings();
        string yaml = AssetManager.Serializer.Serialize(current);
        return AssetManager.Deserializer.Deserialize<BuildSettings>(yaml) ?? new BuildSettings();
    }

    private static BuildContext CreateBuildContext(XRProject project, BuildSettings settings)
    {
        string assetsDir = RequireDirectory(project.AssetsDirectory, "Assets");
        string buildDir = RequireDirectory(project.BuildDirectory, "Build");
        string intermediateDir = RequireDirectory(project.IntermediateDirectory, "Intermediate");

        string buildRoot = CombineAndNormalize(buildDir, settings.OutputSubfolder);
        string contentDir = CombineAndNormalize(buildRoot, settings.ContentOutputFolder);
        string configDir = CombineAndNormalize(buildRoot, settings.ConfigOutputFolder);
        string binariesDir = CombineAndNormalize(buildRoot, settings.BinariesOutputFolder);
        string stagingDir = Path.Combine(intermediateDir, "Build", "ConfigStaging");

        string contentArchivePath = ResolveArchivePath(contentDir, settings.ContentArchiveName, "GameContent.pak");
        string configArchivePath = ResolveArchivePath(configDir, settings.ConfigArchiveName, "GameConfig.pak");

        return new BuildContext(
            project,
            settings,
            assetsDir,
            intermediateDir,
            buildRoot,
            contentDir,
            configDir,
            binariesDir,
            stagingDir,
            contentArchivePath,
            configArchivePath);
    }

    private static List<BuildStep> CreateSteps(BuildSettings settings, BuildContext context)
    {
        List<BuildStep> steps = [];
        string configuration = ResolveConfiguration(settings.Configuration);
        string platform = ResolvePlatform(settings.Platform);

        if (settings.PublishLauncherAsNativeAot && !settings.BuildLauncherExecutable)
            throw new InvalidOperationException("PublishLauncherAsNativeAot requires BuildLauncherExecutable to be enabled.");

        bool isFinalBuild = settings.PublishLauncherAsNativeAot;
        bool cookContent = settings.CookContent || isFinalBuild;
        bool generateConfigArchive = settings.GenerateConfigArchive || isFinalBuild;

        if (settings.SaveSettingsBeforeBuild)
        {
            steps.Add(new BuildStep("Saving project settings", Engine.SaveProjectSettings));
        }

        steps.Add(new BuildStep("Preparing output directories", () => PrepareOutputDirectories(context, settings.CleanOutputDirectory)));

        if (cookContent)
        {
            steps.Add(new BuildStep("Cooking content", () => CookContent(context)));
        }

        bool needConfigArchive = generateConfigArchive || settings.BuildLauncherExecutable;
        if (needConfigArchive)
        {
            steps.Add(new BuildStep("Generating config archive", () => GenerateConfigArchive(context)));
        }

        if (settings.BuildManagedAssemblies)
        {
            steps.Add(new BuildStep("Compiling managed assemblies", () => BuildManagedAssemblies(configuration, platform)));
        }

        if (settings.CopyGameAssemblies)
        {
            steps.Add(new BuildStep("Copying game assemblies", () => CopyGameAssemblies(context, configuration, platform, settings.IncludePdbFiles)));
        }

        if (settings.CopyEngineBinaries)
        {
            steps.Add(new BuildStep("Copying engine binaries", () => CopyEngineBinaries(context, settings.IncludePdbFiles)));
        }

        if (settings.BuildLauncherExecutable)
        {
            steps.Add(new BuildStep("Building launcher executable", () => BuildLauncherExecutable(context, settings, configuration, platform)));
        }

        return steps;
    }

    private static void PrepareOutputDirectories(BuildContext context, bool cleanRoot)
    {
        try
        {
            if (cleanRoot && Directory.Exists(context.BuildRoot))
                Directory.Delete(context.BuildRoot, true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to clean build output at '{context.BuildRoot}'.", ex);
        }

        Directory.CreateDirectory(context.BuildRoot);
        Directory.CreateDirectory(context.ContentOutputDirectory);
        Directory.CreateDirectory(context.ConfigOutputDirectory);
        Directory.CreateDirectory(context.BinariesOutputDirectory);

        if (Directory.Exists(context.ConfigStagingDirectory))
            Directory.Delete(context.ConfigStagingDirectory, true);
        Directory.CreateDirectory(context.ConfigStagingDirectory);
    }

    private static void CookContent(BuildContext context)
    {
        if (!Directory.Exists(context.AssetsDirectory))
            throw new DirectoryNotFoundException($"Assets directory not found at '{context.AssetsDirectory}'.");

        string cookedDir = PrepareCookedContentDirectory(context);

        Directory.CreateDirectory(Path.GetDirectoryName(context.ContentArchivePath)!);
        if (File.Exists(context.ContentArchivePath))
            File.Delete(context.ContentArchivePath);

        AssetPacker.Pack(cookedDir, context.ContentArchivePath);
    }

    private static void GenerateConfigArchive(BuildContext context)
    {
        string staging = context.ConfigStagingDirectory;
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        WriteCookedAsset(Engine.GameSettings ?? new GameStartupSettings(), Path.Combine(staging, StartupAssetName));

        if (Engine.EditorPreferences is not null)
            WriteCookedAsset(Engine.EditorPreferences, Path.Combine(staging, XRProject.EngineSettingsFileName));

        WriteCookedAsset(Engine.UserSettings ?? new UserSettings(), Path.Combine(staging, XRProject.UserSettingsFileName));

        Directory.CreateDirectory(Path.GetDirectoryName(context.ConfigArchivePath)!);
        if (File.Exists(context.ConfigArchivePath))
            File.Delete(context.ConfigArchivePath);

        AssetPacker.Pack(staging, context.ConfigArchivePath);
        Directory.Delete(staging, true);
    }

    private static void BuildManagedAssemblies(string configuration, string platform)
    {
        if (Engine.Assets is null)
            throw new InvalidOperationException("Asset system unavailable; cannot build assemblies.");

        var manager = global::CodeManager.Instance;
        manager.RemakeSolutionAsDLL(false);
        if (!manager.CompileSolution(configuration, platform))
            throw new InvalidOperationException("Managed build failed. See log for details.");
    }

    private static void CopyGameAssemblies(BuildContext context, string configuration, string platform, bool includePdb)
    {
        string binaryPath = global::CodeManager.Instance.GetBinaryPath(configuration, platform);
        string? sourceDir = Path.GetDirectoryName(binaryPath);
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Managed build output not found for {configuration}|{platform}.");

        CopyDirectory(sourceDir, context.BinariesOutputDirectory, includePdb);
    }

    private static void CopyEngineBinaries(BuildContext context, bool includePdb)
    {
        string destination = context.BinariesOutputDirectory;
        string sourceDir = AppContext.BaseDirectory;
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase) && !includePdb)
                continue;
            if (!string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("XREngine.Editor", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(file, Path.Combine(destination, fileName), true);
        }

        string runtimeSource = Path.Combine(sourceDir, "runtimes");
        if (Directory.Exists(runtimeSource))
            CopyDirectory(runtimeSource, Path.Combine(destination, "runtimes"), includePdb);

        string? engineAssetsPath = Engine.Assets?.EngineAssetsPath;
        if (!string.IsNullOrWhiteSpace(engineAssetsPath) && Directory.Exists(engineAssetsPath))
        {
            string engineAssetsDestination = Path.Combine(context.BuildRoot, "Build", "CommonAssets");
            CopyDirectory(engineAssetsPath, engineAssetsDestination, includePdb);
        }
    }

    private static void BuildLauncherExecutable(BuildContext context, BuildSettings settings, string configuration, string platform)
    {
        if (!File.Exists(context.ConfigArchivePath))
            throw new FileNotFoundException("Config archive not found. Enable config generation before building the launcher.", context.ConfigArchivePath);

        string launcherPath = global::CodeManager.Instance.BuildLauncherExecutable(
            settings,
            configuration,
            platform,
            StartupAssetName,
            XRProject.EngineSettingsFileName,
            XRProject.UserSettingsFileName);

        CopyLauncherArtifacts(launcherPath, context.BinariesOutputDirectory, settings.LauncherExecutableName, settings.IncludePdbFiles);
    }

    private static void CopyLauncherArtifacts(string sourceExePath, string destinationDirectory, string? requestedName, bool includePdb)
    {
        string executableName = NormalizeExecutableName(requestedName);
        Directory.CreateDirectory(destinationDirectory);

        string sourceDirectory = Path.GetDirectoryName(sourceExePath)!;
        string sourceBaseName = Path.GetFileNameWithoutExtension(sourceExePath);
        string targetBaseName = Path.GetFileNameWithoutExtension(executableName);

        File.Copy(sourceExePath, Path.Combine(destinationDirectory, executableName), true);

        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.runtimeconfig.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.runtimeconfig.json"));
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.deps.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.deps.json"));

        if (includePdb)
        {
            CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.pdb"), Path.Combine(destinationDirectory, $"{targetBaseName}.pdb"));
        }
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WriteCookedAsset(object data, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var blob = CreateCookedBlob(data);
        WriteCookedBlob(destination, blob);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static string PrepareCookedContentDirectory(BuildContext context)
    {
        string cookedRoot = Path.Combine(context.IntermediateDirectory, "Build", "CookedContent");
        if (Directory.Exists(cookedRoot))
            Directory.Delete(cookedRoot, true);
        Directory.CreateDirectory(cookedRoot);

        foreach (string sourceFile in Directory.EnumerateFiles(context.AssetsDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(context.AssetsDirectory, sourceFile);
            string destination = Path.Combine(cookedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            {
                string yaml = File.ReadAllText(sourceFile, Encoding.UTF8);
                var blob = CreateCookedBlobFromYaml(yaml, sourceFile);
                WriteCookedBlob(destination, blob);
            }
            else
            {
                File.Copy(sourceFile, destination, true);
            }
        }

        return cookedRoot;
    }

    internal static string PrepareCookedContentDirectoryForTests(string assetsDirectory, string intermediateDirectory)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory))
            throw new ArgumentException("Assets directory must be provided.", nameof(assetsDirectory));
        if (!Directory.Exists(assetsDirectory))
            throw new DirectoryNotFoundException($"Assets directory not found at '{assetsDirectory}'.");

        if (string.IsNullOrWhiteSpace(intermediateDirectory))
            throw new ArgumentException("Intermediate directory must be provided.", nameof(intermediateDirectory));
        Directory.CreateDirectory(intermediateDirectory);

        string projectRoot = Path.Combine(intermediateDirectory, "UnitTestProjectRoot");
        Directory.CreateDirectory(projectRoot);

        var project = new XRProject("UnitTestProject")
        {
            FilePath = Path.Combine(projectRoot, $"UnitTestProject.{XRProject.ProjectExtension}")
        };

        string buildRoot = Path.Combine(intermediateDirectory, "TestBuild");
        var context = new BuildContext(
            project,
            new BuildSettings(),
            assetsDirectory,
            intermediateDirectory,
            buildRoot,
            Path.Combine(buildRoot, "Content"),
            Path.Combine(buildRoot, "Config"),
            Path.Combine(buildRoot, "Binaries"),
            Path.Combine(buildRoot, "ConfigStaging"),
            Path.Combine(buildRoot, "Content.pak"),
            Path.Combine(buildRoot, "Config.pak"));

        return PrepareCookedContentDirectory(context);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static CookedAssetBlob CreateCookedBlobFromYaml(string yaml, string sourcePath)
    {
        string? typeHint = ExtractAssetTypeHint(yaml);
        if (string.IsNullOrWhiteSpace(typeHint))
            throw new InvalidOperationException($"Asset '{sourcePath}' is missing an __assetType hint and cannot be cooked.");

        Type assetType = ResolveAssetType(typeHint)
            ?? throw new InvalidOperationException($"Unable to resolve asset type '{typeHint}' referenced by '{sourcePath}'.");

        object asset = DeserializeAssetFromYaml(yaml, assetType)
            ?? throw new InvalidOperationException($"Failed to deserialize asset '{sourcePath}' as '{assetType.FullName}'.");

        return CreateCookedBlob(asset);
    }

    private static void WriteCookedBlob(string destination, CookedAssetBlob blob)
    {
        byte[] cookedBytes = MemoryPackSerializer.Serialize(blob);
        File.WriteAllBytes(destination, cookedBytes);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static CookedAssetBlob CreateCookedBlob(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Type runtimeType = instance.GetType();
        string typeName = runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name;
        byte[] payload = CookedBinarySerializer.Serialize(instance);
        return new CookedAssetBlob(typeName, CookedAssetFormat.BinaryV1, payload);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static object? DeserializeAssetFromYaml(string yaml, Type assetType)
    {
        using var reader = new StringReader(yaml);
        return AssetManager.Deserializer.Deserialize(reader, assetType);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static Type? ResolveAssetType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        Type? resolved = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
            return resolved;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static string? ExtractAssetTypeHint(string yaml)
    {
        using var reader = new StringReader(yaml);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("__assetType:", StringComparison.Ordinal))
                continue;

            string value = trimmed.Substring("__assetType:".Length).Trim();
            if (value.Length == 0)
                continue;

            return value.Trim('"');
        }

        return null;
    }

    private static string ResolveConfiguration(EBuildConfiguration configuration)
        => configuration switch
        {
            EBuildConfiguration.Release => global::CodeManager.Config_Release,
            _ => global::CodeManager.Config_Debug
        };

    private static string ResolvePlatform(EBuildPlatform platform)
        => platform switch
        {
            EBuildPlatform.Windows64 => global::CodeManager.Platform_x64,
            _ => global::CodeManager.Platform_AnyCPU
        };

    private static string RequireDirectory(string? path, string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"Project {friendlyName} directory is not configured.");

        return Path.GetFullPath(path);
    }

    private static string CombineAndNormalize(string root, string? relative)
    {
        string basePath = Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(relative))
            return basePath;

        return Path.GetFullPath(Path.Combine(basePath, relative));
    }

    private static string ResolveArchivePath(string directory, string? name, string defaultName)
    {
        string fileName = string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim();
        if (Path.IsPathRooted(fileName))
            return Path.GetFullPath(fileName);

        return Path.Combine(directory, fileName);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool includePdb)
    {
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase) && !includePdb)
                continue;

            string relative = Path.GetRelativePath(sourceDir, file);
            string destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, true);
    }

    private static string NormalizeExecutableName(string? requested)
    {
        string fallback = string.IsNullOrWhiteSpace(requested) ? "Game.exe" : requested.Trim();
        return fallback.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fallback : fallback + ".exe";
    }
}
