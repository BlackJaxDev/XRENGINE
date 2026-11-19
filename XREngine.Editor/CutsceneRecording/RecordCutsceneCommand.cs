using System.Diagnostics;
using System.Globalization;
using XREngine.Cutscenes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// The <c>--record-cutscene</c> command. It starts the engine with one hidden window, loads a saved world, opens the
/// cutscene of a <see cref="CutsceneDirectorComponent"/> in that world, and records it with
/// <see cref="CutsceneLockstepRecorder"/>. The world holds all content of the video, so the command has no content values.
/// </summary>
public static class RecordCutsceneCommand
{
    /// <summary>The command line flag that selects this command.</summary>
    public const string CommandFlag = "--record-cutscene";

    private const string ProjectFlag = "--project";
    private const string WorldFlag = "--world";
    private const string DirectorFlag = "--director";
    private const string OutputDirFlag = "--output-dir";
    private const string BackendFlag = "--backend";
    private const string FfmpegFlag = "--ffmpeg";
    private const string WidthFlag = "--width";
    private const string HeightFlag = "--height";
    private const string FpsFlag = "--fps";
    private const string DurationFlag = "--duration";
    private const string VideoEncoderFlag = "--video-encoder";
    private const string OptimizedMaxBytesFlag = "--optimized-max-bytes";
    private const string ContactSheetFlag = "--contact-sheet";
    private const string PosterTimeFlag = "--poster-time";
    private const string SubstepsFlag = "--physics-substeps";

#if DEBUG
    private const string BuildConfigurationName = "Debug";
#else
    private const string BuildConfigurationName = "Release";
#endif

    /// <summary>
    /// Runs the command when <paramref name="args"/> contains <see cref="CommandFlag"/>, and sets the process exit code.
    /// </summary>
    /// <returns>True when the command ran; false when <paramref name="args"/> is for another mode.</returns>
    public static bool TryRun(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.Any(static arg => string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase)))
            return false;

        Environment.ExitCode = Run(args);
        return true;
    }

    /// <summary>Parses the options, starts the engine, records the cutscene, and returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        long runStartTimestamp = Stopwatch.GetTimestamp();
        if (!TryParseOptions(args, out RecordOptions? options, out string? error) || options is null)
        {
            Console.Error.WriteLine($"Cutscene recording failed: {error}");
            return 2;
        }

        if (!File.Exists(options.WorldPath))
        {
            Console.Error.WriteLine($"Cutscene recording failed: the world '{options.WorldPath}' does not exist.");
            return 2;
        }

        Console.WriteLine(
            $"Recording cutscene: world='{options.WorldPath}', director='{options.DirectorPath ?? "<only director>"}', " +
            $"output='{options.OutputDirectory}', backend={options.Backend}, size={options.Settings.Width}x{options.Settings.Height}, " +
            $"fps={options.Settings.FramesPerSecond}, videoEncoder={options.Settings.VideoEncoder}, build={BuildConfigurationName}.");

        int exitCode = 1;
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(90));

        void AfterWindowsCreated(GameStartupSettings startupSettings, GameState state)
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            _ = Task.Run(async () =>
            {
                try
                {
                    exitCode = await ExecuteAsync(options, runStartTimestamp, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Cutscene recording failed with an unexpected error: {ex}");
                    exitCode = 1;
                }
                finally
                {
                    Environment.ExitCode = exitCode;
                    RuntimeEngine.EnqueueRenderThreadTask(Engine.ShutDown, "RecordCutscene.ShutDown", RenderThreadJobKind.RequiresGraphicsContext);
                }
            });
        }

        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.PreferredRenderBackend, options.Backend);
        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.VSync, EVSyncMode.Off);
        Engine.AfterCreateWindows += AfterWindowsCreated;
        try
        {
            Engine.Run(CreateStartupSettings(options), new GameState(), beginPlayingAllWorlds: false);
        }
        finally
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            cts.Cancel();
        }

        Environment.ExitCode = exitCode;
        return exitCode;
    }

    private static async Task<int> ExecuteAsync(RecordOptions options, long runStartTimestamp, CancellationToken cancellationToken)
    {
        if (RuntimeEngine.Windows.Count == 0 || RuntimeEngine.Windows[0].Viewports.Count == 0)
            throw new InvalidOperationException("No engine window with a viewport was created for the cutscene recording.");

        XRWindow window = RuntimeEngine.Windows[0];
        XRViewport viewport = window.Viewports[0];

        if (options.ProjectPath is { } projectPath && !Engine.LoadProject(projectPath))
            throw new InvalidOperationException($"The project '{projectPath}' did not load.");

        XRWorld world = Engine.Assets?.Load<XRWorld>(options.WorldPath)
            ?? throw new InvalidOperationException($"The world '{options.WorldPath}' did not load.");

        List<SceneNode> rootNodes = [];
        foreach (XRScene scene in world.Scenes)
            rootNodes.AddRange(scene.RootNodes);

        using CutsceneCapturePreparation preparation = new();

        // The Uber material setup reads shader files, so it runs here on a worker thread, before the world begins play.
        int materialCount = preparation.PrepareWorldMaterials(rootNodes);
        Console.WriteLine($"Prepared world materials: materials={materialCount}, textureRequests={preparation.RequestedTextureCount}.");

        CutsceneRecordingScene? recordingScene = null;
        await CutsceneLockstepRecorder.RunOnUpdateThreadAsync(() =>
        {
            RuntimeWorld runtimeWorld = Engine.GetOrCreateWorld(world);
            Task beginPlay = RuntimeWorldHostServices.Current?.BeginPlayAsync(runtimeWorld) ?? runtimeWorld.BeginPlayAsync();
            beginPlay.GetAwaiter().GetResult();
            window.SetWorld(Engine.GetOrCreateRenderWorld(world));

            CutsceneDirectorComponent director = FindDirector(rootNodes, options.DirectorPath);
            recordingScene = new CutsceneRecordingScene(window, viewport, runtimeWorld, director, options.Settings, preparation);
        }, cancellationToken).ConfigureAwait(false);

        CutsceneRecordingScene sceneState = recordingScene!;

        // A component that loads data at activation, for example a cache from its saved path, finishes before the capture.
        await WaitForComponentLoadsAsync(rootNodes, cancellationToken).ConfigureAwait(false);

        await CutsceneLockstepRecorder.RunOnUpdateThreadAsync(() =>
        {
            sceneState.Prepare();
            IReadOnlyList<CutsceneCompileDiagnostic> diagnostics = sceneState.OpenCutscene(cancellationToken);
            for (int i = 0; i < diagnostics.Count; i++)
                Console.WriteLine($"[Cutscene diagnostic] {diagnostics[i].Message}");
        }, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"Cutscene opened: duration={sceneState.GetDurationSeconds():F2}s, cuts={sceneState.GetCameraCutStartSeconds().Count}, " +
            $"physicsChains={sceneState.PhysicsChainCount}, readinessSources={sceneState.ReadinessSourceCount}.");

        try
        {
            CutsceneRecordingResult result = await CutsceneLockstepRecorder.RecordAsync(
                sceneState,
                options.OutputDirectory,
                options.FfmpegPath,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Poster written to: {result.PosterPath}");
            Console.WriteLine($"Cutscene video written to: {result.MasterVideoPath}");
            if (result.OptimizedVideoPath is { } optimizedPath)
                Console.WriteLine($"Optimized file size copy written to: {optimizedPath} ({new FileInfo(optimizedPath).Length} bytes)");
            if (result.ContactSheetPath is { } contactSheetPath)
                Console.WriteLine($"Contact sheet written to: {contactSheetPath}");
            Console.WriteLine(
                $"[CutsceneTiming] warmup={result.WarmupSeconds:F1}s, capture+masterEncode={result.CaptureSeconds:F1}s, " +
                $"optimizedEncode={result.OptimizedEncodeSeconds:F1}s, contactSheet={result.ContactSheetSeconds:F1}s, " +
                $"total={Stopwatch.GetElapsedTime(runStartTimestamp).TotalSeconds:F1}s.");
            return 0;
        }
        finally
        {
            await CutsceneLockstepRecorder.RunOnUpdateThreadAsync(sceneState.Detach, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WaitForComponentLoadsAsync(IReadOnlyList<SceneNode> rootNodes, CancellationToken cancellationToken)
    {
        List<Task> loads = [];
        for (int i = 0; i < rootNodes.Count; i++)
        {
            rootNodes[i].IterateComponents<XREngine.Components.Scene.Mesh.GaussianCacheComponent>(cache =>
            {
                if (cache.CacheLoadTask is { } load)
                    loads.Add(load);
            }, iterateChildHierarchy: true);
        }

        if (loads.Count > 0)
            await Task.WhenAll(loads).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the director at a node path such as <c>Root/Cutscene</c>, at a node name, or the only director of the world
    /// when <paramref name="directorPath"/> is null.
    /// </summary>
    private static CutsceneDirectorComponent FindDirector(IReadOnlyList<SceneNode> rootNodes, string? directorPath)
    {
        List<(string Path, CutsceneDirectorComponent Director)> directors = [];
        for (int i = 0; i < rootNodes.Count; i++)
            CollectDirectors(rootNodes[i], rootNodes[i].Name ?? string.Empty, directors);

        if (directorPath is null)
        {
            return directors.Count switch
            {
                1 => directors[0].Director,
                0 => throw new InvalidOperationException("The world has no CutsceneDirectorComponent."),
                _ => throw new InvalidOperationException(
                    $"The world has {directors.Count} directors. Choose one with {DirectorFlag}: " +
                    string.Join(", ", directors.Select(static entry => entry.Path))),
            };
        }

        foreach ((string path, CutsceneDirectorComponent director) in directors)
        {
            if (string.Equals(path, directorPath, StringComparison.Ordinal) ||
                string.Equals(director.SceneNode?.Name, directorPath, StringComparison.Ordinal))
            {
                return director;
            }
        }

        throw new InvalidOperationException(
            $"No CutsceneDirectorComponent is at '{directorPath}'. Directors in the world: " +
            string.Join(", ", directors.Select(static entry => entry.Path)));
    }

    private static void CollectDirectors(SceneNode node, string path, List<(string Path, CutsceneDirectorComponent Director)> directors)
    {
        foreach (CutsceneDirectorComponent director in node.GetComponents<CutsceneDirectorComponent>())
            directors.Add((path, director));

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is { } childNode)
                CollectDirectors(childNode, $"{path}/{childNode.Name}", directors);
        }
    }

    private static GameStartupSettings CreateStartupSettings(RecordOptions options)
        => new()
        {
            StartupWindows =
            [
                new GameWindowStartupSettings
                {
                    WindowTitle = "XRE Cutscene Recording",
                    WindowState = EWindowState.Windowed,
                    Width = options.Settings.Width,
                    Height = options.Settings.Height,
                    UseNativeTitleBar = false,
                    Visible = false,
                },
            ],
            DefaultUserSettings = new UserSettings
            {
                PreferredRenderBackend = options.Backend,
                VSync = EVSyncMode.Off,
            },
            NetworkingType = ENetworkingType.Local,
        };

    private static bool TryParseOptions(IReadOnlyList<string> args, out RecordOptions? options, out string? error)
    {
        options = null;
        error = null;
        CutsceneRecordSettings settings = new();
        string? projectPath = null;
        string? worldPath = null;
        string? directorPath = null;
        string? outputDirectory = null;
        string ffmpegPath = "ffmpeg";
        ERenderLibrary backend = ERenderLibrary.Vulkan;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i] ?? string.Empty;
            if (string.Equals(arg, CommandFlag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 >= args.Count)
            {
                error = $"The option '{arg}' requires a value.";
                return false;
            }

            string value = args[++i] ?? string.Empty;
            switch (arg.ToLowerInvariant())
            {
                case ProjectFlag:
                    projectPath = Path.GetFullPath(value);
                    break;
                case WorldFlag:
                    worldPath = Path.GetFullPath(value);
                    break;
                case DirectorFlag:
                    directorPath = value;
                    break;
                case OutputDirFlag:
                    outputDirectory = Path.GetFullPath(value);
                    break;
                case FfmpegFlag:
                    ffmpegPath = value;
                    break;
                case BackendFlag:
                    if (string.Equals(value, "vulkan", StringComparison.OrdinalIgnoreCase))
                        backend = ERenderLibrary.Vulkan;
                    else if (string.Equals(value, "opengl", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "gl", StringComparison.OrdinalIgnoreCase))
                        backend = ERenderLibrary.OpenGL;
                    else
                    {
                        error = $"The {BackendFlag} value '{value}' must be 'vulkan' or 'opengl'.";
                        return false;
                    }
                    break;
                case WidthFlag:
                    if (!TryParseInt(value, 16, 8192, WidthFlag, out int width, out error))
                        return false;
                    settings.Width = width;
                    break;
                case HeightFlag:
                    if (!TryParseInt(value, 16, 8192, HeightFlag, out int height, out error))
                        return false;
                    settings.Height = height;
                    break;
                case FpsFlag:
                    if (!TryParseInt(value, 1, 240, FpsFlag, out int fps, out error))
                        return false;
                    settings.FramesPerSecond = fps;
                    break;
                case SubstepsFlag:
                    if (!TryParseInt(value, 1, 64, SubstepsFlag, out int substeps, out error))
                        return false;
                    settings.PhysicsSubstepsPerFrame = substeps;
                    break;
                case DurationFlag:
                    if (!TryParsePositiveFloat(value, DurationFlag, out float duration, out error))
                        return false;
                    settings.DurationSeconds = duration;
                    break;
                case PosterTimeFlag:
                    if (!TryParsePositiveFloat(value, PosterTimeFlag, out float posterTime, out error))
                        return false;
                    settings.PosterTimeSeconds = posterTime;
                    break;
                case VideoEncoderFlag:
                    if (!CutsceneVideoEncoderArguments.TryParse(value, out ECutsceneVideoEncoder encoder))
                    {
                        error = $"The {VideoEncoderFlag} value '{value}' must be 'nvenc' or 'x264'.";
                        return false;
                    }
                    settings.VideoEncoder = encoder;
                    break;
                case OptimizedMaxBytesFlag:
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long maxBytes) || maxBytes < 0)
                    {
                        error = $"The {OptimizedMaxBytesFlag} value '{value}' must be 0 or a positive byte count.";
                        return false;
                    }
                    settings.OptimizedFileSizeMaxBytes = maxBytes;
                    break;
                case ContactSheetFlag:
                    if (!bool.TryParse(value, out bool contactSheet))
                    {
                        error = $"The {ContactSheetFlag} value '{value}' must be 'true' or 'false'.";
                        return false;
                    }
                    settings.ContactSheetEnabled = contactSheet;
                    break;
                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        if (worldPath is null)
        {
            error = $"Missing required option {WorldFlag}.";
            return false;
        }

        if (outputDirectory is null)
        {
            error = $"Missing required option {OutputDirFlag}.";
            return false;
        }

        options = new RecordOptions(projectPath, worldPath, directorPath, outputDirectory, ffmpegPath, backend, settings);
        return true;
    }

    private static bool TryParseInt(string value, int min, int max, string flag, out int result, out string? error)
    {
        error = null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= min && result <= max)
            return true;

        error = $"The {flag} value '{value}' must be between {min} and {max}.";
        return false;
    }

    private static bool TryParsePositiveFloat(string value, string flag, out float result, out string? error)
    {
        error = null;
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && float.IsFinite(result) && result >= 0.0f)
            return true;

        error = $"The {flag} value '{value}' must be a finite number of seconds, 0 or more.";
        return false;
    }

    private sealed record RecordOptions(
        string? ProjectPath,
        string WorldPath,
        string? DirectorPath,
        string OutputDirectory,
        string FfmpegPath,
        ERenderLibrary Backend,
        CutsceneRecordSettings Settings);
}
