using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Linq;
using MemoryPack;
using XREngine;
using XREngine.Components.Scripting;
using XREngine.Core.Attributes;
using XREngine.Core.Files;
using XREngine.Diagnostics;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Serialization;

namespace XREngine.Editor;

internal static class ProjectBuilder
{
    #region Nested Types

    /// <summary>
    /// Represents the progress of cooking a single asset during the content cooking step of the build process.
    /// </summary>
    /// <param name="ProcessedFiles">The number of asset files that have been processed so far.</param>
    /// <param name="TotalFiles">The total number of asset files that need to be processed.</param>
    /// <param name="RelativePath">The relative path of the asset being processed, relative to the root of the assets directory.</param>
    /// <param name="CookedAsBinaryAsset">Indicates whether the asset was successfully cooked into a binary format (true) or if it was copied as-is (false).</param>
    internal readonly record struct CookProgress(int ProcessedFiles, int TotalFiles, string RelativePath, bool CookedAsBinaryAsset);

    private const string StartupAssetName = "startup.asset";
    private const string CommonAssetsArchiveName = "CommonAssets.pak";
    private const string AotRuntimeMetadataFileName = AotRuntimeMetadataStore.MetadataFileName;

    private sealed record AssetTimestampEntry(string Path, long LastWriteUtcTicks, long Length);

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

    #endregion

    #region State & Public API

    private static readonly object _buildLock = new();
    private static Job? _activeJob;

    public static bool IsBuilding => _activeJob is { IsRunning: true };

    internal static void BuildCurrentProjectSynchronously(Action<JobProgress>? progressCallback = null)
        => BuildCurrentProjectSynchronously(settingsOverride: null, progressCallback);

    internal static void BuildCurrentProjectSynchronously(BuildSettings? settingsOverride, Action<JobProgress>? progressCallback = null)
    {
        lock (_buildLock)
        {
            if (_activeJob is { IsRunning: true })
                throw new InvalidOperationException("A project build is already running.");
        }

        progressCallback?.Invoke(new JobProgress(0f, "Starting build..."));

        var project = EnsureProjectLoaded();
        var settings = settingsOverride ?? SnapshotSettings();
        AssetPacker.ArchiveCopyBufferBytes = settings.ArchiveCopyBufferBytes;
        var context = CreateBuildContext(project, settings);
        var steps = CreateSteps(settings, context);

        if (steps.Count == 0)
        {
            progressCallback?.Invoke(new JobProgress(1f, "Nothing to build."));
            return;
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            Debug.Out(step.Description);
            step.Action();
            float progress = (i + 1f) / steps.Count;
            progressCallback?.Invoke(new JobProgress(progress, step.Description));
        }

        progressCallback?.Invoke(new JobProgress(1f, "Build completed"));
    }

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
            job.Faulted += OnJobFaulted;

            EditorJobTracker.Track(job, "Build Project", payload => payload as string);
            _activeJob = job;
        }
    }

    #endregion

    #region Build Orchestration

    private static void OnJobFaulted(Job j, Exception ex)
    {
        Debug.LogException(ex, "Project build failed.");
        OnJobFinished(j);
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
        AssetPacker.ArchiveCopyBufferBytes = settings.ArchiveCopyBufferBytes;
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

        if (settings.BuildManagedAssemblies)
        {
            steps.Add(new BuildStep("Compiling managed assemblies", () => BuildManagedAssemblies(configuration, platform)));
        }

        bool needConfigArchive = generateConfigArchive || settings.BuildLauncherExecutable;
        if (needConfigArchive)
        {
            steps.Add(new BuildStep("Generating config archive", () => GenerateConfigArchive(context, configuration, platform)));
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

    #endregion

    #region Build Steps

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

        if (!ShouldUseIncrementalArchives(context))
        {
            string cookedDir = PrepareCookedContentDirectory(context);
            Directory.CreateDirectory(Path.GetDirectoryName(context.ContentArchivePath)!);
            if (File.Exists(context.ContentArchivePath))
                File.Delete(context.ContentArchivePath);

            AssetPacker.Pack(cookedDir, context.ContentArchivePath);
            return;
        }

        BuildContentArchiveIncremental(context);
    }

    private static void GenerateConfigArchive(BuildContext context, string configuration, string platform)
    {
        string staging = context.ConfigStagingDirectory;
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        WriteCookedAsset(Engine.GameSettings ?? new GameStartupSettings(), Path.Combine(staging, StartupAssetName));

        if (Engine.EditorPreferences is not null)
            WriteCookedAsset(Engine.EditorPreferences, Path.Combine(staging, XRProject.EngineSettingsFileName));

        WriteCookedAsset(Engine.UserSettings ?? new UserSettings(), Path.Combine(staging, XRProject.UserSettingsFileName));

        if (context.Settings.PublishLauncherAsNativeAot)
            WriteAotRuntimeMetadata(staging, configuration, platform);

        Directory.CreateDirectory(Path.GetDirectoryName(context.ConfigArchivePath)!);
        try
        {
            if (!ShouldUseIncrementalArchives(context) || !File.Exists(context.ConfigArchivePath))
            {
                if (File.Exists(context.ConfigArchivePath))
                    File.Delete(context.ConfigArchivePath);

                AssetPacker.Pack(staging, context.ConfigArchivePath);
                return;
            }

            RepackConfigArchiveIncremental(context, staging);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static void WriteAotRuntimeMetadata(string stagingDirectory, string configuration, string platform)
    {
        AotRuntimeMetadata metadata = BuildAotRuntimeMetadata(configuration, platform);
        byte[] bytes = MemoryPackSerializer.Serialize(metadata);
        File.WriteAllBytes(Path.Combine(stagingDirectory, AotRuntimeMetadataFileName), bytes);
    }

    private static AotRuntimeMetadata BuildAotRuntimeMetadata(string configuration, string platform)
    {
        List<Assembly> assemblies = [.. AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)];
        DynamicGameAssemblyScope? gameAssemblyScope = TryLoadBuiltGameAssembly(configuration, platform);
        if (gameAssemblyScope?.Assembly is not null)
            assemblies.Add(gameAssemblyScope.Assembly);

        try
        {
            Type[] allTypes = [.. EnumerateLoadableTypes(assemblies)
                .Where(t => t.AssemblyQualifiedName is not null)
                .DistinctBy(t => t.AssemblyQualifiedName, StringComparer.Ordinal)];

            AotRuntimeMetadata metadata = new()
            {
                KnownTypeAssemblyQualifiedNames = [.. allTypes
                    .Select(t => t.AssemblyQualifiedName!)
                    .OrderBy(x => x, StringComparer.Ordinal)],
                TransformTypes = [.. allTypes
                    .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(TransformBase)))
                    .Select(t => new AotTransformTypeInfo
                    {
                        AssemblyQualifiedName = t.AssemblyQualifiedName!,
                        FriendlyName = FriendlyTransformName(t),
                    })
                    .OrderBy(x => x.AssemblyQualifiedName, StringComparer.Ordinal)],
                TypeRedirects = [.. allTypes
                    .SelectMany(BuildRedirectInfos)
                    .OrderBy(x => x.LegacyTypeName, StringComparer.Ordinal)
                    .ThenBy(x => x.FullName, StringComparer.Ordinal)],
                WorldObjectReplications = [.. allTypes
                    .Where(t => !t.IsAbstract && typeof(XRWorldObjectBase).IsAssignableFrom(t))
                    .Select(BuildReplicationInfo)
                    .Where(x => x is not null)
                    .Cast<AotWorldObjectReplicationInfo>()
                    .OrderBy(x => x.AssemblyQualifiedName, StringComparer.Ordinal)],
                YamlTypeConverterTypeNames = [.. allTypes
                    .Where(t => !t.IsAbstract && !t.IsInterface)
                    .Where(t => typeof(IYamlTypeConverter).IsAssignableFrom(t))
                    .Where(t => t.GetCustomAttribute<YamlTypeConverterAttribute>() is not null)
                    .Select(t => t.AssemblyQualifiedName!)
                    .OrderBy(x => x, StringComparer.Ordinal)]
            };

            return metadata;
        }
        finally
        {
            gameAssemblyScope?.Dispose();
        }
    }

    private static IEnumerable<Type> EnumerateLoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies.DistinctBy(a => a.FullName, StringComparer.Ordinal))
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (Type type in types)
                yield return type;
        }
    }

    private static IEnumerable<AotTypeRedirectInfo> BuildRedirectInfos(Type type)
    {
        XRTypeRedirectAttribute[] attrs = type.GetCustomAttributes<XRTypeRedirectAttribute>(inherit: false).ToArray();
        if (attrs.Length == 0)
            yield break;

        string fullName = type.FullName ?? type.Name;
        string? assemblyQualifiedName = type.AssemblyQualifiedName;

        foreach (XRTypeRedirectAttribute attr in attrs)
        {
            foreach (string legacyName in attr.LegacyTypeNames ?? [])
            {
                if (string.IsNullOrWhiteSpace(legacyName))
                    continue;

                yield return new AotTypeRedirectInfo
                {
                    LegacyTypeName = legacyName.Trim(),
                    FullName = fullName,
                    AssemblyQualifiedName = assemblyQualifiedName,
                };
            }
        }
    }

    private static AotWorldObjectReplicationInfo? BuildReplicationInfo(Type type)
    {
        const BindingFlags replicableFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        List<string> onChange = [];
        List<string> onTick = [];
        HashSet<string> compressed = new(StringComparer.Ordinal);

        foreach (PropertyInfo property in type.GetProperties(replicableFlags))
        {
            if (property.GetCustomAttribute<ReplicateOnChangeAttribute>(true) is ReplicateOnChangeAttribute changeAttr)
            {
                onChange.Add(property.Name);
                if (changeAttr.Compress)
                    compressed.Add(property.Name);
            }

            if (property.GetCustomAttribute<ReplicateOnTickAttribute>(true) is ReplicateOnTickAttribute tickAttr)
            {
                onTick.Add(property.Name);
                if (tickAttr.Compress)
                    compressed.Add(property.Name);
            }
        }

        if (onChange.Count == 0 && onTick.Count == 0)
            return null;

        return new AotWorldObjectReplicationInfo
        {
            AssemblyQualifiedName = type.AssemblyQualifiedName!,
            ReplicateOnChangeProperties = [.. onChange.OrderBy(x => x, StringComparer.Ordinal)],
            ReplicateOnTickProperties = [.. onTick.OrderBy(x => x, StringComparer.Ordinal)],
            CompressedPropertyNames = [.. compressed.OrderBy(x => x, StringComparer.Ordinal)],
        };
    }

    private static string FriendlyTransformName(Type type)
    {
        DisplayNameAttribute? name = type.GetCustomAttribute<DisplayNameAttribute>();
        return $"{name?.DisplayName ?? type.Name} ({type.Assembly.GetName().Name})";
    }

    private static DynamicGameAssemblyScope? TryLoadBuiltGameAssembly(string configuration, string platform)
    {
        string binaryPath = global::CodeManager.Instance.GetBinaryPath(configuration, platform);
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
            return null;

        try
        {
            byte[] assemblyBytes = File.ReadAllBytes(binaryPath);
            string pdbPath = Path.ChangeExtension(binaryPath, ".pdb");
            byte[]? pdbBytes = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;

            GameCSProjLoader.DynamicEngineAssemblyLoadContext context = new();
            Assembly assembly;
            using MemoryStream assemblyStream = new(assemblyBytes);
            if (pdbBytes is not null)
            {
                using MemoryStream pdbStream = new(pdbBytes);
                assembly = context.LoadFromStream(assemblyStream, pdbStream);
            }
            else
            {
                assembly = context.LoadFromStream(assemblyStream);
            }

            return new DynamicGameAssemblyScope(context, assembly);
        }
        catch
        {
            return null;
        }
    }

    private sealed class DynamicGameAssemblyScope(GameCSProjLoader.DynamicEngineAssemblyLoadContext context, Assembly assembly) : IDisposable
    {
        public GameCSProjLoader.DynamicEngineAssemblyLoadContext Context { get; } = context;
        public Assembly Assembly { get; } = assembly;

        public void Dispose()
        {
            Context.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
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
            string commonAssetsArchivePath = Path.Combine(context.ContentOutputDirectory, CommonAssetsArchiveName);
            Directory.CreateDirectory(Path.GetDirectoryName(commonAssetsArchivePath)!);

            if (!ShouldUseIncrementalArchives(context))
            {
                if (File.Exists(commonAssetsArchivePath))
                    File.Delete(commonAssetsArchivePath);

                AssetPacker.Pack(engineAssetsPath, commonAssetsArchivePath);
            }
            else
            {
                RepackArchiveFromSourceIncremental(
                    sourceDirectory: engineAssetsPath,
                    archivePath: commonAssetsArchivePath,
                    intermediateDirectory: context.IntermediateDirectory,
                    deltaFolderName: "CommonAssetsDelta",
                    transformFile: static (source, destination) => File.Copy(source, destination, true));
            }
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

        // Framework-dependent launchers need the managed entry assembly beside the renamed host exe.
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.dll"), Path.Combine(destinationDirectory, $"{targetBaseName}.dll"));
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.runtimeconfig.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.runtimeconfig.json"));
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.deps.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.deps.json"));

        if (includePdb)
        {
            CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.pdb"), Path.Combine(destinationDirectory, $"{targetBaseName}.pdb"));
        }
    }

    #endregion

    #region Incremental Build

    private static bool ShouldUseIncrementalArchives(BuildContext context)
        => context.Settings.PublishLauncherAsNativeAot;

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void BuildContentArchiveIncremental(BuildContext context)
    {
        RepackArchiveFromSourceIncremental(
            sourceDirectory: context.AssetsDirectory,
            archivePath: context.ContentArchivePath,
            intermediateDirectory: context.IntermediateDirectory,
            deltaFolderName: "CookedContentDelta",
            transformFile: (source, destination) => WriteCookedAssetFile(source, destination));
    }

    private static void RepackConfigArchiveIncremental(BuildContext context, string stagingDirectory)
    {
        IReadOnlyCollection<string> existingPaths;
        try
        {
            existingPaths = new HashSet<string>(AssetPacker.GetAssetPaths(context.ConfigArchivePath), StringComparer.Ordinal);
        }
        catch
        {
            if (File.Exists(context.ConfigArchivePath))
                File.Delete(context.ConfigArchivePath);
            AssetPacker.Pack(stagingDirectory, context.ConfigArchivePath);
            return;
        }

        string deltaDirectory = Path.Combine(context.IntermediateDirectory, "Build", "ConfigDelta");
        if (Directory.Exists(deltaDirectory))
            Directory.Delete(deltaDirectory, true);

        HashSet<string> stagedPaths = new(StringComparer.Ordinal);
        List<string> changedPaths = [];

        foreach (string filePath in Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizePath(Path.GetRelativePath(stagingDirectory, filePath));
            stagedPaths.Add(relativePath);

            bool changed = true;
            try
            {
                byte[] archiveData = AssetPacker.GetAsset(context.ConfigArchivePath, relativePath);
                byte[] stagedData = File.ReadAllBytes(filePath);
                changed = !archiveData.AsSpan().SequenceEqual(stagedData);
            }
            catch (FileNotFoundException)
            {
                changed = true;
            }

            if (!changed)
                continue;

            string destination = Path.Combine(deltaDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(filePath, destination, true);
            changedPaths.Add(relativePath);
        }

        List<string> removedPaths = [];
        foreach (string path in existingPaths)
        {
            if (!stagedPaths.Contains(path))
                removedPaths.Add(path);
        }

        if (changedPaths.Count == 0 && removedPaths.Count == 0)
            return;

        AssetPacker.Repack(
            context.ConfigArchivePath,
            changedPaths.Count > 0 ? deltaDirectory : string.Empty,
            [.. removedPaths]);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void RepackArchiveFromSourceIncremental(
        string sourceDirectory,
        string archivePath,
        string intermediateDirectory,
        string deltaFolderName,
        Action<string, string> transformFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        Dictionary<string, AssetTimestampEntry> currentSnapshot = CaptureDirectorySnapshot(sourceDirectory);

        if (!File.Exists(archivePath))
        {
            string fullStagingDir = Path.Combine(intermediateDirectory, "Build", deltaFolderName + "_Full");
            StageFiles(sourceDirectory, fullStagingDir, currentSnapshot.Keys, transformFile);
            AssetPacker.Pack(fullStagingDir, archivePath);
            return;
        }

        // Read archive metadata (timestamps are embedded in the TOC)
        AssetPacker.ArchiveInfo archiveInfo;
        try
        {
            archiveInfo = AssetPacker.ReadArchiveInfo(archivePath);
        }
        catch
        {
            // Corrupt or unreadable archive — do a full repack
            string fullStagingDir = Path.Combine(intermediateDirectory, "Build", deltaFolderName + "_FallbackFull");
            StageFiles(sourceDirectory, fullStagingDir, currentSnapshot.Keys, transformFile);
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            AssetPacker.Pack(fullStagingDir, archivePath);
            return;
        }

        // Build a lookup from the embedded TOC timestamps
        Dictionary<string, long> embeddedTimestamps = new(StringComparer.Ordinal);
        foreach (var entry in archiveInfo.Entries)
            embeddedTimestamps[entry.Path] = entry.SourceTimestampUtcTicks;

        // Determine which source files have changed vs. the embedded timestamp
        List<string> changedPaths = [];
        foreach (var (path, current) in currentSnapshot)
        {
            if (!embeddedTimestamps.TryGetValue(path, out long embeddedTicks) ||
                embeddedTicks != current.LastWriteUtcTicks)
            {
                changedPaths.Add(path);
            }
        }

        // Determine which archive entries have no corresponding source file
        List<string> removedPaths = [];
        foreach (string existingPath in embeddedTimestamps.Keys)
        {
            if (!currentSnapshot.ContainsKey(existingPath))
                removedPaths.Add(existingPath);
        }

        if (changedPaths.Count == 0 && removedPaths.Count == 0)
            return;

        string deltaDirectory = Path.Combine(intermediateDirectory, "Build", deltaFolderName);
        if (changedPaths.Count > 0)
            StageFiles(sourceDirectory, deltaDirectory, changedPaths, transformFile);

        AssetPacker.Repack(
            archivePath,
            changedPaths.Count > 0 ? deltaDirectory : string.Empty,
            [.. removedPaths]);
    }

    private static void StageFiles(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<string> relativePaths,
        Action<string, string> transformFile)
    {
        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, true);
        Directory.CreateDirectory(destinationDirectory);

        foreach (string relativePath in relativePaths)
        {
            string sourcePath = Path.Combine(sourceDirectory, ToSystemPath(relativePath));
            if (!File.Exists(sourcePath))
                continue;

            string destinationPath = Path.Combine(destinationDirectory, ToSystemPath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            transformFile(sourcePath, destinationPath);
        }
    }

    private static Dictionary<string, AssetTimestampEntry> CaptureDirectorySnapshot(string rootDirectory)
    {
        Dictionary<string, AssetTimestampEntry> snapshot = new(StringComparer.Ordinal);
        foreach (string filePath in Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizePath(Path.GetRelativePath(rootDirectory, filePath));
            var fileInfo = new FileInfo(filePath);
            snapshot[relativePath] = new AssetTimestampEntry(relativePath, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
        }

        return snapshot;
    }

    #endregion

    #region Content Cooking

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WriteCookedAssetFile(string sourceFile, string destination)
    {
        if (TryWriteCookedAssetFile(sourceFile, destination))
            return;

        File.Copy(sourceFile, destination, true);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static bool TryWriteCookedAssetFile(string sourceFile, string destination)
    {
        if (!string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            return false;

        string yaml = File.ReadAllText(sourceFile, Encoding.UTF8);
        string? typeHint = ExtractAssetTypeHint(yaml);

        if (string.IsNullOrWhiteSpace(typeHint))
            return false;

        try
        {
            var blob = CreateCookedBlobFromYaml(yaml, sourceFile);
            WriteCookedBlob(destination, blob);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
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
    private static string PrepareCookedContentDirectory(BuildContext context, Action<CookProgress>? progress = null)
    {
        string cookedRoot = Path.Combine(context.IntermediateDirectory, "Build", "CookedContent");
        if (Directory.Exists(cookedRoot))
            Directory.Delete(cookedRoot, true);
        Directory.CreateDirectory(cookedRoot);

        string[] sourceFiles = Directory.GetFiles(context.AssetsDirectory, "*", SearchOption.AllDirectories);
        int totalFiles = sourceFiles.Length;

        for (int i = 0; i < sourceFiles.Length; i++)
        {
            string sourceFile = sourceFiles[i];
            string relative = Path.GetRelativePath(context.AssetsDirectory, sourceFile);
            string destination = Path.Combine(cookedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            bool cookedAsBinaryAsset = false;

            if (string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            {
                cookedAsBinaryAsset = TryWriteCookedAssetFile(sourceFile, destination);
                if (!cookedAsBinaryAsset)
                    File.Copy(sourceFile, destination, true);

                progress?.Invoke(new CookProgress(i + 1, totalFiles, relative, cookedAsBinaryAsset));
                continue;
            }

            File.Copy(sourceFile, destination, true);
            progress?.Invoke(new CookProgress(i + 1, totalFiles, relative, cookedAsBinaryAsset));
        }

        return cookedRoot;
    }

    internal static string PrepareCookedContentDirectoryForTests(string assetsDirectory, string intermediateDirectory, Action<CookProgress>? progress = null)
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

        return PrepareCookedContentDirectory(context, progress);
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

            string value = trimmed["__assetType:".Length..].Trim();
            if (value.Length == 0)
                continue;

            return value.Trim('"');
        }

        return null;
    }

    #endregion

    #region Utilities

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private static string ToSystemPath(string normalizedPath)
        => normalizedPath.Replace('/', Path.DirectorySeparatorChar);

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

    #endregion
}
