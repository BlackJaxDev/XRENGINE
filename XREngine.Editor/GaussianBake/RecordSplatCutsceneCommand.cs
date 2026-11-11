using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Budgets;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Motion;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Renders a 1080p Gaussian splat cutscene video and matching hero poster.
/// Supports live Uber-shaded avatar surface splats from a cooked <c>.pak</c> archive (with live skeletal animation and
/// <see cref="PhysicsChainComponent"/> simulation) as well as baked <see cref="GaussianCacheComponent"/> playback.
/// </summary>
public static class RecordSplatCutsceneCommand
{
    /// <summary>The command line flag that selects this command.</summary>
    public const string CommandFlag = "--record-splat-cutscene";

    private const string PakFlag = "--pak";
    private const string AssetFlag = "--asset";
    private const string ClipFlag = "--clip";
    private const string SplatsFlag = "--splats";
    private const string CacheFlag = "--cache";
    private const string OutputDirFlag = "--output-dir";
    private const string TitleFlag = "--title";
    private const string SubtitleFlag = "--subtitle";
    private const string DurationFlag = "--duration";
    private const string FpsFlag = "--fps";
    private const string BackendFlag = "--backend";
    private const string FfmpegFlag = "--ffmpeg";
    private const string YawOffsetFlag = "--yaw-offset";

    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;
    private const float ViewportAspectRatio = FrameWidth / (float)FrameHeight;
    private const string DefaultAssetPath = "Avatars/avatar.asset";
    private const string DefaultClipName = "Sexy Walk";
    private const int DefaultLiveSplatCount = 2_400_000;
    private const string DefaultTitle = "XRENGINE";
    private const string DefaultSubtitle = "GAUSSIAN SPLAT AVATAR  //  CUTSCENE PLAYBACK";
    private const float DefaultDurationSeconds = 8.8f;
    private const int DefaultFps = 30;
    private const int ShotCameraCount = 5;
    private const int WarmupRenderFrames = 45;
    private const int SettleRenderFramesPerStep = 2;
    private const int PhysicsSubstepsPerCapturedFrame = 2;
    private const int PhysicsGravitySettleSubsteps = 30;
    private const float PhysicsLeadInWindowSeconds = 0.45f;
    private const long RemoteProxyByteLimit = 2_097_152L;
    private const long RemoteTargetBytes = 1_650_000L;

    private static readonly RenderInfo.DelAddRenderCommandsCallback RejectAllRenderPassesCallback = static (_, _, _) => false;

    /// <summary>
    /// Runs the cutscene recording command when <paramref name="args"/> contains <see cref="CommandFlag"/>.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>True when the command ran; false when <paramref name="args"/> is for another mode.</returns>
    public static bool TryRun(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!HasCommandFlag(args))
            return false;

        int exitCode = Run(args);
        Environment.ExitCode = exitCode;
        return true;
    }

    /// <summary>
    /// Parses the options, starts the engine with the requested graphics backend, records the cutscene sequence,
    /// copies the hero poster frame, and encodes the MP4 cutscene clips with ffmpeg.
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryParseOptions(args, out CutsceneCommandOptions? options, out string? error) || options is null)
        {
            Console.Error.WriteLine($"Splat cutscene recording failed: {error}");
            return 2;
        }

        if (options.UseLiveAvatarSplats)
        {
            if (!File.Exists(options.PakPath))
            {
                Console.Error.WriteLine($"Splat cutscene recording failed: the cooked pak '{options.PakPath}' does not exist.");
                return 2;
            }
        }
        else if (!File.Exists(options.CacheAssetPath))
        {
            Console.Error.WriteLine($"Splat cutscene recording failed: the cache root asset '{options.CacheAssetPath}' does not exist.");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        int totalFrames = Math.Clamp((int)MathF.Round(options.DurationSeconds * options.Fps), 60, ViewportSequenceCaptureOptions.MaximumFrames);
        float effectiveDurationSeconds = totalFrames / (float)options.Fps;

        string sourceSummary = options.UseLiveAvatarSplats
            ? $"pak='{options.PakPath}', asset='{options.AssetPath}', clip='{options.ClipName}', splats={options.SplatCount}"
            : $"cache='{options.CacheAssetPath}'";
        Console.WriteLine(
            $"Recording splat cutscene: {sourceSummary}, output='{options.OutputDirectory}', " +
            $"backend={options.Backend}, frames={totalFrames}, fps={options.Fps}, duration={effectiveDurationSeconds:F2}s.");

        int exitCode = 1;
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(20));

        void AfterWindowsCreated(GameStartupSettings settings, GameState state)
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;

            _ = Task.Run(async () =>
            {
                try
                {
                    exitCode = await ExecuteRecordingAsync(
                        options,
                        effectiveDurationSeconds,
                        totalFrames,
                        cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Splat cutscene recording failed with an unexpected error: {ex}");
                    exitCode = 1;
                }
                finally
                {
                    Environment.ExitCode = exitCode;
                    RuntimeEngine.EnqueueRenderThreadTask(
                        Engine.ShutDown,
                        "RecordSplatCutscene.ShutDown",
                        RenderThreadJobKind.RequiresGraphicsContext);
                }
            });
        }

        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.PreferredRenderBackend, options.Backend);
        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.VSync, EVSyncMode.Off);
        Engine.AfterCreateWindows += AfterWindowsCreated;
        try
        {
            Engine.Run(CreateStartupSettings(options.Backend), new GameState(), beginPlayingAllWorlds: true);
        }
        finally
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;
            cts.Cancel();
        }

        Environment.ExitCode = exitCode;
        return exitCode;
    }

    private static async Task<int> ExecuteRecordingAsync(
        CutsceneCommandOptions options,
        float durationSeconds,
        int totalFrames,
        CancellationToken cancellationToken)
    {
        if (RuntimeEngine.Windows.Count == 0)
            throw new InvalidOperationException("No engine window was created for the cutscene recording.");

        XRWindow window = RuntimeEngine.Windows[0];
        if (window.Viewports.Count == 0)
            throw new InvalidOperationException("The recording window has no viewport.");

        XRViewport viewport = window.Viewports[0];
        Console.WriteLine(
            $"Window created: backend={window.DesktopWindowBackend?.GraphicsApi}, " +
            $"framebuffer={window.EffectiveFramebufferSize.X}x{window.EffectiveFramebufferSize.Y}, " +
            $"viewport={viewport.Width}x{viewport.Height}.");

        // Prewarm the default UI font on this background thread before building the scene on the update thread.
        FontGlyphSet defaultFont = FontGlyphSet.LoadDefaultUIFont();

        CutsceneShotScene? shotScene = null;
        await RunOnUpdateThreadAsync(() =>
        {
            shotScene = BuildCutsceneShotScene(
                window,
                viewport,
                defaultFont,
                options,
                durationSeconds);
        }, cancellationToken).ConfigureAwait(false);

        if (shotScene is null)
            throw new InvalidOperationException("Failed to construct the cutscene shot scene.");

        try
        {
            AABB avatarBounds;
            float defaultYawDegrees = 0.0f;
            float forwardClipDuration = durationSeconds;

            if (shotScene.AvatarSplatComponent is { } liveSplats)
            {
                avatarBounds = shotScene.LiveAvatarBounds;
                forwardClipDuration = shotScene.MotionChoice is { DurationSeconds: > 0.01f } motion
                    ? motion.DurationSeconds
                    : durationSeconds;
                Console.WriteLine(
                    $"Initialized live Uber avatar splats: splats={liveSplats.TotalSplats}, slices={liveSplats.SubmeshSliceCount}, " +
                    $"materials={liveSplats.MaterialCount}, textures2D={liveSplats.Texture2DCount}, " +
                    $"clip='{shotScene.MotionChoice?.ClipName ?? "none"}' ({forwardClipDuration:F2}s), " +
                    $"physicsChains={shotScene.PhysicsChains.Count}, bounds=({avatarBounds.Min} .. {avatarBounds.Max}).");
            }
            else if (shotScene.CacheComponent is { } cacheComponent)
            {
                await cacheComponent.LoadAsync(options.CacheAssetPath, cancellationToken).ConfigureAwait(false);
                GaussianCache cache = cacheComponent.Cache
                    ?? throw new InvalidOperationException("GaussianCacheComponent.LoadAsync completed without a cache instance.");

                avatarBounds = cache.Header.Bounds;
                defaultYawDegrees = cache.Header.CameraHints?.DefaultYawDegrees ?? 0.0f;
                forwardClipDuration = cache.Header.FrameCount > 1 && cache.Header.FrameRate > 0.0f
                    ? (cache.Header.FrameCount - 1) / cache.Header.FrameRate
                    : durationSeconds;
                Console.WriteLine(
                    $"Loaded Gaussian cache: nodes={cache.Header.SplatLevelCounts.Sum()}, levels={cache.Header.SplatLevelCounts.Length}, " +
                    $"frames={cache.Header.FrameCount}, fps={cache.Header.FrameRate}, " +
                    $"bounds=({avatarBounds.Min} .. {avatarBounds.Max}).");
            }
            else
            {
                throw new InvalidOperationException("CutsceneShotScene has neither a live avatar splat component nor a cache component.");
            }

            await RunOnUpdateThreadAsync(() =>
            {
                ConfigureCutsceneFromBounds(
                    shotScene,
                    avatarBounds,
                    defaultYawDegrees,
                    forwardClipDuration,
                    durationSeconds,
                    options.YawOffsetDegrees);
                shotScene.Director.OpenAsync(cancellationToken).GetAwaiter().GetResult();
                if (!shotScene.Director.IsOpen || shotScene.Director.Player is null)
                {
                    string diag = string.Join("; ", shotScene.Director.Diagnostics.Select(static d => d.Message));
                    throw new InvalidOperationException($"CutsceneDirectorComponent failed to open the cutscene: {diag}");
                }

                if (shotScene.Director.Diagnostics.Count > 0)
                {
                    foreach (CutsceneCompileDiagnostic diagnostic in shotScene.Director.Diagnostics)
                        Console.WriteLine($"[Cutscene diagnostic] {diagnostic.Message}");
                }

                SeekAndEvaluateCutscene(shotScene, 0.0, captureFrameIndex: 0);
            }, cancellationToken).ConfigureAwait(false);

            // Wait until the GPU splat buffers and initial radix depth sort finish and warm up pipelines.
            await WaitForWarmupAsync(shotScene, WarmupRenderFrames, cancellationToken).ConfigureAwait(false);

            // Prepare the sequence capture directory so ViewportSequenceCaptureManager writes into a clean folder.
            string sequenceRootDir = Path.Combine(options.OutputDirectory, "sequence");
            if (Directory.Exists(sequenceRootDir))
                Directory.Delete(sequenceRootDir, recursive: true);
            Directory.CreateDirectory(sequenceRootDir);

            if (!ViewportSequenceCaptureOptions.TryCreate(
                    frameCount: totalFrames,
                    durationSeconds: null,
                    frameStride: 1,
                    captureFramesPerSecond: null,
                    maxFrames: totalFrames,
                    outputScale: 1.0,
                    maxInFlightReadbacks: 4,
                    overflowPolicy: "drop",
                    preserveAlpha: false,
                    createContactSheet: true,
                    contactSheetColumns: 6,
                    contactSheetThumbnailWidth: 320,
                    computeFrameDifferences: true,
                    outputDirectory: sequenceRootDir,
                    out ViewportSequenceCaptureOptions? captureOptions,
                    out string? optionsError) ||
                captureOptions is null)
            {
                throw new InvalidOperationException($"Invalid ViewportSequenceCaptureOptions: {optionsError}");
            }

            // Step the cutscene timeline in deterministic lockstep with ViewportSequenceCaptureSession.
            CutsceneLockstepController lockstep = new(viewport, shotScene, totalFrames, options.Fps);
            ViewportSequenceCaptureSession? session = null;

            await RunOnUpdateThreadAsync(() =>
            {
                lockstep.Attach();
                if (!ViewportSequenceCaptureManager.Instance.TryStart(
                        viewport,
                        captureOptions,
                        out session,
                        out string? startError) ||
                    session is null)
                {
                    lockstep.Detach();
                    throw new InvalidOperationException($"Failed to start ViewportSequenceCaptureSession: {startError}");
                }

                session.ShouldCaptureFrame = lockstep.ShouldCaptureFrame;
                session.FrameScheduled = lockstep.OnFrameScheduled;
            }, cancellationToken).ConfigureAwait(false);

            Stopwatch captureWatch = Stopwatch.StartNew();
            try
            {
                while (!session!.IsTerminal)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (captureWatch.Elapsed > TimeSpan.FromMinutes(15))
                    {
                        ViewportSequenceCaptureSnapshot timeoutSnapshot = session.CreateSnapshot(includeFrames: false);
                        throw new TimeoutException(
                            $"Timed out waiting for ViewportSequenceCapture (scheduled={timeoutSnapshot.ScheduledFrameCount}, " +
                            $"completed={timeoutSnapshot.CompletedFrameCount}).");
                    }

                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await RunOnUpdateThreadAsync(lockstep.Detach, CancellationToken.None).ConfigureAwait(false);
            }

            ViewportSequenceCaptureSnapshot snapshot = session.CreateSnapshot(includeFrames: true);
            Console.WriteLine(
                $"ViewportSequenceCapture finished: state={snapshot.State}, stopReason={snapshot.StopReason}, " +
                $"scheduled={snapshot.ScheduledFrameCount}, completed={snapshot.CompletedFrameCount}, " +
                $"failed={snapshot.FailedFrameCount}, outputDir='{snapshot.OutputDirectory}'.");

            if (!string.Equals(snapshot.State, "completed", StringComparison.OrdinalIgnoreCase) ||
                snapshot.CompletedFrameCount != totalFrames)
            {
                throw new InvalidOperationException(
                    $"ViewportSequenceCapture did not complete all {totalFrames} frames " +
                    $"(state={snapshot.State}, completed={snapshot.CompletedFrameCount}, error={snapshot.Error}).");
            }

            // Copy the manifest, contact sheet, and final hero frame to top-level output paths.
            string manifestDestPath = Path.Combine(options.OutputDirectory, "manifest.json");
            File.Copy(snapshot.ManifestPath, manifestDestPath, overwrite: true);

            if (!string.IsNullOrWhiteSpace(snapshot.ContactSheetPath) && File.Exists(snapshot.ContactSheetPath))
            {
                string contactSheetDestPath = Path.Combine(options.OutputDirectory, "contact-sheet.png");
                File.Copy(snapshot.ContactSheetPath, contactSheetDestPath, overwrite: true);
            }

            string heroFrameSourcePath = Path.Combine(snapshot.OutputDirectory, $"frame_{totalFrames - 1:D6}.png");
            string heroPosterPath = Path.Combine(options.OutputDirectory, "hero-poster-1080.png");
            File.Copy(heroFrameSourcePath, heroPosterPath, overwrite: true);

            string masterVideoPath = Path.Combine(options.OutputDirectory, "cutscene-1080.mp4");
            await EncodeMp4WithFfmpegAsync(options.FfmpegPath, snapshot.OutputDirectory, options.Fps, masterVideoPath, cancellationToken).ConfigureAwait(false);

            string remoteVideoPath = Path.Combine(options.OutputDirectory, "cutscene-1080-remote.mp4");
            await EncodeRemoteSafeMp4WithFfmpegAsync(
                options.FfmpegPath,
                snapshot.OutputDirectory,
                options.Fps,
                durationSeconds,
                remoteVideoPath,
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Hero poster written to: {heroPosterPath}");
            Console.WriteLine($"Cutscene video written to: {masterVideoPath}");
            Console.WriteLine($"Remote-safe cutscene video written to: {remoteVideoPath} ({new FileInfo(remoteVideoPath).Length} bytes)");
            return 0;
        }
        finally
        {
            if (shotScene.MotionChoice is { } motionChoice)
            {
                await RunOnUpdateThreadAsync(
                    () => BakeMotionSelector.ReleaseTemporaryPlayers(motionChoice),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static CutsceneShotScene BuildCutsceneShotScene(
        XRWindow window,
        XRViewport viewport,
        FontGlyphSet font,
        CutsceneCommandOptions options,
        float durationSeconds)
    {
        SceneNode stageNode = GaussianPreviewStage.CreateDefault();
        if (stageNode.Transform is Transform stageTransform)
            stageTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

        SceneNode avatarNode = new("AvatarSplatNode");
        avatarNode.SetTransform<Transform>();

        GaussianCacheComponent? cacheComponent = null;
        GaussianAvatarSplatComponent? avatarSplatComponent = null;
        SceneNode? avatarPrefabRoot = null;
        BakeMotionChoice? motionChoice = null;
        List<PhysicsChainComponent> physicsChains = [];
        AABB liveAvatarBounds = default;

        if (options.UseLiveAvatarSplats)
        {
            CookedBakeInput input = CookedBakeInputLoader.Load(options.PakPath, options.AssetPath, GaussianBakeKind.Avatar);
            avatarPrefabRoot = input.PrefabRoot
                ?? throw new InvalidOperationException($"The cooked asset '{options.AssetPath}' has no prefab root node.");

            EnsureAvatarRuntimeMeshes(avatarPrefabRoot);

            List<string> motionWarnings = [];
            motionChoice = BakeMotionSelector.ChooseForPrefab(
                avatarPrefabRoot,
                options.ClipName,
                GaussianBakeBudget.For(GaussianBakeKind.Avatar),
                motionWarnings);
            if (motionWarnings.Count > 0)
                Console.WriteLine($"[Avatar motion warnings] {string.Join(", ", motionWarnings)}");

            liveAvatarBounds = ComputeAvatarMotionBounds(input, avatarPrefabRoot, motionChoice);

            // Sample dense surface splats and intern all NativeUber / Standard submesh materials in rest pose.
            avatarSplatComponent = avatarNode.AddComponent<GaussianAvatarSplatComponent>("AvatarLiveUberSplats")
                ?? throw new InvalidOperationException("Failed to add GaussianAvatarSplatComponent.");
            avatarSplatComponent.InitializeFromAvatar(avatarPrefabRoot, options.SplatCount);

            ConfigureAvatarAnimationAndPhysicsChains(avatarPrefabRoot, motionChoice, physicsChains);
            HideAvatarTriangleMeshes(avatarPrefabRoot);
        }
        else
        {
            cacheComponent = avatarNode.AddComponent<GaussianCacheComponent>("AvatarGaussianCache")
                ?? throw new InvalidOperationException("Failed to add GaussianCacheComponent.");
            cacheComponent.Playing = false;
            cacheComponent.LodThresholdPixels = 0.01f;
            cacheComponent.MaxRenderedSplats = 2_000_000;
        }

        // Multi-shot Cutscene CameraComponent nodes routed through CameraCutTrack jump cuts.
        SceneNode cameraGroupNode = new("CutsceneCameras");
        cameraGroupNode.SetTransform<Transform>();
        ShotCameraRig[] shotCameras = new ShotCameraRig[ShotCameraCount];
        for (int i = 0; i < ShotCameraCount; i++)
        {
            string slotName = $"camera{i}";
            SceneNode cameraNode = new(cameraGroupNode) { Name = $"CutsceneShotCamera_{i}" };
            Transform cameraTransform = cameraNode.SetTransform<Transform>();
            CameraComponent cameraComponent = cameraNode.AddComponent<CameraComponent>($"CutsceneCameraComponent_{i}")
                ?? throw new InvalidOperationException($"Failed to add CameraComponent {i}.");
            cameraComponent.CameraParameters = new XRPerspectiveCameraParameters(34.0f, ViewportAspectRatio, 0.03f, 100.0f);
            cameraComponent.InternalResolutionMode = EInternalResolutionMode.FullResolution;
            shotCameras[i] = new ShotCameraRig(slotName, cameraNode, cameraTransform, cameraComponent);
        }

        // Screen-space UICanvasComponent with Title and Subtitle UITextComponents.
        SceneNode canvasRoot = new("CutsceneOverlayCanvas");
        UICanvasComponent canvasComponent = canvasRoot.AddComponent<UICanvasComponent>("CutsceneUICanvas")
            ?? throw new InvalidOperationException("Failed to add UICanvasComponent.");
        UICanvasTransform canvasTransform = canvasComponent.CanvasTransform;
        canvasTransform.DrawSpace = ECanvasDrawSpace.Screen;
        canvasTransform.SetSize(new Vector2(FrameWidth, FrameHeight));
        canvasTransform.Padding = Vector4.Zero;

        SceneNode titleNode = CreateTextNode(
            canvasRoot,
            "TitleTextNode",
            font,
            options.TitleText,
            fontSize: 72.0f,
            position: new Vector2(96.0f, 300.0f),
            size: new Vector2(920.0f, 110.0f),
            fillColor: new ColorF4(0.97f, 0.98f, 1.0f, 0.0f),
            outlineColor: new ColorF4(0.04f, 0.09f, 0.18f, 0.0f),
            outlineThickness: 0.0f,
            out UITextComponent titleComponent);

        SceneNode subtitleNode = CreateTextNode(
            canvasRoot,
            "SubtitleTextNode",
            font,
            options.SubtitleText,
            fontSize: 28.0f,
            position: new Vector2(100.0f, 232.0f),
            size: new Vector2(920.0f, 64.0f),
            fillColor: new ColorF4(0.66f, 0.86f, 1.0f, 0.0f),
            outlineColor: new ColorF4(0.03f, 0.06f, 0.14f, 0.0f),
            outlineThickness: 0.0f,
            out UITextComponent subtitleComponent);

        // CutsceneDirectorComponent node.
        SceneNode directorNode = new("CutsceneDirector");
        directorNode.SetTransform<Transform>();
        CutsceneDirectorComponent director = directorNode.AddComponent<CutsceneDirectorComponent>("CutsceneDirector")
            ?? throw new InvalidOperationException("Failed to add CutsceneDirectorComponent.");
        director.ResidencyEnabled = false;
        director.TargetViewport = viewport;

        XRScene scene = avatarPrefabRoot is not null
            ? new("SplatCutsceneScene", stageNode, avatarPrefabRoot, avatarNode, cameraGroupNode, canvasRoot, directorNode)
            : new("SplatCutsceneScene", stageNode, avatarNode, cameraGroupNode, canvasRoot, directorNode);
        XRWorld world = new("SplatCutsceneWorld", scene);
        world.Settings.AmbientLightColor = new ColorF3(1.0f, 1.0f, 1.0f);
        world.Settings.AmbientLightIntensity = GaussianPreviewStage.AmbientIntensity;

        director.BindingResolver = new WorldCutsceneBindingResolver(world);
        for (int i = 0; i < shotCameras.Length; i++)
            director.SetRuntimeSlot(shotCameras[i].SlotName, shotCameras[i].Node);
        director.SetRuntimeSlot("avatar", avatarNode);
        director.SetRuntimeSlot("title", titleNode);
        director.SetRuntimeSlot("subtitle", subtitleNode);

        // Attach the world to the window and bind the UI canvas to each shot camera and the viewport.
        RuntimeWorld runtimeWorld = Engine.GetOrCreateWorld(world);
        Task beginPlay = RuntimeWorldHostServices.Current?.BeginPlayAsync(runtimeWorld) ?? runtimeWorld.BeginPlayAsync();
        beginPlay.GetAwaiter().GetResult();

        if (avatarPrefabRoot is not null)
        {
            // Keep source ModelComponents active for GPU compute skinning while rejecting triangle draw collection,
            // and pause automatic world play so lockstep capture advances animation and physics chains deterministically.
            HideAvatarTriangleMeshes(avatarPrefabRoot);
            runtimeWorld.PausePlay();
        }

        IRuntimeRenderWorld renderWorld = Engine.GetOrCreateRenderWorld(world);
        window.SetWorld(renderWorld);

        RenderPipeline renderPipeline = RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline();
        for (int i = 0; i < shotCameras.Length; i++)
        {
            CameraComponent shotCameraComponent = shotCameras[i].Component;
            XRCamera camera = shotCameraComponent.Camera;
            camera.RenderPipeline = renderPipeline;
            camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
            camera.MsaaSampleCountOverride = 1u;
            camera.TsrRenderScaleOverride = 1.0f;
            shotCameraComponent.UserInterface = canvasComponent;
            shotCameraComponent.SetManualExposure(1.0f);
            shotCameraComponent.SetBloomEnabled(false);
        }

        viewport.SetRenderPipelineFromCamera = false;
        viewport.AllowAutomaticInternalResolution = false;
        viewport.RenderPipeline = renderPipeline;
        viewport.SetInternalResolution(FrameWidth, FrameHeight, true);
        viewport.AllowUIRender = true;
        viewport.ScreenSpaceUserInterfaceOverride = canvasComponent;
        viewport.CameraComponent = shotCameras[0].Component;

        canvasTransform.UpdateLayout();
        canvasTransform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential);

        return new CutsceneShotScene(
            world,
            runtimeWorld,
            avatarNode,
            avatarPrefabRoot,
            cacheComponent,
            avatarSplatComponent,
            motionChoice,
            physicsChains,
            liveAvatarBounds,
            cameraGroupNode,
            shotCameras,
            canvasRoot,
            canvasComponent,
            titleNode,
            titleComponent,
            subtitleNode,
            subtitleComponent,
            director,
            durationSeconds,
            options.YawOffsetDegrees);
    }

    private static void EnsureAvatarRuntimeMeshes(SceneNode avatarPrefabRoot)
    {
        avatarPrefabRoot.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        avatarPrefabRoot.IterateComponents<RenderableComponent>(component =>
        {
            if (!BakeMotionSelector.IsEnabled(component, avatarPrefabRoot))
                return;

            if (component is ModelComponent { Meshes.Count: 0 } modelComponent)
                modelComponent.RebuildRuntimeMeshes();
        }, iterateChildHierarchy: true);
    }

    private static AABB ComputeAvatarMotionBounds(
        CookedBakeInput input,
        SceneNode avatarPrefabRoot,
        BakeMotionChoice motionChoice)
    {
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        bool anyVertex = false;

        if (motionChoice.FrameCount > 0)
        {
            BakeFrameSampler.ForEachFrame(avatarPrefabRoot, motionChoice, (_, surface) =>
            {
                float[] positions = surface.Positions;
                for (int offset = 0; offset + 2 < positions.Length; offset += 3)
                {
                    Vector3 p = new(positions[offset], positions[offset + 1], positions[offset + 2]);
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                        continue;

                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    anyVertex = true;
                }
            });
        }

        if (anyVertex)
            return new AABB(min, max);

        if (BakeWorldFactory.TryComputeContentBounds(input, out AABB contentBounds) && contentBounds.IsValid)
            return contentBounds;

        return new AABB(new Vector3(-0.5f, 0.0f, -0.5f), new Vector3(0.5f, 1.8f, 0.5f));
    }

    private static void ConfigureAvatarAnimationAndPhysicsChains(
        SceneNode avatarPrefabRoot,
        BakeMotionChoice motionChoice,
        List<PhysicsChainComponent> physicsChains)
    {
        IReadOnlyList<AnimationClipComponent> selectedPlayers = motionChoice.Players;
        for (int i = 0; i < selectedPlayers.Count; i++)
            selectedPlayers[i].StartOnActivate = false;

        avatarPrefabRoot.IterateComponents(component =>
        {
            if (component is AnimStateMachineComponent)
            {
                component.IsActive = false;
                return;
            }

            if (component is AnimationClipComponent clipComponent && !ContainsPlayer(selectedPlayers, clipComponent))
            {
                clipComponent.StartOnActivate = false;
                clipComponent.IsActive = false;
                return;
            }

            if (component is PhysicsChainComponent chain && BakeMotionSelector.IsEnabled(chain, avatarPrefabRoot))
            {
                chain.UseGPU = false;
                chain.Multithread = false;
                chain.InterpolationMode = PhysicsChainComponent.EInterpolationMode.Discrete;
                chain.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
                chain.QualityTier = PhysicsChainQualityTier.Strict;
                chain.TransformMirrorPolicy = PhysicsChainOutputControl.EverySimulationStep;
                chain.OffscreenBehavior = PhysicsChainOffscreenBehavior.Simulate;
                chain.EnableAutomaticSleep = false;
                chain.DistantDisable = false;
                chain.SetRuntimeVisibility(true);
                physicsChains.Add(chain);
            }
        }, iterateChildHierarchy: true);
    }

    private static bool ContainsPlayer(IReadOnlyList<AnimationClipComponent> players, AnimationClipComponent candidate)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (ReferenceEquals(players[i], candidate))
                return true;
        }

        return false;
    }

    private static void HideAvatarTriangleMeshes(SceneNode avatarPrefabRoot)
    {
        avatarPrefabRoot.IterateComponents<RenderableComponent>(component =>
        {
            component.MeshCastsShadows = false;
            foreach (RenderableMesh mesh in component.Meshes)
                mesh.RenderInfo.SetCameraIndependentPreCollectCommandsCallback(RejectAllRenderPassesCallback);
        }, iterateChildHierarchy: true);
    }

    private static SceneNode CreateTextNode(
        SceneNode canvasRoot,
        string name,
        FontGlyphSet font,
        string text,
        float fontSize,
        Vector2 position,
        Vector2 size,
        ColorF4 fillColor,
        ColorF4 outlineColor,
        float outlineThickness,
        out UITextComponent textComponent)
    {
        SceneNode node = new(canvasRoot) { Name = name };
        UIBoundableTransform transform = node.SetTransform<UIBoundableTransform>();
        transform.MinAnchor = Vector2.Zero;
        transform.MaxAnchor = Vector2.Zero;
        transform.NormalizedPivot = Vector2.Zero;
        transform.Width = size.X;
        transform.Height = size.Y;
        transform.Translation = position;

        textComponent = node.AddComponent<UITextComponent>(name)
            ?? throw new InvalidOperationException($"Failed to add UITextComponent '{name}'.");
        textComponent.Font = font;
        textComponent.FontSize = fontSize;
        textComponent.HorizontalAlignment = EHorizontalAlignment.Left;
        textComponent.VerticalAlignment = EVerticalAlignment.Center;
        textComponent.WrapMode = FontGlyphSet.EWrapMode.None;
        textComponent.Color = fillColor;
        textComponent.OutlineColor = outlineColor;
        textComponent.OutlineThickness = outlineThickness;
        textComponent.Text = text;
        return node;
    }

    private static void ConfigureCutsceneFromBounds(
        CutsceneShotScene shotScene,
        AABB bounds,
        float defaultYawDegrees,
        float forwardClipDuration,
        float durationSeconds,
        float yawOffsetDegrees)
    {
        Vector3 boundsMin = bounds.Min;
        Vector3 boundsMax = bounds.Max;
        Vector3 boundsSize = boundsMax - boundsMin;
        float height = MathF.Max(boundsSize.Y, 0.4f);
        float centerX = (boundsMin.X + boundsMax.X) * 0.5f;
        float centerZ = (boundsMin.Z + boundsMax.Z) * 0.5f;

        float nearZ = Math.Clamp(height * 0.02f, 0.015f, 0.15f);
        float farZ = MathF.Max(height * 25.0f, 50.0f);
        for (int i = 0; i < shotScene.ShotCameras.Length; i++)
        {
            shotScene.ShotCameras[i].Component.CameraParameters = new XRPerspectiveCameraParameters(
                34.0f,
                ViewportAspectRatio,
                nearZ,
                farZ);
        }

        float frontYawDegrees = defaultYawDegrees + 180.0f + yawOffsetDegrees;
        float frontYawRadians = frontYawDegrees * (MathF.PI / 180.0f);
        Vector3 avatarForward = new(MathF.Sin(frontYawRadians), 0.0f, MathF.Cos(frontYawRadians));
        CutsceneTime fullDuration = CutsceneTime.FromSeconds(durationSeconds);

        // Five jump-cut shots moving across the walking avatar's body from boots/legs up to face/ears,
        // ending on a 3.7s full-body hero push-in shot with unobstructed left-side UI reveal.
        ShotCameraDefinition[] shotDefinitions =
        [
            // Shot 0 (0%..14.2%): Low-angle boots, stockings, and striding thighs.
            new(
                StartFraction: 0.000f,
                EndFraction: 0.142f,
                StartYawDegrees: frontYawDegrees - 28.0f,
                EndYawDegrees: frontYawDegrees - 14.0f,
                StartPitchDegrees: -5.5f,
                EndPitchDegrees: -2.0f,
                StartFocusHeightFraction: 0.22f,
                EndFocusHeightFraction: 0.31f,
                ForwardFocusBias: 0.22f,
                StartDistanceFactor: 0.78f,
                EndDistanceFactor: 0.70f,
                StartFovDegrees: 30.0f,
                EndFovDegrees: 28.0f,
                StartHorizontalLeadFactor: 0.0f,
                EndHorizontalLeadFactor: 0.0f,
                CacheStartFraction: 0.02f,
                CacheEndFraction: 0.41f),
            // Shot 1 (14.2%..29.0%): Jump cut to 3/4 rear-side swaying hips, shorts, and snow-leopard tail.
            new(
                StartFraction: 0.142f,
                EndFraction: 0.290f,
                StartYawDegrees: frontYawDegrees - 122.0f,
                EndYawDegrees: frontYawDegrees - 98.0f,
                StartPitchDegrees: 5.0f,
                EndPitchDegrees: 2.5f,
                StartFocusHeightFraction: 0.44f,
                EndFocusHeightFraction: 0.50f,
                ForwardFocusBias: 0.05f,
                StartDistanceFactor: 0.88f,
                EndDistanceFactor: 0.80f,
                StartFovDegrees: 32.0f,
                EndFovDegrees: 30.0f,
                StartHorizontalLeadFactor: 0.0f,
                EndHorizontalLeadFactor: 0.0f,
                CacheStartFraction: 0.41f,
                CacheEndFraction: 0.82f),
            // Shot 2 (29.0%..43.8%): Jump cut to opposing 3/4 front-left mid-torso, outfit, and swinging arms.
            new(
                StartFraction: 0.290f,
                EndFraction: 0.438f,
                StartYawDegrees: frontYawDegrees + 40.0f,
                EndYawDegrees: frontYawDegrees + 20.0f,
                StartPitchDegrees: 4.0f,
                EndPitchDegrees: 2.0f,
                StartFocusHeightFraction: 0.56f,
                EndFocusHeightFraction: 0.66f,
                ForwardFocusBias: 0.22f,
                StartDistanceFactor: 0.72f,
                EndDistanceFactor: 0.64f,
                StartFovDegrees: 29.0f,
                EndFovDegrees: 27.0f,
                StartHorizontalLeadFactor: 0.0f,
                EndHorizontalLeadFactor: 0.0f,
                CacheStartFraction: 0.08f,
                CacheEndFraction: 0.49f),
            // Shot 3 (43.8%..58.0%): Jump cut to close-up portrait of face, hair, and cat ears.
            new(
                StartFraction: 0.438f,
                EndFraction: 0.580f,
                StartYawDegrees: frontYawDegrees - 22.0f,
                EndYawDegrees: frontYawDegrees - 8.0f,
                StartPitchDegrees: 4.5f,
                EndPitchDegrees: 2.0f,
                StartFocusHeightFraction: 0.87f,
                EndFocusHeightFraction: 0.89f,
                ForwardFocusBias: 0.22f,
                StartDistanceFactor: 0.64f,
                EndDistanceFactor: 0.56f,
                StartFovDegrees: 27.0f,
                EndFovDegrees: 25.0f,
                StartHorizontalLeadFactor: 0.0f,
                EndHorizontalLeadFactor: 0.0f,
                CacheStartFraction: 0.49f,
                CacheEndFraction: 0.88f),
            // Shot 4 (58.0%..100%): Jump cut to eased orbit-settle + push-in hero shot with left-side UI reveal.
            new(
                StartFraction: 0.580f,
                EndFraction: 1.000f,
                StartYawDegrees: frontYawDegrees + 32.0f,
                EndYawDegrees: frontYawDegrees + 15.0f,
                StartPitchDegrees: 6.5f,
                EndPitchDegrees: 3.0f,
                StartFocusHeightFraction: 0.51f,
                EndFocusHeightFraction: 0.52f,
                ForwardFocusBias: 0.16f,
                StartDistanceFactor: 2.02f,
                EndDistanceFactor: 1.72f,
                StartFovDegrees: 38.0f,
                EndFovDegrees: 35.0f,
                StartHorizontalLeadFactor: 0.30f,
                EndHorizontalLeadFactor: 0.34f,
                CacheStartFraction: 0.02f,
                CacheEndFraction: 0.96f),
        ];

        List<CutsceneBindingReference> bindings = new(ShotCameraCount + 3);
        for (int i = 0; i < shotScene.ShotCameras.Length; i++)
        {
            bindings.Add(new CutsceneBindingReference
            {
                Kind = ECutsceneBindingKind.RuntimeSlot,
                SlotName = shotScene.ShotCameras[i].SlotName,
            });
        }

        int avatarBindingSlot = bindings.Count;
        bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "avatar" });
        int titleBindingSlot = bindings.Count;
        bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "title" });
        int subtitleBindingSlot = bindings.Count;
        bindings.Add(new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "subtitle" });

        List<CutsceneClip> cameraCutClips = new(shotDefinitions.Length);
        List<CutsceneClip> avatarPlaybackClips = new(shotDefinitions.Length);
        List<CutsceneTrack> tracks = new(1 + (shotDefinitions.Length * 2) + 9);

        CameraCutTrack cameraCutTrack = new()
        {
            Name = "CameraJumpCuts",
            BindingSlot = 0,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
        };
        tracks.Add(cameraCutTrack);

        for (int shotIndex = 0; shotIndex < shotDefinitions.Length; shotIndex++)
        {
            ShotCameraDefinition shot = shotDefinitions[shotIndex];
            CutsceneTime shotStart = shotIndex == 0
                ? CutsceneTime.Zero
                : CutsceneTime.FromSeconds(durationSeconds * shot.StartFraction);
            CutsceneTime shotEnd = shotIndex == shotDefinitions.Length - 1
                ? fullDuration
                : CutsceneTime.FromSeconds(durationSeconds * shot.EndFraction);
            CutsceneTime shotDuration = new(Math.Max(1L, shotEnd.Flicks - shotStart.Flicks));
            bool isFinalShot = shotIndex == shotDefinitions.Length - 1;

            cameraCutClips.Add(new CameraCutClip
            {
                Start = shotStart,
                Duration = shotDuration,
                CameraBindingSlot = shotIndex,
                Extrapolation = isFinalShot ? ECutsceneClipExtrapolation.Hold : ECutsceneClipExtrapolation.None,
            });

            CutsceneTime nonOverlappingDuration = isFinalShot
                ? shotDuration
                : new CutsceneTime(Math.Max(1L, shotDuration.Flicks - 1L));
            avatarPlaybackClips.Add(new PropertyTrackClip
            {
                Start = shotStart,
                Duration = nonOverlappingDuration,
                Extrapolation = isFinalShot ? ECutsceneClipExtrapolation.Hold : ECutsceneClipExtrapolation.None,
                FloatCurve = new CutsceneFloatCurve
                {
                    Interpolation = ECutsceneCurveInterpolation.Linear,
                    Times = [CutsceneTime.Zero, nonOverlappingDuration],
                    Values =
                    [
                        forwardClipDuration * shot.CacheStartFraction,
                        forwardClipDuration * shot.CacheEndFraction,
                    ],
                },
            });

            BuildShotCameraTracks(
                shotIndex,
                shotScene.ShotCameras[shotIndex],
                shot,
                shotStart,
                shotDuration,
                isFinalShot,
                centerX,
                centerZ,
                avatarForward,
                boundsMin.Y,
                height,
                out TransformTrack shotTransformTrack,
                out CameraPropertyTrack shotFovTrack);
            tracks.Add(shotTransformTrack);
            tracks.Add(shotFovTrack);
        }

        cameraCutTrack.Clips = cameraCutClips;

        float heroCutSeconds = durationSeconds * shotDefinitions[^1].StartFraction;
        float titleRevealStart = heroCutSeconds + 0.04f;
        float titleRevealEnd = Math.Min(heroCutSeconds + 0.52f, durationSeconds - 3.15f);
        if (titleRevealEnd <= titleRevealStart + 0.2f)
        {
            titleRevealStart = Math.Max(heroCutSeconds + 0.02f, durationSeconds - 3.65f);
            float maxRevealDuration = Math.Max(0.08f, (durationSeconds - titleRevealStart) * 0.35f);
            titleRevealEnd = Math.Min(
                durationSeconds - 0.04f,
                Math.Max(titleRevealStart + Math.Min(0.42f, maxRevealDuration), durationSeconds - 3.15f));
        }

        float subtitleRevealStart = Math.Min(titleRevealStart + 0.14f, Math.Max(titleRevealStart + 0.02f, durationSeconds - 0.12f));
        float subtitleRevealEnd = Math.Min(titleRevealEnd + 0.18f, Math.Max(subtitleRevealStart + 0.04f, durationSeconds - 0.02f));

        string avatarPlaybackComponentType = shotScene.AvatarSplatComponent is not null
            ? nameof(GaussianAvatarSplatComponent)
            : nameof(GaussianCacheComponent);
        string avatarPlaybackMemberPath = shotScene.AvatarSplatComponent is not null
            ? nameof(GaussianAvatarSplatComponent.PlaybackTimeSeconds)
            : nameof(GaussianCacheComponent.PlaybackTimeSeconds);

        tracks.Add(new PropertyTrack
        {
            Name = "AvatarPlaybackTime",
            BindingSlot = avatarBindingSlot,
            ComponentType = avatarPlaybackComponentType,
            MemberPath = avatarPlaybackMemberPath,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips = avatarPlaybackClips,
        });

        // Title and Subtitle in-engine native UI tracks: hidden during close-up body cuts, revealed on the hero shot
        // and held at full readability for over 3.2 seconds through the final hero poster frame.
        tracks.Add(BuildUiSlideTransformTrack(
            name: "TitleTransform",
            bindingSlot: titleBindingSlot,
            fullDuration: fullDuration,
            revealStartSeconds: titleRevealStart,
            revealEndSeconds: titleRevealEnd,
            startPosition: new Vector3(28.0f, 300.0f, 0.0f),
            endPosition: new Vector3(96.0f, 300.0f, 0.0f),
            startScale: new Vector3(0.92f, 0.92f, 1.0f),
            endScale: Vector3.One));
        tracks.Add(BuildUiFadePropertyTrack(
            name: "TitleOpacity",
            bindingSlot: titleBindingSlot,
            memberPath: nameof(UITextComponent.Opacity),
            fullDuration: fullDuration,
            revealStartSeconds: titleRevealStart,
            revealEndSeconds: titleRevealEnd,
            targetValue: 1.0f));
        tracks.Add(BuildUiFadePropertyTrack(
            name: "TitleOutlineOpacity",
            bindingSlot: titleBindingSlot,
            memberPath: nameof(UITextComponent.OutlineOpacity),
            fullDuration: fullDuration,
            revealStartSeconds: titleRevealStart,
            revealEndSeconds: titleRevealEnd,
            targetValue: 0.95f));
        tracks.Add(BuildUiOutlinePulseTrack(
            name: "TitleOutlineThickness",
            bindingSlot: titleBindingSlot,
            fullDuration: fullDuration,
            revealStartSeconds: titleRevealStart,
            peakSeconds: Lerp(titleRevealStart, titleRevealEnd, 0.55f),
            settleSeconds: titleRevealEnd + 0.15f,
            peakThickness: 4.2f,
            settledThickness: 2.6f));

        tracks.Add(BuildUiSlideTransformTrack(
            name: "SubtitleTransform",
            bindingSlot: subtitleBindingSlot,
            fullDuration: fullDuration,
            revealStartSeconds: subtitleRevealStart,
            revealEndSeconds: subtitleRevealEnd,
            startPosition: new Vector3(48.0f, 232.0f, 0.0f),
            endPosition: new Vector3(100.0f, 232.0f, 0.0f),
            startScale: new Vector3(0.95f, 0.95f, 1.0f),
            endScale: Vector3.One));
        tracks.Add(BuildUiFadePropertyTrack(
            name: "SubtitleOpacity",
            bindingSlot: subtitleBindingSlot,
            memberPath: nameof(UITextComponent.Opacity),
            fullDuration: fullDuration,
            revealStartSeconds: subtitleRevealStart,
            revealEndSeconds: subtitleRevealEnd,
            targetValue: 1.0f));
        tracks.Add(BuildUiFadePropertyTrack(
            name: "SubtitleOutlineOpacity",
            bindingSlot: subtitleBindingSlot,
            memberPath: nameof(UITextComponent.OutlineOpacity),
            fullDuration: fullDuration,
            revealStartSeconds: subtitleRevealStart,
            revealEndSeconds: subtitleRevealEnd,
            targetValue: 0.90f));
        tracks.Add(BuildUiOutlinePulseTrack(
            name: "SubtitleOutlineThickness",
            bindingSlot: subtitleBindingSlot,
            fullDuration: fullDuration,
            revealStartSeconds: subtitleRevealStart,
            peakSeconds: Lerp(subtitleRevealStart, subtitleRevealEnd, 0.55f),
            settleSeconds: subtitleRevealEnd + 0.15f,
            peakThickness: 3.0f,
            settledThickness: 1.8f));

        shotScene.Director.Asset = new CutsceneAsset
        {
            Duration = fullDuration,
            Bindings = [.. bindings],
            Tracks = [.. tracks],
        };
    }

    private static void BuildShotCameraTracks(
        int shotIndex,
        ShotCameraRig shotCameraRig,
        ShotCameraDefinition shot,
        CutsceneTime shotStart,
        CutsceneTime shotDuration,
        bool isFinalShot,
        float centerX,
        float centerZ,
        Vector3 avatarForward,
        float minY,
        float height,
        out TransformTrack transformTrack,
        out CameraPropertyTrack fovTrack)
    {
        double shotDurationSeconds = shotDuration.ToSeconds();
        int sampleCount = Math.Max(32, (int)MathF.Ceiling((float)shotDurationSeconds * 60.0f) + 1);
        CutsceneTime[] sampleTimes = new CutsceneTime[sampleCount];
        Vector3[] positions = new Vector3[sampleCount];
        Quaternion[] rotations = new Quaternion[sampleCount];
        float[] fovs = new float[sampleCount];
        Vector3 forwardBiasOffset = avatarForward * (height * shot.ForwardFocusBias);

        for (int i = 0; i < sampleCount; i++)
        {
            double normalized = i / (double)(sampleCount - 1);
            sampleTimes[i] = CutsceneTime.FromSeconds(normalized * shotDurationSeconds);

            float eased = isFinalShot
                ? SmoothStep01((float)Math.Clamp(normalized / 0.78, 0.0, 1.0))
                : SmoothStep01((float)normalized);

            float yawDegrees = Lerp(shot.StartYawDegrees, shot.EndYawDegrees, eased);
            float pitchDegrees = Lerp(shot.StartPitchDegrees, shot.EndPitchDegrees, eased);
            float distance = height * Lerp(shot.StartDistanceFactor, shot.EndDistanceFactor, eased);
            float fovDegrees = Lerp(shot.StartFovDegrees, shot.EndFovDegrees, eased);
            float focusY = minY + (height * Lerp(shot.StartFocusHeightFraction, shot.EndFocusHeightFraction, eased));
            float horizontalLead = height * Lerp(shot.StartHorizontalLeadFactor, shot.EndHorizontalLeadFactor, eased);

            float yawRadians = yawDegrees * (MathF.PI / 180.0f);
            float pitchRadians = pitchDegrees * (MathF.PI / 180.0f);
            Quaternion orbitOrientation = Quaternion.Normalize(
                Quaternion.CreateFromYawPitchRoll(yawRadians, -pitchRadians, 0.0f));
            Vector3 cameraForward = Vector3.Transform(-Vector3.UnitZ, orbitOrientation);
            Vector3 cameraRight = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));

            Vector3 focus = new Vector3(centerX, focusY, centerZ) + forwardBiasOffset - (cameraRight * horizontalLead);
            Vector3 offset = Vector3.Transform(new Vector3(0.0f, 0.0f, distance), orbitOrientation);
            Vector3 position = focus + offset;
            Vector3 lookDirection = Vector3.Normalize(focus - position);

            positions[i] = position;
            rotations[i] = LookRotation(lookDirection, Vector3.UnitY);
            fovs[i] = fovDegrees;
        }

        shotCameraRig.Transform.Translation = positions[0];
        shotCameraRig.Transform.Rotation = rotations[0];
        if (shotCameraRig.Component.CameraParameters is XRPerspectiveCameraParameters initialParams)
        {
            shotCameraRig.Component.CameraParameters = new XRPerspectiveCameraParameters(
                fovs[0],
                initialParams.AspectRatio,
                initialParams.NearZ,
                initialParams.FarZ);
        }

        const ECutsceneClipExtrapolation extrapolation = ECutsceneClipExtrapolation.Hold;

        transformTrack = new TransformTrack
        {
            Name = $"ShotCamera{shotIndex}_Transform",
            BindingSlot = shotIndex,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips =
            [
                new TransformTrackClip
                {
                    Start = shotStart,
                    Duration = shotDuration,
                    Extrapolation = extrapolation,
                    Position = new CutsceneVector3Curve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = sampleTimes,
                        Values = positions,
                    },
                    Rotation = new CutsceneQuaternionCurve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = sampleTimes,
                        Values = rotations,
                    },
                },
            ],
        };

        fovTrack = new CameraPropertyTrack
        {
            Name = $"ShotCamera{shotIndex}_Fov",
            BindingSlot = shotIndex,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips =
            [
                new CameraPropertyClip
                {
                    Start = shotStart,
                    Duration = shotDuration,
                    Extrapolation = extrapolation,
                    FieldOfView = new CutsceneFloatCurve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = sampleTimes,
                        Values = fovs,
                    },
                },
            ],
        };
    }

    private static TransformTrack BuildUiSlideTransformTrack(
        string name,
        int bindingSlot,
        CutsceneTime fullDuration,
        float revealStartSeconds,
        float revealEndSeconds,
        Vector3 startPosition,
        Vector3 endPosition,
        Vector3 startScale,
        Vector3 endScale)
    {
        const int keyCount = 32;
        CutsceneTime[] times = new CutsceneTime[keyCount + 2];
        Vector3[] positions = new Vector3[keyCount + 2];
        Vector3[] scales = new Vector3[keyCount + 2];

        times[0] = CutsceneTime.Zero;
        positions[0] = startPosition;
        scales[0] = startScale;

        for (int i = 0; i < keyCount; i++)
        {
            float u = i / (float)(keyCount - 1);
            float eased = CubicEaseOut(u);
            float t = Lerp(revealStartSeconds, revealEndSeconds, u);
            times[i + 1] = CutsceneTime.FromSeconds(t);
            positions[i + 1] = Vector3.Lerp(startPosition, endPosition, eased);
            scales[i + 1] = Vector3.Lerp(startScale, endScale, eased);
        }

        times[keyCount + 1] = fullDuration;
        positions[keyCount + 1] = endPosition;
        scales[keyCount + 1] = endScale;

        return new TransformTrack
        {
            Name = name,
            BindingSlot = bindingSlot,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips =
            [
                new TransformTrackClip
                {
                    Start = CutsceneTime.Zero,
                    Duration = fullDuration,
                    Extrapolation = ECutsceneClipExtrapolation.Hold,
                    Position = new CutsceneVector3Curve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = times,
                        Values = positions,
                    },
                    Rotation = new CutsceneQuaternionCurve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = [CutsceneTime.Zero, fullDuration],
                        Values = [Quaternion.Identity, Quaternion.Identity],
                    },
                    Scale = new CutsceneVector3Curve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = times,
                        Values = scales,
                    },
                },
            ],
        };
    }

    private static PropertyTrack BuildUiFadePropertyTrack(
        string name,
        int bindingSlot,
        string memberPath,
        CutsceneTime fullDuration,
        float revealStartSeconds,
        float revealEndSeconds,
        float targetValue)
    {
        const int keyCount = 24;
        CutsceneTime[] times = new CutsceneTime[keyCount + 2];
        float[] values = new float[keyCount + 2];

        times[0] = CutsceneTime.Zero;
        values[0] = 0.0f;

        for (int i = 0; i < keyCount; i++)
        {
            float u = i / (float)(keyCount - 1);
            float eased = SmoothStep01(u);
            float t = Lerp(revealStartSeconds, revealEndSeconds, u);
            times[i + 1] = CutsceneTime.FromSeconds(t);
            values[i + 1] = targetValue * eased;
        }

        times[keyCount + 1] = fullDuration;
        values[keyCount + 1] = targetValue;

        return new PropertyTrack
        {
            Name = name,
            BindingSlot = bindingSlot,
            ComponentType = nameof(UITextComponent),
            MemberPath = memberPath,
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips =
            [
                new PropertyTrackClip
                {
                    Start = CutsceneTime.Zero,
                    Duration = fullDuration,
                    Extrapolation = ECutsceneClipExtrapolation.Hold,
                    FloatCurve = new CutsceneFloatCurve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times = times,
                        Values = values,
                    },
                },
            ],
        };
    }

    private static PropertyTrack BuildUiOutlinePulseTrack(
        string name,
        int bindingSlot,
        CutsceneTime fullDuration,
        float revealStartSeconds,
        float peakSeconds,
        float settleSeconds,
        float peakThickness,
        float settledThickness)
    {
        return new PropertyTrack
        {
            Name = name,
            BindingSlot = bindingSlot,
            ComponentType = nameof(UITextComponent),
            MemberPath = nameof(UITextComponent.OutlineThickness),
            WhenFinished = ECutsceneFinishBehavior.KeepState,
            Clips =
            [
                new PropertyTrackClip
                {
                    Start = CutsceneTime.Zero,
                    Duration = fullDuration,
                    Extrapolation = ECutsceneClipExtrapolation.Hold,
                    FloatCurve = new CutsceneFloatCurve
                    {
                        Interpolation = ECutsceneCurveInterpolation.Linear,
                        Times =
                        [
                            CutsceneTime.Zero,
                            CutsceneTime.FromSeconds(revealStartSeconds),
                            CutsceneTime.FromSeconds(peakSeconds),
                            CutsceneTime.FromSeconds(settleSeconds),
                            fullDuration,
                        ],
                        Values =
                        [
                            0.0f,
                            0.0f,
                            peakThickness,
                            settledThickness,
                            settledThickness,
                        ],
                    },
                },
            ],
        };
    }

    private static void SeekAndEvaluateCutscene(CutsceneShotScene shotScene, double timeSeconds, int captureFrameIndex)
    {
        CutscenePlayer? player = shotScene.Director.Player;
        if (player is null)
            return;

        player.Seek(CutsceneTime.FromSeconds(timeSeconds), ESeekIntent.Jump);
        shotScene.Director.AdvanceFrame(0.0);

        if (shotScene.AvatarSplatComponent is { } liveSplats && shotScene.AvatarPrefabRoot is not null)
            StepLiveAvatarAndPhysicsChains(shotScene, captureFrameIndex, liveSplats.PlaybackTimeSeconds);

        shotScene.CameraGroupNode.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        shotScene.AvatarNode.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        UICanvasTransform canvasTransform = shotScene.CanvasComponent.CanvasTransform;
        canvasTransform.InvalidateLayout();
        canvasTransform.UpdateLayout();
        canvasTransform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    private static void StepLiveAvatarAndPhysicsChains(
        CutsceneShotScene shotScene,
        int captureFrameIndex,
        float targetAnimTimeSeconds)
    {
        if (shotScene.AvatarPrefabRoot is not { } avatarPrefabRoot)
            return;

        if (shotScene.LastSteppedCaptureFrame == captureFrameIndex)
            return;

        bool needsPreRoll = shotScene.LastSteppedCaptureFrame < 0
            || targetAnimTimeSeconds < shotScene.LastAppliedAnimTimeSeconds - 1e-3f;

        if (needsPreRoll)
        {
            float leadInStart = MathF.Max(0.0f, targetAnimTimeSeconds - PhysicsLeadInWindowSeconds);
            EvaluateAvatarAnimationAtTime(shotScene, leadInStart);

            if (shotScene.PhysicsChains.Count > 0)
            {
                for (int i = 0; i < shotScene.PhysicsChains.Count; i++)
                {
                    PhysicsChainComponent chain = shotScene.PhysicsChains[i];
                    chain.SetRuntimeVisibility(true);
                    chain.ResetSimulationToCurrentPose();
                }

                // Consume the requested pose reset on the late tick, then let gravity settle the chains smoothly.
                shotScene.RuntimeWorld.TickGroup(ETickGroup.Late);
                avatarPrefabRoot.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

                for (int settleStep = 0; settleStep < PhysicsGravitySettleSubsteps; settleStep++)
                    TickPhysicsChainsOneSubstep(shotScene);
            }

            float leadInSpan = targetAnimTimeSeconds - leadInStart;
            int leadInSubsteps = Math.Max(4, (int)MathF.Round(leadInSpan * 60.0f));
            for (int step = 1; step <= leadInSubsteps; step++)
            {
                float u = step / (float)leadInSubsteps;
                EvaluateAvatarAnimationAtTime(shotScene, Lerp(leadInStart, targetAnimTimeSeconds, u));
                TickPhysicsChainsOneSubstep(shotScene);
            }
        }
        else
        {
            float startAnimTime = shotScene.LastAppliedAnimTimeSeconds;
            for (int step = 1; step <= PhysicsSubstepsPerCapturedFrame; step++)
            {
                float u = step / (float)PhysicsSubstepsPerCapturedFrame;
                EvaluateAvatarAnimationAtTime(shotScene, Lerp(startAnimTime, targetAnimTimeSeconds, u));
                TickPhysicsChainsOneSubstep(shotScene);
            }
        }

        shotScene.LastAppliedAnimTimeSeconds = targetAnimTimeSeconds;
        shotScene.LastSteppedCaptureFrame = captureFrameIndex;
    }

    private static void EvaluateAvatarAnimationAtTime(CutsceneShotScene shotScene, float timeSeconds)
    {
        if (shotScene.AvatarPrefabRoot is not { } avatarPrefabRoot)
            return;

        if (shotScene.MotionChoice is { } motionChoice)
        {
            IReadOnlyList<AnimationClipComponent> players = motionChoice.Players;
            for (int i = 0; i < players.Count; i++)
            {
                AnimationClipComponent player = players[i];
                float clipLength = player.Animation?.LengthInSeconds ?? 0.0f;
                float wrappedTime = WrapClipTime(timeSeconds, clipLength);
                player.EvaluateAtTime(wrappedTime, dispatchEvents: false);
            }
        }

        avatarPrefabRoot.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    private static void TickPhysicsChainsOneSubstep(CutsceneShotScene shotScene)
    {
        if (shotScene.PhysicsChains.Count == 0 || shotScene.AvatarPrefabRoot is not { } avatarPrefabRoot)
            return;

        for (int i = 0; i < shotScene.PhysicsChains.Count; i++)
            shotScene.PhysicsChains[i].SetRuntimeVisibility(true);

        shotScene.RuntimeWorld.TickGroup(ETickGroup.PostPhysics);
        shotScene.RuntimeWorld.TickGroup(ETickGroup.Late);
        avatarPrefabRoot.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    private static float WrapClipTime(float timeSeconds, float lengthSeconds)
    {
        if (!(lengthSeconds > 0.0f) || timeSeconds <= 0.0f)
            return 0.0f;
        if (timeSeconds < lengthSeconds)
            return timeSeconds;

        double wrapped = timeSeconds - (Math.Floor((double)timeSeconds / lengthSeconds) * lengthSeconds);
        return wrapped >= lengthSeconds ? 0.0f : (float)wrapped;
    }

    private static async Task WaitForWarmupAsync(
        CutsceneShotScene shotScene,
        int warmupFrames,
        CancellationToken cancellationToken)
    {
        ulong startFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        ulong readyStartFrame = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool ready = shotScene.IsGpuDataReady;
            ulong currentFrame = RuntimeEngine.Rendering.State.RenderFrameId;
            if (ready)
            {
                if (readyStartFrame == 0)
                    readyStartFrame = currentFrame;
                else if (currentFrame >= readyStartFrame + (ulong)warmupFrames)
                    return;
            }

            if (currentFrame > startFrame + 1800)
            {
                throw new TimeoutException(
                    $"Timed out waiting for Gaussian splat GPU readiness (IsGpuDataReady={ready}).");
            }

            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task RunOnUpdateThreadAsync(Action action, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.EnqueueUpdateThreadTask(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task.WaitAsync(cancellationToken);
    }

    private static async Task EncodeMp4WithFfmpegAsync(
        string ffmpegPath,
        string frameDirectory,
        int fps,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        string inputPattern = Path.Combine(frameDirectory, "frame_%06d.png");
        if (File.Exists(outputMp4Path))
            File.Delete(outputMp4Path);

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputPattern);
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("slow");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("16");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputMp4Path);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{ffmpegPath}'.");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
    }

    private static async Task EncodeRemoteSafeMp4WithFfmpegAsync(
        string ffmpegPath,
        string frameDirectory,
        int fps,
        float durationSeconds,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        int targetBitrateKbps = Math.Clamp(
            (int)MathF.Floor((RemoteTargetBytes * 8.0f / 1000.0f) / Math.Max(durationSeconds, 1.0f)),
            250,
            4000);

        await RunTwoPassMp4EncodeAsync(
            ffmpegPath,
            frameDirectory,
            fps,
            targetBitrateKbps,
            outputMp4Path,
            cancellationToken).ConfigureAwait(false);

        long encodedBytes = new FileInfo(outputMp4Path).Length;
        if (encodedBytes < RemoteProxyByteLimit)
            return;

        int fallbackBitrateKbps = Math.Max(200, (int)(targetBitrateKbps * 0.72f));
        await RunTwoPassMp4EncodeAsync(
            ffmpegPath,
            frameDirectory,
            fps,
            fallbackBitrateKbps,
            outputMp4Path,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunTwoPassMp4EncodeAsync(
        string ffmpegPath,
        string frameDirectory,
        int fps,
        int targetBitrateKbps,
        string outputMp4Path,
        CancellationToken cancellationToken)
    {
        string inputPattern = Path.Combine(frameDirectory, "frame_%06d.png");
        if (File.Exists(outputMp4Path))
            File.Delete(outputMp4Path);

        string passLogDir = Path.Combine(frameDirectory, "_passlog");
        if (Directory.Exists(passLogDir))
            Directory.Delete(passLogDir, recursive: true);
        Directory.CreateDirectory(passLogDir);
        string passLogPrefix = Path.Combine(passLogDir, "ffmpeg2pass");
        int maxRateKbps = (int)MathF.Ceiling(targetBitrateKbps * 1.12f);
        int bufSizeKbps = targetBitrateKbps * 2;
        string nullSink = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        try
        {
            await RunFfmpegArgumentsAsync(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                    "-i", inputPattern,
                    "-c:v", "libx264",
                    "-preset", "slow",
                    "-b:v", $"{targetBitrateKbps}k",
                    "-maxrate", $"{maxRateKbps}k",
                    "-bufsize", $"{bufSizeKbps}k",
                    "-pix_fmt", "yuv420p",
                    "-pass", "1",
                    "-passlogfile", passLogPrefix,
                    "-f", "mp4",
                    nullSink,
                ],
                cancellationToken).ConfigureAwait(false);

            await RunFfmpegArgumentsAsync(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-framerate", fps.ToString(CultureInfo.InvariantCulture),
                    "-i", inputPattern,
                    "-c:v", "libx264",
                    "-preset", "slow",
                    "-b:v", $"{targetBitrateKbps}k",
                    "-maxrate", $"{maxRateKbps}k",
                    "-bufsize", $"{bufSizeKbps}k",
                    "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart",
                    "-pass", "2",
                    "-passlogfile", passLogPrefix,
                    outputMp4Path,
                ],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(passLogDir))
                Directory.Delete(passLogDir, recursive: true);
        }
    }

    private static async Task RunFfmpegArgumentsAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (int i = 0; i < arguments.Count; i++)
            startInfo.ArgumentList.Add(arguments[i]);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{ffmpegPath}'.");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
    }

    private static GameStartupSettings CreateStartupSettings(ERenderLibrary backend)
        => new()
        {
            StartupWindows =
            [
                new GameWindowStartupSettings
                {
                    WindowTitle = "XRE Splat Cutscene",
                    WindowState = EWindowState.Windowed,
                    Width = FrameWidth,
                    Height = FrameHeight,
                    UseNativeTitleBar = false,
                    Visible = false,
                },
            ],
            DefaultUserSettings = new UserSettings
            {
                PreferredRenderBackend = backend,
                VSync = EVSyncMode.Off,
            },
            NetworkingType = ENetworkingType.Local,
        };

    private static Quaternion LookRotation(Vector3 forward, Vector3 up)
    {
        Vector3 safeUp = MathF.Abs(Vector3.Dot(forward, up)) > 0.99f ? Vector3.UnitZ : up;
        return Quaternion.Normalize(
            Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, safeUp)));
    }

    private static float Lerp(float a, float b, float t)
        => a + ((b - a) * Math.Clamp(t, 0.0f, 1.0f));

    private static float SmoothStep01(float t)
    {
        float x = Math.Clamp(t, 0.0f, 1.0f);
        return x * x * (3.0f - (2.0f * x));
    }

    private static float CubicEaseOut(float t)
    {
        float x = 1.0f - Math.Clamp(t, 0.0f, 1.0f);
        return 1.0f - (x * x * x);
    }

    private static bool HasCommandFlag(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], CommandFlag, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> args,
        out CutsceneCommandOptions? options,
        out string? error)
    {
        options = null;
        string pakPath = string.Empty;
        string assetPath = DefaultAssetPath;
        string clipName = DefaultClipName;
        int splatCount = DefaultLiveSplatCount;
        string cacheAssetPath = string.Empty;
        string outputDirectory = string.Empty;
        string titleText = DefaultTitle;
        string subtitleText = DefaultSubtitle;
        float durationSeconds = DefaultDurationSeconds;
        int fps = DefaultFps;
        ERenderLibrary backend = ERenderLibrary.Vulkan;
        string ffmpegPath = "ffmpeg";
        float yawOffsetDegrees = 0.0f;
        error = null;

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
            if (string.Equals(arg, PakFlag, StringComparison.OrdinalIgnoreCase))
                pakPath = Path.GetFullPath(value);
            else if (string.Equals(arg, AssetFlag, StringComparison.OrdinalIgnoreCase))
                assetPath = value;
            else if (string.Equals(arg, ClipFlag, StringComparison.OrdinalIgnoreCase))
                clipName = value;
            else if (string.Equals(arg, SplatsFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out splatCount) ||
                    splatCount < 10_000 ||
                    splatCount > 8_000_000)
                {
                    error = $"The {SplatsFlag} value '{value}' must be between 10000 and 8000000.";
                    return false;
                }
            }
            else if (string.Equals(arg, CacheFlag, StringComparison.OrdinalIgnoreCase))
                cacheAssetPath = Path.GetFullPath(value);
            else if (string.Equals(arg, OutputDirFlag, StringComparison.OrdinalIgnoreCase))
                outputDirectory = Path.GetFullPath(value);
            else if (string.Equals(arg, TitleFlag, StringComparison.OrdinalIgnoreCase))
                titleText = value;
            else if (string.Equals(arg, SubtitleFlag, StringComparison.OrdinalIgnoreCase))
                subtitleText = value;
            else if (string.Equals(arg, DurationFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds) ||
                    !float.IsFinite(durationSeconds) ||
                    durationSeconds <= 1.0f ||
                    durationSeconds > 60.0f)
                {
                    error = $"The {DurationFlag} value '{value}' must be between 1 and 60 seconds.";
                    return false;
                }
            }
            else if (string.Equals(arg, FpsFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps) ||
                    fps < 10 ||
                    fps > 120)
                {
                    error = $"The {FpsFlag} value '{value}' must be between 10 and 120.";
                    return false;
                }
            }
            else if (string.Equals(arg, BackendFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(value, "vulkan", StringComparison.OrdinalIgnoreCase))
                    backend = ERenderLibrary.Vulkan;
                else if (string.Equals(value, "opengl", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(value, "gl", StringComparison.OrdinalIgnoreCase))
                    backend = ERenderLibrary.OpenGL;
                else
                {
                    error = $"The {BackendFlag} value '{value}' must be 'vulkan' or 'opengl'.";
                    return false;
                }
            }
            else if (string.Equals(arg, FfmpegFlag, StringComparison.OrdinalIgnoreCase))
                ffmpegPath = value;
            else if (string.Equals(arg, YawOffsetFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out yawOffsetDegrees) ||
                    !float.IsFinite(yawOffsetDegrees))
                {
                    error = $"The {YawOffsetFlag} value '{value}' must be a finite number.";
                    return false;
                }
            }
            else
            {
                error = $"Unknown option '{arg}'.";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pakPath) && string.IsNullOrWhiteSpace(cacheAssetPath))
        {
            error = $"Missing required option {PakFlag} (or {CacheFlag}).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error = $"Missing required option {OutputDirFlag}.";
            return false;
        }

        options = new CutsceneCommandOptions(
            pakPath,
            assetPath,
            clipName,
            splatCount,
            cacheAssetPath,
            outputDirectory,
            titleText,
            subtitleText,
            durationSeconds,
            fps,
            backend,
            ffmpegPath,
            yawOffsetDegrees);
        return true;
    }

    private sealed record CutsceneCommandOptions(
        string PakPath,
        string AssetPath,
        string ClipName,
        int SplatCount,
        string CacheAssetPath,
        string OutputDirectory,
        string TitleText,
        string SubtitleText,
        float DurationSeconds,
        int Fps,
        ERenderLibrary Backend,
        string FfmpegPath,
        float YawOffsetDegrees)
    {
        public bool UseLiveAvatarSplats => !string.IsNullOrWhiteSpace(PakPath);
    }

    private sealed record ShotCameraRig(
        string SlotName,
        SceneNode Node,
        Transform Transform,
        CameraComponent Component);

    private readonly record struct ShotCameraDefinition(
        float StartFraction,
        float EndFraction,
        float StartYawDegrees,
        float EndYawDegrees,
        float StartPitchDegrees,
        float EndPitchDegrees,
        float StartFocusHeightFraction,
        float EndFocusHeightFraction,
        float ForwardFocusBias,
        float StartDistanceFactor,
        float EndDistanceFactor,
        float StartFovDegrees,
        float EndFovDegrees,
        float StartHorizontalLeadFactor,
        float EndHorizontalLeadFactor,
        float CacheStartFraction,
        float CacheEndFraction);

    private sealed class CutsceneShotScene(
        XRWorld world,
        RuntimeWorld runtimeWorld,
        SceneNode avatarNode,
        SceneNode? avatarPrefabRoot,
        GaussianCacheComponent? cacheComponent,
        GaussianAvatarSplatComponent? avatarSplatComponent,
        BakeMotionChoice? motionChoice,
        List<PhysicsChainComponent> physicsChains,
        AABB liveAvatarBounds,
        SceneNode cameraGroupNode,
        ShotCameraRig[] shotCameras,
        SceneNode canvasRoot,
        UICanvasComponent canvasComponent,
        SceneNode titleNode,
        UITextComponent titleComponent,
        SceneNode subtitleNode,
        UITextComponent subtitleComponent,
        CutsceneDirectorComponent director,
        float durationSeconds,
        float yawOffsetDegrees)
    {
        public XRWorld World { get; } = world;
        public RuntimeWorld RuntimeWorld { get; } = runtimeWorld;
        public SceneNode AvatarNode { get; } = avatarNode;
        public SceneNode? AvatarPrefabRoot { get; } = avatarPrefabRoot;
        public GaussianCacheComponent? CacheComponent { get; } = cacheComponent;
        public GaussianAvatarSplatComponent? AvatarSplatComponent { get; } = avatarSplatComponent;
        public BakeMotionChoice? MotionChoice { get; } = motionChoice;
        public List<PhysicsChainComponent> PhysicsChains { get; } = physicsChains;
        public AABB LiveAvatarBounds { get; } = liveAvatarBounds;
        public SceneNode CameraGroupNode { get; } = cameraGroupNode;
        public ShotCameraRig[] ShotCameras { get; } = shotCameras;
        public SceneNode CanvasRoot { get; } = canvasRoot;
        public UICanvasComponent CanvasComponent { get; } = canvasComponent;
        public SceneNode TitleNode { get; } = titleNode;
        public UITextComponent TitleComponent { get; } = titleComponent;
        public SceneNode SubtitleNode { get; } = subtitleNode;
        public UITextComponent SubtitleComponent { get; } = subtitleComponent;
        public CutsceneDirectorComponent Director { get; } = director;
        public float DurationSeconds { get; } = durationSeconds;
        public float YawOffsetDegrees { get; } = yawOffsetDegrees;
        public float LastAppliedAnimTimeSeconds { get; set; }
        public int LastSteppedCaptureFrame { get; set; } = -1;

        public bool IsGpuDataReady => AvatarSplatComponent is { } liveSplats
            ? liveSplats.IsGpuDataReady
            : (CacheComponent?.Cache?.IsComplete ?? false) && (CacheComponent?.IsGpuDataReady ?? false);
    }

    private sealed class WorldCutsceneBindingResolver(XRWorld world) : ICutsceneBindingResolver
    {
        public SceneNode? FindNodeById(Guid id)
        {
            foreach (XRScene scene in world.Scenes)
            {
                foreach (SceneNode root in scene.RootNodes)
                {
                    if (root.ID == id)
                        return root;
                    SceneNode? found = root.FindDescendant(t => t.SceneNode?.ID == id);
                    if (found is not null)
                        return found;
                }
            }

            return null;
        }

        public SceneNode? FindNodeByPath(string path) => null;

        public SceneNode? FindPrefabNode(Guid prefabAssetId, Guid prefabNodeId) => null;

        public SceneNode? InstantiatePrefab(Guid prefabAssetId) => null;
    }

    /// <summary>
    /// Coordinates deterministic frame-by-frame cutscene evaluation and render-thread readback scheduling.
    /// </summary>
    private sealed class CutsceneLockstepController(XRViewport viewport, CutsceneShotScene shotScene, int totalFrames, int fps)
    {
        private readonly object _gate = new();
        private int _targetFrameIndex;
        private int _appliedFrameIndex = -1;
        private int _pendingApplyFrame = -1;
        private ulong _appliedRenderFrameId;
        private ulong _scheduledAtRenderFrameId;
        private bool _readyToCapture;
        private bool _detached;

        public void Attach()
        {
            lock (_gate)
            {
                _detached = false;
                _targetFrameIndex = 0;
                _appliedFrameIndex = -1;
                _pendingApplyFrame = -1;
                _scheduledAtRenderFrameId = 0UL;
                _readyToCapture = false;
            }

            ApplyFrameOnUpdateThread(0);
        }

        public void Detach()
        {
            lock (_gate)
            {
                _detached = true;
                _pendingApplyFrame = -1;
                _readyToCapture = false;
            }
        }

        public bool ShouldCaptureFrame(int scheduledFrameCount)
        {
            int frameToEnqueue = -1;
            lock (_gate)
            {
                if (_detached)
                    return false;

                ulong currentRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (_pendingApplyFrame >= 0 && currentRenderFrameId > _scheduledAtRenderFrameId)
                {
                    frameToEnqueue = _pendingApplyFrame;
                    _pendingApplyFrame = -1;
                }

                if (frameToEnqueue < 0)
                {
                    if (!_readyToCapture ||
                        scheduledFrameCount != _targetFrameIndex ||
                        _appliedFrameIndex != scheduledFrameCount ||
                        currentRenderFrameId < _appliedRenderFrameId + (ulong)SettleRenderFramesPerStep ||
                        !shotScene.IsGpuDataReady)
                    {
                        return false;
                    }
                }
            }

            if (frameToEnqueue >= 0)
            {
                Engine.EnqueueUpdateThreadTask(() => ApplyFrameOnUpdateThread(frameToEnqueue));
                return false;
            }

            if (!viewport.TryEnterRenderPipelineReadbackScope(out IDisposable? probeScope))
                return false;

            probeScope?.Dispose();
            return true;
        }

        public void OnFrameScheduled(int captureIndex)
        {
            lock (_gate)
            {
                _readyToCapture = false;
                int nextFrame = captureIndex + 1;
                _targetFrameIndex = nextFrame;
                if (_detached || nextFrame >= totalFrames)
                {
                    _pendingApplyFrame = -1;
                    return;
                }

                // Defer advancing the cutscene until the next render frame begins so end-of-frame UI compositing
                // and readback on the current render frame continue reading the active shot camera's render target.
                _pendingApplyFrame = nextFrame;
                _scheduledAtRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            }
        }

        private void ApplyFrameOnUpdateThread(int frameToApply)
        {
            lock (_gate)
            {
                if (_detached || frameToApply >= totalFrames || _appliedFrameIndex == frameToApply)
                    return;
            }

            double timeSeconds = frameToApply / (double)fps;
            SeekAndEvaluateCutscene(shotScene, timeSeconds, frameToApply);

            lock (_gate)
            {
                if (_detached)
                    return;

                _appliedFrameIndex = frameToApply;
                _appliedRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                _readyToCapture = true;
            }
        }
    }
}
