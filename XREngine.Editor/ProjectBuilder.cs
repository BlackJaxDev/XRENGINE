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
using XREngine.Editor.Publishing;
using XREngine.Networking;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Serialization;
using Model = XREngine.Rendering.Models.Model;
using SubMesh = XREngine.Rendering.Models.SubMesh;
using SubMeshLodDescriptor = XREngine.Rendering.Models.SubMeshLodDescriptor;
using SubMeshLodPayloadCookSink = XREngine.Rendering.Models.SubMeshLodPayloadCookSink;
using XRWorld = XREngine.Scene.XRWorld;

namespace XREngine.Editor;

internal static partial class ProjectBuilder
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

    private static readonly string[] NativeHostDependencyFileNames =
    [
        "openvr_api.dll",
        "OVRLipSync.dll",
        "RestirGI.Native.dll",
    ];

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
        string ConfigArchivePath)
    {
        internal Action? CleanupAfterBuild { get; set; }
    }

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
        // Published archives keep incompressible entries uncompressed so the runtime can lease them
        // straight from the mapped file.
        AssetPacker.StoreIncompressibleEntries = true;
        var context = CreateBuildContext(project, settings);
        var steps = CreateSteps(settings, context);

        if (steps.Count == 0)
        {
            progressCallback?.Invoke(new JobProgress(1f, "Nothing to build."));
            return;
        }

        try
        {
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
        finally { context.CleanupAfterBuild?.Invoke(); }
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
        AssetPacker.StoreIncompressibleEntries = true;
        var context = CreateBuildContext(project, settings);
        var steps = CreateSteps(settings, context);

        if (steps.Count == 0)
        {
            yield return new JobProgress(1f, "Nothing to build.");
            yield break;
        }

        try
        {
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
        finally { context.CleanupAfterBuild?.Invoke(); }
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
        if (settings.Platform == EBuildPlatform.BrowserWebGPU)
            return CreateBrowserSteps(settings, context);

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

        // Compile first so the cooker and generated launcher can inspect the current
        // game assembly rather than stale output from a previous editor session.
        if (settings.BuildManagedAssemblies)
        {
            steps.Add(new BuildStep("Compiling managed assemblies", () => BuildManagedAssemblies(configuration, platform)));
        }

        if (cookContent)
        {
            steps.Add(new BuildStep("Cooking content", () => CookContent(context)));
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

        if (settings.CopyEngineBinaries || settings.BuildLauncherExecutable)
        {
            steps.Add(new BuildStep("Packaging common assets", () => PackageCommonAssets(context, settings.CommonAssetsPackageMode)));
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

        AotRuntimeMetadata? aotMetadata = context.Settings.PublishLauncherAsNativeAot
            ? BuildAotRuntimeMetadata(configuration, platform)
            : null;

        WriteConfigArchiveAssets(
            staging,
            Engine.PersistentGameSettings,
            Engine.CreatePersistableEditorPreferencesSnapshot(),
            Engine.UserSettings ?? new UserSettings(),
            aotMetadata);

        if (aotMetadata is not null)
            WriteAotRuntimeMetadata(staging, aotMetadata);

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

    /// <summary>
    /// Writes the settings assets of the config archive into a staging folder. Each asset is a published copy with no file
    /// path and a portable source path, written under the cook scope and checked for machine paths.
    /// </summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    internal static void WriteConfigArchiveAssets(
        string staging,
        GameStartupSettings startupSettings,
        EditorPreferences editorPreferences,
        UserSettings userSettings,
        AotRuntimeMetadata? aotMetadata)
    {
        using CookedSourcePathScope sourcePaths = CookedSourcePathProjection.Begin();
        WriteGuardedCookedAsset(
            PublishedConfigAssets.CreateStartupSettings(startupSettings),
            Path.Combine(staging, StartupAssetName),
            StartupAssetName,
            aotMetadata);
        WriteGuardedCookedAsset(
            PublishedConfigAssets.CreateEditorPreferences(editorPreferences),
            Path.Combine(staging, XRProject.EditorPreferencesFileName),
            XRProject.EditorPreferencesFileName,
            aotMetadata);
        WriteGuardedCookedAsset(
            PublishedConfigAssets.CreateUserSettings(userSettings),
            Path.Combine(staging, XRProject.UserSettingsFileName),
            XRProject.UserSettingsFileName,
            aotMetadata);

        // The published copies project their own source paths in this scope.
        ThrowIfSourcePathsNotPortable(sourcePaths, "config archive settings");
    }

    /// <summary>
    /// Writes one cooked asset under the cook scope, fails if a source path is outside every asset root, and checks the
    /// written file for machine paths. The config archive and the browser cooks use it.
    /// </summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WriteGuardedCookedAsset(object data, string destination, string assetLabel,
        AotRuntimeMetadata? aotMetadata = null, CookedBinarySerializationCallbacks? callbacks = null)
    {
        using (CookedSourcePathScope sourcePaths = CookedSourcePathProjection.Begin())
        {
            WriteCookedAsset(data, destination, aotMetadata, callbacks);
            ThrowIfSourcePathsNotPortable(sourcePaths, assetLabel);
        }
        CookedOutputPathGuard.ThrowIfMachinePath(destination, assetLabel);
    }

    private static void WriteAotRuntimeMetadata(string stagingDirectory, AotRuntimeMetadata metadata)
    {
        byte[] bytes = MemoryPackSerializer.Serialize(metadata);
        File.WriteAllBytes(Path.Combine(stagingDirectory, AotRuntimeMetadataFileName), bytes);
    }

    private static AotRuntimeMetadata BuildAotRuntimeMetadata(string configuration, string platform)
    {
        List<Assembly> assemblies = [.. AppDomain.CurrentDomain.GetAssemblies().Where(static assembly => !assembly.IsDynamic)];
        using DynamicGameAssemblyScope? gameAssembly = TryLoadBuiltGameAssembly(configuration, platform);
        if (gameAssembly?.Assembly is { } game)
            assemblies.Add(game);
        return XREngine.Publishing.AotRuntimeMetadataBuilder.Build(assemblies);
    }

    private static IEnumerable<Type> EnumerateLoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies.DistinctBy(static item => item.FullName, StringComparer.Ordinal))
        foreach (Type type in XREngine.Core.XRLoadableTypeCatalog.GetTypes(assembly))
            yield return type;
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
            Engine.RequestMaintenanceGarbageCollection(new EngineMaintenanceGcRequest(
                EngineMaintenanceGcReason.DynamicAssemblyUnload,
                $"Temporary build assembly '{Assembly.GetName().Name}' unloaded.",
                Generation: GC.MaxGeneration,
                CompactLargeObjectHeap: false,
                WaitForPendingFinalizers: true));
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
            if (string.Equals(fileName, "XREngine.dll", StringComparison.OrdinalIgnoreCase))
                continue;

            File.Copy(file, Path.Combine(destination, fileName), true);
        }

        CopyRuntimeDependencies(sourceDir, destination, includePdb);
    }

    private static void CopyRuntimeDependencies(string sourceDirectory, string destinationDirectory, bool includePdb, string? runtimeIdentifier = null)
    {
        string runtimesSource = Path.Combine(sourceDirectory, "runtimes");
        if (!Directory.Exists(runtimesSource))
            return;

        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            CopyDirectory(
                runtimesSource,
                Path.Combine(destinationDirectory, "runtimes"),
                includePdb);
            return;
        }

        string runtimeSource = Path.Combine(runtimesSource, runtimeIdentifier);
        if (!Directory.Exists(runtimeSource))
            throw new DirectoryNotFoundException(
                $"Runtime dependency directory '{runtimeSource}' was not produced.");

        CopyDirectory(
            runtimeSource,
            Path.Combine(destinationDirectory, "runtimes", runtimeIdentifier),
            includePdb);
    }

    private static void CopyNativeHostDependencies(string sourceDirectory, string destinationDirectory)
    {
        foreach (string fileName in NativeHostDependencyFileNames)
            CopyIfExists(
                Path.Combine(sourceDirectory, fileName),
                Path.Combine(destinationDirectory, fileName));
    }

    private static void PackageCommonAssets(BuildContext context, ECommonAssetsPackageMode packageMode)
    {
        string commonAssetsArchivePath = Path.Combine(context.ContentOutputDirectory, CommonAssetsArchiveName);
        Directory.CreateDirectory(Path.GetDirectoryName(commonAssetsArchivePath)!);

        string? engineAssetsPath = Engine.Assets?.EngineAssetsPath;
        if (string.IsNullOrWhiteSpace(engineAssetsPath) || !Directory.Exists(engineAssetsPath))
            throw new DirectoryNotFoundException("The engine common-assets directory is unavailable.");

        XREngine.Editor.GpuLayouts.GpuRecordSpirvValidator.ValidateShaderDirectory(
            Path.Combine(engineAssetsPath, "Shaders"));

        if (packageMode == ECommonAssetsPackageMode.RuntimeShaders)
        {
            PackageRuntimeShaderAssets(engineAssetsPath, context.IntermediateDirectory, commonAssetsArchivePath);
            return;
        }

        if (!ShouldUseIncrementalArchives(context))
        {
            if (File.Exists(commonAssetsArchivePath))
                File.Delete(commonAssetsArchivePath);

            AssetPacker.Pack(engineAssetsPath, commonAssetsArchivePath);
            return;
        }

        RepackArchiveFromSourceIncremental(
            sourceDirectory: engineAssetsPath,
            archivePath: commonAssetsArchivePath,
            intermediateDirectory: context.IntermediateDirectory,
            deltaFolderName: "CommonAssetsDelta",
            transformFile: static (source, destination) => File.Copy(source, destination, true),
            packFullSourceDirectly: true);
    }

    private static void PackageRuntimeShaderAssets(
        string engineAssetsDirectory,
        string intermediateDirectory,
        string commonAssetsArchivePath)
    {
        string sourceShadersDirectory = Path.Combine(engineAssetsDirectory, "Shaders");
        if (!Directory.Exists(sourceShadersDirectory))
            throw new DirectoryNotFoundException($"The engine runtime shader directory was not found at '{sourceShadersDirectory}'.");

        string stagingDirectory = Path.Combine(intermediateDirectory, "Build", "RuntimeCommonAssets");
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, true);

        Directory.CreateDirectory(stagingDirectory);
        CopyDirectory(sourceShadersDirectory, Path.Combine(stagingDirectory, "Shaders"), includePdb: false);
        File.WriteAllText(
            Path.Combine(stagingDirectory, "manifest.json"),
            """{"format":"XREngine.CommonAssets","version":1,"scope":"runtime-shaders"}""",
            Encoding.UTF8);

        if (File.Exists(commonAssetsArchivePath))
            File.Delete(commonAssetsArchivePath);

        AssetPacker.Pack(stagingDirectory, commonAssetsArchivePath);
    }

    internal static string PackageRuntimeShaderAssetsForTests(
        string engineAssetsDirectory,
        string intermediateDirectory,
        string contentOutputDirectory)
    {
        string archivePath = Path.Combine(contentOutputDirectory, CommonAssetsArchiveName);
        Directory.CreateDirectory(contentOutputDirectory);
        PackageRuntimeShaderAssets(engineAssetsDirectory, intermediateDirectory, archivePath);
        return archivePath;
    }

    internal static string? ResolveGameLaunchBootstrapTypeName(string configuration, string platform)
    {
        using DynamicGameAssemblyScope? gameAssemblyScope = TryLoadBuiltGameAssembly(configuration, platform);
        if (gameAssemblyScope?.Assembly is null)
            return null;
        return ResolveGameLaunchBootstrapTypeName(gameAssemblyScope.Assembly);
    }

    internal static string? ResolveGameLaunchBootstrapTypeName(Assembly gameAssembly)
    {
        Type[] bootstrapTypes = [.. EnumerateLoadableTypes([gameAssembly])
            .Where(static type =>
                type is { IsAbstract: false, IsInterface: false } &&
                typeof(IGameLaunchBootstrap).IsAssignableFrom(type))];

        if (bootstrapTypes.Length == 0)
            return null;

        if (bootstrapTypes.Length > 1)
        {
            string names = string.Join(", ", bootstrapTypes.Select(static type => type.FullName ?? type.Name));
            throw new InvalidOperationException($"The game assembly contains multiple IGameLaunchBootstrap implementations: {names}.");
        }

        Type bootstrapType = bootstrapTypes[0];
        if (!(bootstrapType.IsPublic || bootstrapType.IsNestedPublic) ||
            bootstrapType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Game launch bootstrap '{bootstrapType.FullName}' must be public and expose a public parameterless constructor.");
        }

        return bootstrapType.FullName
            ?? throw new InvalidOperationException("The game launch bootstrap type has no stable full name.");
    }

    private static void BuildLauncherExecutable(BuildContext context, BuildSettings settings, string configuration, string platform)
    {
        if (!File.Exists(context.ConfigArchivePath))
            throw new FileNotFoundException("Config archive not found. Enable config generation before building the launcher.", context.ConfigArchivePath);

        string? gameLaunchBootstrapTypeName = ResolveGameLaunchBootstrapTypeName(configuration, platform);
        if (settings.PublishLauncherAsNativeAot && string.IsNullOrWhiteSpace(gameLaunchBootstrapTypeName))
        {
            throw new InvalidOperationException(
                "NativeAOT game launchers require exactly one public IGameLaunchBootstrap implementation " +
                "in the compiled game assembly so custom game types are statically rooted.");
        }

        string launcherPath = global::CodeManager.Instance.BuildLauncherExecutable(
            settings,
            configuration,
            platform,
            StartupAssetName,
            XRProject.EditorPreferencesFileName,
            XRProject.UserSettingsFileName,
            gameLaunchBootstrapTypeName);

        CopyLauncherArtifacts(launcherPath, context.BinariesOutputDirectory, settings.LauncherExecutableName, settings.IncludePdbFiles, settings.PublishLauncherAsNativeAot);

        if (settings.PublishLauncherAsNativeAot)
        {
            CopyRuntimeDependencies(
                AppContext.BaseDirectory,
                context.BinariesOutputDirectory,
                settings.IncludePdbFiles,
                "win-x64");
            CopyNativeHostDependencies(AppContext.BaseDirectory, context.BinariesOutputDirectory);
        }
    }

    private static void CopyLauncherArtifacts(string sourceExePath, string destinationDirectory, string? requestedName, bool includePdb, bool isNativeAot)
    {
        string executableName = NormalizeExecutableName(requestedName);
        Directory.CreateDirectory(destinationDirectory);

        string sourceDirectory = Path.GetDirectoryName(sourceExePath)!;
        string sourceBaseName = Path.GetFileNameWithoutExtension(sourceExePath);
        string targetBaseName = Path.GetFileNameWithoutExtension(executableName);

        File.Copy(sourceExePath, Path.Combine(destinationDirectory, executableName), true);

        if (isNativeAot)
        {
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, sourceExePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "aot-publish.log", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "aot-publish-warnings.md", StringComparison.OrdinalIgnoreCase))
                    continue;

                string extension = Path.GetExtension(file);
                if (string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase) && !includePdb)
                    continue;

                string relativePath = Path.GetRelativePath(sourceDirectory, file);
                if (string.Equals(relativePath, $"{sourceBaseName}.pdb", StringComparison.OrdinalIgnoreCase))
                    relativePath = $"{targetBaseName}.pdb";

                string destinationPath = Path.Combine(destinationDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(file, destinationPath, true);
            }

            return;
        }

        // Framework-dependent launchers need the managed entry assembly beside the renamed host exe.
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.dll"), Path.Combine(destinationDirectory, $"{targetBaseName}.dll"));
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.runtimeconfig.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.runtimeconfig.json"));
        CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.deps.json"), Path.Combine(destinationDirectory, $"{targetBaseName}.deps.json"));

        if (includePdb)
        {
            CopyIfExists(Path.Combine(sourceDirectory, $"{sourceBaseName}.pdb"), Path.Combine(destinationDirectory, $"{targetBaseName}.pdb"));
        }
    }

    internal static void CopyLauncherArtifactsForTests(
        string sourceExePath,
        string destinationDirectory,
        string requestedName,
        bool includePdb,
        bool isNativeAot)
        => CopyLauncherArtifacts(sourceExePath, destinationDirectory, requestedName, includePdb, isNativeAot);

    internal static void CopyRuntimeDependenciesForTests(
        string sourceDirectory,
        string destinationDirectory,
        bool includePdb,
        string? runtimeIdentifier = null)
        => CopyRuntimeDependencies(sourceDirectory, destinationDirectory, includePdb, runtimeIdentifier);

    internal static void CopyNativeHostDependenciesForTests(
        string sourceDirectory,
        string destinationDirectory)
        => CopyNativeHostDependencies(sourceDirectory, destinationDirectory);

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
            transformFile: TransformIncrementalContentFile,
            includeRelativePath: ShouldIncludeInCookedContent);
    }

    /// <summary>
    /// Cooks one file of the incremental content archive, then checks a cooked asset file for machine paths.
    /// </summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    internal static void TransformIncrementalContentFile(string source, string destination)
    {
        WritePublishedCookedFile(source, destination);
        if (string.Equals(Path.GetExtension(source), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            CookedOutputPathGuard.ThrowIfMachinePath(destination, source);
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
        Action<string, string> transformFile,
        bool packFullSourceDirectly = false,
        Func<string, bool>? includeRelativePath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        Dictionary<string, AssetTimestampEntry> currentSnapshot = CaptureDirectorySnapshot(sourceDirectory, includeRelativePath);
        string ledgerPath = SubMeshLodEntryLedger.GetPath(intermediateDirectory, deltaFolderName);

        if (!File.Exists(archivePath))
        {
            if (packFullSourceDirectly)
            {
                SubMeshLodEntryLedger.Delete(ledgerPath);
                AssetPacker.Pack(sourceDirectory, archivePath);
                return;
            }

            string fullStagingDir = Path.Combine(intermediateDirectory, "Build", deltaFolderName + "_Full");
            PackFullAndWriteLedger(sourceDirectory, fullStagingDir, archivePath, ledgerPath, currentSnapshot.Keys, transformFile);
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
            if (packFullSourceDirectly)
            {
                SubMeshLodEntryLedger.Delete(ledgerPath);
                if (File.Exists(archivePath))
                    File.Delete(archivePath);

                AssetPacker.Pack(sourceDirectory, archivePath);
                return;
            }

            string fullStagingDir = Path.Combine(intermediateDirectory, "Build", deltaFolderName + "_FallbackFull");
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            PackFullAndWriteLedger(sourceDirectory, fullStagingDir, archivePath, ledgerPath, currentSnapshot.Keys, transformFile);
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
            // LOD payload entries have no source file of their own; the cooked sub-mesh that refers to one writes it.
            if (!currentSnapshot.ContainsKey(existingPath) && !IsSubMeshLodPayloadEntry(existingPath))
                removedPaths.Add(existingPath);
        }

        if (changedPaths.Count == 0 && removedPaths.Count == 0)
            return;

        // The ledger knows the LOD entries of the files that this build does not cook again.
        // With no valid ledger, the build keeps all LOD entries, because it cannot know which entries are live.
        SubMeshLodEntryLedger? ledger = packFullSourceDirectly ? null : SubMeshLodEntryLedger.TryLoad(ledgerPath, archivePath);

        string deltaDirectory = Path.Combine(intermediateDirectory, "Build", deltaFolderName);
        if (changedPaths.Count > 0)
            StageFiles(sourceDirectory, deltaDirectory, changedPaths, transformFile, ledger);

        if (ledger is not null)
        {
            for (int i = 0; i < removedPaths.Count; i++)
                ledger.Remove(removedPaths[i]);

            HashSet<string> liveEntries = ledger.CollectLiveEntries();
            foreach (string existingPath in embeddedTimestamps.Keys)
                if (IsSubMeshLodPayloadEntry(existingPath) && !liveEntries.Contains(NormalizePath(existingPath)))
                    removedPaths.Add(existingPath);
        }

        AssetPacker.Repack(
            archivePath,
            changedPaths.Count > 0 ? deltaDirectory : string.Empty,
            [.. removedPaths]);
        ledger?.Save(ledgerPath, archivePath);
    }

    private static void PackFullAndWriteLedger(
        string sourceDirectory,
        string stagingDirectory,
        string archivePath,
        string ledgerPath,
        IEnumerable<string> relativePaths,
        Action<string, string> transformFile)
    {
        SubMeshLodEntryLedger ledger = new();
        StageFiles(sourceDirectory, stagingDirectory, relativePaths, transformFile, ledger);
        AssetPacker.Pack(stagingDirectory, archivePath);
        ledger.Save(ledgerPath, archivePath);
    }

    private static void StageFiles(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<string> relativePaths,
        Action<string, string> transformFile,
        SubMeshLodEntryLedger? ledger = null)
    {
        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, true);
        Directory.CreateDirectory(destinationDirectory);

        // A cooked sub-mesh writes its separate LOD payloads next to the staged files.
        using SubMeshLodPayloadCookSink lodPayloads = SubMeshLodPayloadCookSink.Begin(destinationDirectory);
        foreach (string relativePath in relativePaths)
        {
            string sourcePath = Path.Combine(sourceDirectory, ToSystemPath(relativePath));
            if (!File.Exists(sourcePath))
            {
                ledger?.Remove(relativePath);
                continue;
            }

            string destinationPath = Path.Combine(destinationDirectory, ToSystemPath(relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            transformFile(sourcePath, destinationPath);

            // Take the entries after each file, so the ledger knows the LOD entries of each cooked file.
            string[] referencedEntries = lodPayloads.TakeReferencedEntries();
            ledger?.Set(relativePath, referencedEntries);
        }
    }

    private static Dictionary<string, AssetTimestampEntry> CaptureDirectorySnapshot(
        string rootDirectory,
        Func<string, bool>? includeRelativePath = null)
    {
        Dictionary<string, AssetTimestampEntry> snapshot = new(StringComparer.Ordinal);
        foreach (string filePath in Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = NormalizePath(Path.GetRelativePath(rootDirectory, filePath));
            if (includeRelativePath is not null && !includeRelativePath(relativePath))
                continue;

            var fileInfo = new FileInfo(filePath);
            snapshot[relativePath] = new AssetTimestampEntry(relativePath, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
        }

        return snapshot;
    }

    #endregion

    #region Content Cooking

    /// <summary>True for a separate LOD payload entry of a cooked sub-mesh (<c>lod/&lt;key&gt;</c>).</summary>
    private static bool IsSubMeshLodPayloadEntry(string relativePath)
        => NormalizePath(relativePath).StartsWith(SubMeshLodDescriptor.EntryPrefix, StringComparison.Ordinal);

    private static readonly HashSet<string> ExcludedCookedContentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".user", ".pdb", ".editorconfig"
    };

    private static bool ShouldIncludeInCookedContent(string relativePath)
    {
        string normalized = NormalizePath(relativePath);
        if (ExcludedCookedContentExtensions.Contains(Path.GetExtension(normalized)))
            return false;

        // These files are launcher configuration, not game content. Startup settings
        // are emitted to GameConfig.pak and state is created by IGameLaunchBootstrap.
        if (!normalized.Contains('/') &&
            (string.Equals(normalized, StartupAssetName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, "state.asset", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WritePublishedCookedFile(string sourceFile, string destination)
    {
        if (!string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceFile, destination, true);
            return;
        }

        string yaml = File.ReadAllText(sourceFile, Encoding.UTF8);
        CookedAssetBlob blob;
        try
        {
            blob = CreateCookedBlobFromYaml(yaml, sourceFile);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException($"Published asset '{sourceFile}' could not be cooked.", ex);
        }

        if (blob.Format != CookedAssetFormat.RuntimeBinaryV1)
        {
            throw new InvalidOperationException(
                $"Published NativeAOT asset '{sourceFile}' cooked as {blob.Format}; " +
                $"register an AOT-safe {nameof(PublishedCookedAssetRegistry)} serializer for its runtime type.");
        }

        // The registered sub-mesh codec still reads its LOD materials with the reflective cooked serializer, which a
        // published NativeAOT runtime cannot run. Fail here, not at load.
        if (string.Equals(blob.TypeReference, CookedAssetTypeReference.Encode(typeof(SubMesh)), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Published NativeAOT asset '{sourceFile}' is a {nameof(SubMesh)}. Its cooked codec reads the LOD materials with " +
                "the reflective cooked serializer, which NativeAOT builds do not support. Publish without NativeAOT.");
        }

        WriteCookedBlob(destination, blob);
    }

    /// <summary>
    /// Cooks one asset file of the content cook. A cook does not ship a YAML source: the YAML can hold machine paths, also
    /// inside compressed payloads that the output check cannot read. So an asset file with no type hint, or an asset whose
    /// cook fails, fails the cook with an error that names the file.
    /// </summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WriteCookedContentAssetFile(string sourceFile, string destination)
    {
        string yaml = File.ReadAllText(sourceFile, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(ExtractAssetTypeHint(yaml)))
            throw new InvalidOperationException(
                $"Asset '{sourceFile}' could not be cooked: it has no __assetType or __type hint. A cook does not ship YAML sources.");

        CookedAssetBlob blob;
        try
        {
            blob = CreateCookedBlobFromYaml(yaml, sourceFile);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            throw new InvalidOperationException(
                $"Asset '{sourceFile}' could not be cooked, and a cook does not ship YAML sources: {ex.Message}", ex);
        }

        WriteCookedBlob(destination, blob);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static void WriteCookedAsset(object data, string destination, AotRuntimeMetadata? aotMetadata = null,
        CookedBinarySerializationCallbacks? callbacks = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var blob = CreateCookedBlob(data, aotMetadata, callbacks);
        WriteCookedBlob(destination, blob);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static string PrepareCookedContentDirectory(BuildContext context, Action<CookProgress>? progress = null)
    {
        string cookedRoot = Path.Combine(context.IntermediateDirectory, "Build", "CookedContent");
        if (Directory.Exists(cookedRoot))
            Directory.Delete(cookedRoot, true);
        Directory.CreateDirectory(cookedRoot);

        string[] sourceFiles = [.. Directory.GetFiles(context.AssetsDirectory, "*", SearchOption.AllDirectories)
            .Where(sourceFile => ShouldIncludeInCookedContent(
                NormalizePath(Path.GetRelativePath(context.AssetsDirectory, sourceFile))))];
        int totalFiles = sourceFiles.Length;

        // A cooked sub-mesh writes each LOD other than its coarsest one as a separate lod/<key> entry in the cooked root.
        using SubMeshLodPayloadCookSink lodPayloads = SubMeshLodPayloadCookSink.Begin(cookedRoot);
        for (int i = 0; i < sourceFiles.Length; i++)
        {
            string sourceFile = sourceFiles[i];
            string relative = Path.GetRelativePath(context.AssetsDirectory, sourceFile);
            string destination = Path.Combine(cookedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            bool cookedAsBinaryAsset = false;

            if (string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Settings.PublishLauncherAsNativeAot)
                {
                    WritePublishedCookedFile(sourceFile, destination);
                    cookedAsBinaryAsset = true;
                }
                else
                {
                    WriteCookedContentAssetFile(sourceFile, destination);
                    cookedAsBinaryAsset = true;
                }

                progress?.Invoke(new CookProgress(i + 1, totalFiles, relative, cookedAsBinaryAsset));
                continue;
            }

            File.Copy(sourceFile, destination, true);
            progress?.Invoke(new CookProgress(i + 1, totalFiles, relative, cookedAsBinaryAsset));
        }

        GuardCookedContent(context.AssetsDirectory, cookedRoot, sourceFiles);
        return cookedRoot;
    }

    /// <summary>
    /// Fails the cook if a cooked asset file, or a separate LOD entry, holds a machine path. Files that the cook copies as
    /// they are (not asset files) are not checked.
    /// </summary>
    private static void GuardCookedContent(string assetsDirectory, string cookedRoot, string[] sourceFiles)
    {
        foreach (string sourceFile in sourceFiles)
        {
            if (!string.Equals(Path.GetExtension(sourceFile), $".{AssetManager.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(assetsDirectory, sourceFile);
            CookedOutputPathGuard.ThrowIfMachinePath(Path.Combine(cookedRoot, relative), NormalizePath(relative));
        }

        string lodDirectory = Path.Combine(cookedRoot, "lod");
        if (!Directory.Exists(lodDirectory))
            return;

        foreach (string lodFile in Directory.GetFiles(lodDirectory, "*", SearchOption.AllDirectories))
            CookedOutputPathGuard.ThrowIfMachinePath(lodFile, NormalizePath(Path.GetRelativePath(cookedRoot, lodFile)));
    }

    /// <summary>Runs the incremental archive repack with a test transform. The delta folder name is fixed.</summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    internal static void RepackArchiveIncrementalForTests(
        string sourceDirectory,
        string archivePath,
        string intermediateDirectory,
        Action<string, string> transformFile)
        => RepackArchiveFromSourceIncremental(sourceDirectory, archivePath, intermediateDirectory, "TestDelta", transformFile);

    /// <summary>Gets the LOD entry ledger file that <see cref="RepackArchiveIncrementalForTests"/> uses.</summary>
    internal static string GetRepackLedgerPathForTests(string intermediateDirectory)
        => SubMeshLodEntryLedger.GetPath(intermediateDirectory, "TestDelta");

    internal static string PrepareCookedContentDirectoryForTests(
        string assetsDirectory,
        string intermediateDirectory,
        Action<CookProgress>? progress = null,
        bool publishLauncherAsNativeAot = false)
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
            new BuildSettings
            {
                PublishLauncherAsNativeAot = publishLauncherAsNativeAot
            },
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
        => CreateCookedBlobFromYaml(yaml, sourcePath, out _);

    /// <summary>Cooks a YAML asset, and gives the loaded asset (null if the load failed) for the error handling of the caller.</summary>
    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    private static CookedAssetBlob CreateCookedBlobFromYaml(string yaml, string sourcePath, out object? loaded)
    {
        loaded = null;
        string? typeHint = ExtractAssetTypeHint(yaml);
        if (string.IsNullOrWhiteSpace(typeHint))
            throw new InvalidOperationException($"Asset '{sourcePath}' is missing an __assetType or __type hint and cannot be cooked.");

        Type assetType = ResolveAssetType(typeHint)
            ?? throw new InvalidOperationException($"Unable to resolve asset type '{typeHint}' referenced by '{sourcePath}'.");

        object asset = DeserializeAssetFromYaml(yaml, assetType)
            ?? throw new InvalidOperationException($"Failed to deserialize asset '{sourcePath}' as '{assetType.FullName}'.");
        loaded = asset;

        if (asset is XRAsset xrAsset)
            CookedCopyGraph.ReleaseLiveAssets(xrAsset);

        // The cooked build runs from another folder, so the asset headers get portable source paths and no file paths.
        CookedAssetBlob blob;
        using (CookedSourcePathScope sourcePaths = CookedSourcePathProjection.Begin())
        {
            blob = CreateCookedBlob(asset);
            ThrowIfSourcePathsNotPortable(sourcePaths, sourcePath);
        }
        return blob;
    }

    /// <summary>Fails the cook of an asset file if a source path in it is outside every asset root.</summary>
    private static void ThrowIfSourcePathsNotPortable(CookedSourcePathScope sourcePaths, string sourcePath)
    {
        if (sourcePaths.Failures.Count == 0)
            return;

        StringBuilder message = new();
        message.Append("Asset file '").Append(sourcePath).Append("' could not be cooked: ")
            .Append(sourcePaths.Failures.Count).Append(" source path(s) are outside the game and engine asset folders, ")
            .Append("so the cooked build would hold machine paths. Move the source files into an asset folder, or remove the paths.");
        foreach (CookedSourcePathFailure failure in sourcePaths.Failures)
            message.AppendLine().Append("  asset '").Append(failure.AssetName).Append("': ").Append(failure.Path);
        throw new CookedSourcePathException(message.ToString());
    }

    private static void WriteCookedBlob(string destination, CookedAssetBlob blob)
    {
        byte[] cookedBytes = CookedAssetEnvelope.Serialize(blob);
        File.WriteAllBytes(destination, cookedBytes);
    }

    [RequiresUnreferencedCode("Cooking assets reflects over concrete asset types to build binary payloads.")]
    internal static CookedAssetBlob CreateCookedBlob(object instance, AotRuntimeMetadata? aotMetadata = null,
        CookedBinarySerializationCallbacks? callbacks = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Type runtimeType = instance.GetType();
        string typeName = CookedAssetTypeReference.Encode(runtimeType, aotMetadata);

        if (PublishedCookedAssetRegistry.TrySerialize(instance, out byte[] runtimePayload))
            return new CookedAssetBlob(typeName, CookedAssetFormat.RuntimeBinaryV1, runtimePayload);

        // A nested MemoryPack asset envelope starts an independent serialization and
        // would hide its materials from this explicit platform projection. Registered
        // serializers above remain authoritative and retain their original format.
        byte[] payload = callbacks is null ? CookedBinarySerializer.Serialize(instance)
            : CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(instance, callbacks));
        return new CookedAssetBlob(typeName, CookedAssetFormat.BinaryV2, payload);
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
            string prefix;
            if (trimmed.StartsWith("__assetType:", StringComparison.Ordinal))
                prefix = "__assetType:";
            else if (trimmed.StartsWith("__type:", StringComparison.Ordinal))
                prefix = "__type:";
            else
                continue;

            string value = trimmed[prefix.Length..].Trim();
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
            EBuildPlatform.BrowserWebGPU => global::CodeManager.Platform_AnyCPU,
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
