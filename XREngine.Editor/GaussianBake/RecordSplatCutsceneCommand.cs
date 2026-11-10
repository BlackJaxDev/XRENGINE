using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Editor.Mcp;
using XREngine.Rendering;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Renders a 1080p Gaussian splat cutscene video and matching hero poster from a baked <c>root.asset</c> cache.
/// The shot scene places a <see cref="GaussianCacheComponent"/>, a perspective <see cref="CameraComponent"/>, and a
/// screen-space <see cref="UICanvasComponent"/> with two <see cref="UITextComponent"/> nodes under the control of a
/// <see cref="CutsceneDirectorComponent"/> and records the timeline through <see cref="ViewportSequenceCaptureSession"/>.
/// </summary>
public static class RecordSplatCutsceneCommand
{
    /// <summary>The command line flag that selects this command.</summary>
    public const string CommandFlag = "--record-splat-cutscene";

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
    private const string DefaultTitle = "XRENGINE";
    private const string DefaultSubtitle = "GAUSSIAN SPLAT AVATAR // CUTSCENE PLAYBACK";
    private const float DefaultDurationSeconds = 8.0f;
    private const int DefaultFps = 30;
    private const int WarmupRenderFrames = 45;
    private const int SettleRenderFramesPerStep = 2;

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
    /// copies the hero poster frame, and encodes the MP4 cutscene clip with ffmpeg.
    /// </summary>
    public static int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!TryParseOptions(
                args,
                out string cacheAssetPath,
                out string outputDirectory,
                out string titleText,
                out string subtitleText,
                out float durationSeconds,
                out int fps,
                out ERenderLibrary backend,
                out string ffmpegPath,
                out float yawOffsetDegrees,
                out string? error))
        {
            Console.Error.WriteLine($"Splat cutscene recording failed: {error}");
            return 2;
        }

        if (!File.Exists(cacheAssetPath))
        {
            Console.Error.WriteLine($"Splat cutscene recording failed: the cache root asset '{cacheAssetPath}' does not exist.");
            return 2;
        }

        Directory.CreateDirectory(outputDirectory);
        int totalFrames = Math.Clamp((int)MathF.Round(durationSeconds * fps), 60, ViewportSequenceCaptureOptions.MaximumFrames);
        float effectiveDurationSeconds = totalFrames / (float)fps;

        Console.WriteLine(
            $"Recording splat cutscene: cache='{cacheAssetPath}', output='{outputDirectory}', " +
            $"backend={backend}, frames={totalFrames}, fps={fps}, duration={effectiveDurationSeconds:F2}s.");

        int exitCode = 1;
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(8));

        void AfterWindowsCreated(GameStartupSettings settings, GameState state)
        {
            Engine.AfterCreateWindows -= AfterWindowsCreated;

            _ = Task.Run(async () =>
            {
                try
                {
                    exitCode = await ExecuteRecordingAsync(
                        cacheAssetPath,
                        outputDirectory,
                        titleText,
                        subtitleText,
                        effectiveDurationSeconds,
                        fps,
                        totalFrames,
                        ffmpegPath,
                        yawOffsetDegrees,
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

        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.PreferredRenderBackend, backend);
        Engine.SetSessionSetting((UserSettings userSettings) => userSettings.VSync, EVSyncMode.Off);
        Engine.AfterCreateWindows += AfterWindowsCreated;
        try
        {
            Engine.Run(CreateStartupSettings(backend), new GameState(), beginPlayingAllWorlds: true);
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
        string cacheAssetPath,
        string outputDirectory,
        string titleText,
        string subtitleText,
        float durationSeconds,
        int fps,
        int totalFrames,
        string ffmpegPath,
        float yawOffsetDegrees,
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
                titleText,
                subtitleText,
                durationSeconds,
                yawOffsetDegrees);
        }, cancellationToken).ConfigureAwait(false);

        if (shotScene is null)
            throw new InvalidOperationException("Failed to construct the cutscene shot scene.");

        // Load all chunks of the baked Gaussian cache and wait until the GPU buffers and initial sort are ready.
        await shotScene.CacheComponent.LoadAsync(cacheAssetPath, cancellationToken).ConfigureAwait(false);
        GaussianCache cache = shotScene.CacheComponent.Cache
            ?? throw new InvalidOperationException("GaussianCacheComponent.LoadAsync completed without a cache instance.");

        Console.WriteLine(
            $"Loaded Gaussian cache: nodes={cache.Header.SplatLevelCounts.Sum()}, levels={cache.Header.SplatLevelCounts.Length}, " +
            $"frames={cache.Header.FrameCount}, fps={cache.Header.FrameRate}, " +
            $"bounds=({cache.Header.Bounds.Min} .. {cache.Header.Bounds.Max}).");

        // Now that the cache header is loaded, rebuild the camera trajectory from the exact avatar bounds and open the cutscene.
        await RunOnUpdateThreadAsync(() =>
        {
            ConfigureCutsceneFromBounds(shotScene, cache.Header, durationSeconds, yawOffsetDegrees);
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

            SeekAndEvaluateCutscene(shotScene, 0.0);
        }, cancellationToken).ConfigureAwait(false);

        // Wait until the cache GPU uploads and initial splat sort finish and warm up pipelines for WarmupRenderFrames.
        await WaitForWarmupAsync(shotScene, WarmupRenderFrames, cancellationToken).ConfigureAwait(false);

        // Prepare the sequence capture directory so ViewportSequenceCaptureManager writes into a clean folder.
        string sequenceRootDir = Path.Combine(outputDirectory, "sequence");
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
        CutsceneLockstepController lockstep = new(shotScene, totalFrames, fps);
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

        try
        {
            while (!session!.IsTerminal)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        string manifestDestPath = Path.Combine(outputDirectory, "manifest.json");
        File.Copy(snapshot.ManifestPath, manifestDestPath, overwrite: true);

        if (!string.IsNullOrWhiteSpace(snapshot.ContactSheetPath) && File.Exists(snapshot.ContactSheetPath))
        {
            string contactSheetDestPath = Path.Combine(outputDirectory, "contact-sheet.png");
            File.Copy(snapshot.ContactSheetPath, contactSheetDestPath, overwrite: true);
        }

        string heroFrameSourcePath = Path.Combine(snapshot.OutputDirectory, $"frame_{totalFrames - 1:D6}.png");
        string heroPosterPath = Path.Combine(outputDirectory, "hero-poster-1080.png");
        File.Copy(heroFrameSourcePath, heroPosterPath, overwrite: true);

        string masterVideoPath = Path.Combine(outputDirectory, "cutscene-1080.mp4");
        await EncodeMp4WithFfmpegAsync(ffmpegPath, snapshot.OutputDirectory, fps, masterVideoPath, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Hero poster written to: {heroPosterPath}");
        Console.WriteLine($"Cutscene video written to: {masterVideoPath}");
        return 0;
    }

    private static CutsceneShotScene BuildCutsceneShotScene(
        XRWindow window,
        XRViewport viewport,
        FontGlyphSet font,
        string titleText,
        string subtitleText,
        float durationSeconds,
        float yawOffsetDegrees)
    {
        SceneNode stageNode = GaussianPreviewStage.CreateDefault();

        // 1. GaussianCacheComponent node.
        SceneNode avatarNode = new("AvatarSplatNode");
        avatarNode.SetTransform<Transform>();
        GaussianCacheComponent cacheComponent = avatarNode.AddComponent<GaussianCacheComponent>("AvatarGaussianCache")
            ?? throw new InvalidOperationException("Failed to add GaussianCacheComponent.");
        cacheComponent.Playing = false;
        cacheComponent.LodThresholdPixels = 0.01f;
        cacheComponent.MaxRenderedSplats = 200_000;

        // 2. Cutscene CameraComponent node.
        SceneNode cameraNode = new("CutsceneCamera");
        Transform cameraTransform = cameraNode.SetTransform<Transform>();
        CameraComponent cameraComponent = cameraNode.AddComponent<CameraComponent>("CutsceneCameraComponent")
            ?? throw new InvalidOperationException("Failed to add CameraComponent.");
        cameraComponent.CameraParameters = new XRPerspectiveCameraParameters(40.0f, null, 0.05f, 100.0f);
        cameraComponent.InternalResolutionMode = EInternalResolutionMode.FullResolution;

        // 3. Screen-space UICanvasComponent with Title and Subtitle UITextComponents.
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
            titleText,
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
            subtitleText,
            fontSize: 30.0f,
            position: new Vector2(100.0f, 230.0f),
            size: new Vector2(920.0f, 64.0f),
            fillColor: new ColorF4(0.66f, 0.86f, 1.0f, 0.0f),
            outlineColor: new ColorF4(0.03f, 0.06f, 0.14f, 0.0f),
            outlineThickness: 0.0f,
            out UITextComponent subtitleComponent);

        // 4. CutsceneDirectorComponent node.
        SceneNode directorNode = new("CutsceneDirector");
        directorNode.SetTransform<Transform>();
        CutsceneDirectorComponent director = directorNode.AddComponent<CutsceneDirectorComponent>("CutsceneDirector")
            ?? throw new InvalidOperationException("Failed to add CutsceneDirectorComponent.");
        director.ResidencyEnabled = false;
        director.TargetViewport = viewport;

        XRScene scene = new("SplatCutsceneScene", stageNode, avatarNode, cameraNode, canvasRoot, directorNode);
        XRWorld world = new("SplatCutsceneWorld", scene);
        world.Settings.AmbientLightColor = new ColorF3(1.0f, 1.0f, 1.0f);
        world.Settings.AmbientLightIntensity = GaussianPreviewStage.AmbientIntensity;

        director.BindingResolver = new WorldCutsceneBindingResolver(world);
        director.SetRuntimeSlot("camera", cameraNode);
        director.SetRuntimeSlot("avatar", avatarNode);
        director.SetRuntimeSlot("title", titleNode);
        director.SetRuntimeSlot("subtitle", subtitleNode);

        // Attach the world to the window and bind the UI canvas to the camera and viewport.
        RuntimeWorld runtimeWorld = Engine.GetOrCreateWorld(world);
        Task beginPlay = RuntimeWorldHostServices.Current?.BeginPlayAsync(runtimeWorld) ?? runtimeWorld.BeginPlayAsync();
        beginPlay.GetAwaiter().GetResult();
        IRuntimeRenderWorld renderWorld = Engine.GetOrCreateRenderWorld(world);
        window.SetWorld(renderWorld);

        RenderPipeline renderPipeline = RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline();
        XRCamera camera = cameraComponent.Camera;
        camera.RenderPipeline = renderPipeline;
        camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
        camera.MsaaSampleCountOverride = 1u;
        camera.TsrRenderScaleOverride = 1.0f;

        viewport.SetRenderPipelineFromCamera = false;
        viewport.AllowAutomaticInternalResolution = false;
        viewport.RenderPipeline = renderPipeline;
        viewport.SetInternalResolution(FrameWidth, FrameHeight, true);
        viewport.AllowUIRender = true;
        cameraComponent.UserInterface = canvasComponent;
        viewport.ScreenSpaceUserInterfaceOverride = canvasComponent;
        viewport.CameraComponent = cameraComponent;
        cameraComponent.SetManualExposure(1.0f);
        cameraComponent.SetBloomEnabled(false);

        canvasTransform.UpdateLayout();
        canvasTransform.RecalculateMatrixHierarchy(true, true, ELoopType.Sequential);

        return new CutsceneShotScene(
            world,
            avatarNode,
            cacheComponent,
            cameraNode,
            cameraTransform,
            cameraComponent,
            canvasRoot,
            canvasComponent,
            titleNode,
            titleComponent,
            subtitleNode,
            subtitleComponent,
            director,
            durationSeconds,
            yawOffsetDegrees);
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
        GaussianCacheHeader header,
        float durationSeconds,
        float yawOffsetDegrees)
    {
        Vector3 boundsMin = header.Bounds.Min;
        Vector3 boundsMax = header.Bounds.Max;
        Vector3 boundsSize = boundsMax - boundsMin;
        float height = MathF.Max(boundsSize.Y, 0.4f);
        float centerX = (boundsMin.X + boundsMax.X) * 0.5f;
        float centerZ = (boundsMin.Z + boundsMax.Z) * 0.5f;

        float hintRadius = header.CameraHints?.DefaultRadius ?? 0.0f;
        float defaultRadius = hintRadius > 0.1f
            ? hintRadius
            : MathF.Max(height * 2.2f, 1.5f);

        // Configure near/far planes to fit the avatar cleanly throughout the orbit and close-up push-in.
        float nearZ = Math.Clamp(defaultRadius * 0.05f, 0.02f, 0.25f);
        float farZ = MathF.Max(defaultRadius * 8.0f, 50.0f);
        shotScene.CameraComponent.CameraParameters = new XRPerspectiveCameraParameters(40.0f, null, nearZ, farZ);

        // Sample smooth camera position, rotation, and FOV curves at 60 Hz across the cutscene duration.
        int sampleCount = Math.Max(120, (int)MathF.Ceiling(durationSeconds * 60.0f) + 1);
        CutsceneTime[] sampleTimes = new CutsceneTime[sampleCount];
        Vector3[] cameraPositions = new Vector3[sampleCount];
        Quaternion[] cameraRotations = new Quaternion[sampleCount];
        float[] cameraFovs = new float[sampleCount];

        // Orbit phase eases from -108 deg to +15 deg (relative to base yaw) over 0..72% of duration,
        // while the push-in narrows distance and FOV and shifts framing so the avatar sits on the right-center
        // of the 16:9 viewport and leaves the left side clear for the title and subtitle.
        float baseYawDegrees = (header.CameraHints?.DefaultYawDegrees ?? 0.0f) + yawOffsetDegrees;
        float startYawDegrees = baseYawDegrees + 72.0f;
        float endYawDegrees = baseYawDegrees + 194.0f;

        float orbitRadius = defaultRadius * 0.86f;
        float heroRadius = defaultRadius * 0.54f;

        Vector3 orbitFocus = new(centerX, boundsMin.Y + (height * 0.55f), centerZ);
        Vector3 heroFocus = new(centerX, boundsMin.Y + (height * 0.68f), centerZ);

        for (int i = 0; i < sampleCount; i++)
        {
            double normalized = i / (double)(sampleCount - 1);
            double tSeconds = normalized * durationSeconds;
            sampleTimes[i] = CutsceneTime.FromSeconds(tSeconds);

            // Smooth orbit progression that settles into the hero angle by 78% of the clip.
            float orbitProgress = SmoothStep01((float)Math.Clamp(normalized / 0.78, 0.0, 1.0));
            // Subtle secondary settle over the final 22% so motion feels alive before locking the hero frame.
            float settleProgress = SmoothStep01((float)Math.Clamp((normalized - 0.18) / 0.68, 0.0, 1.0));

            float yawDegrees = Lerp(startYawDegrees, endYawDegrees, orbitProgress);
            float pitchDegrees = Lerp(13.0f, 4.5f, settleProgress);
            float radius = Lerp(orbitRadius, heroRadius, settleProgress);
            float fovDegrees = Lerp(42.0f, 34.0f, settleProgress);

            float yawRadians = yawDegrees * (MathF.PI / 180.0f);
            float pitchRadians = pitchDegrees * (MathF.PI / 180.0f);

            Quaternion orbitOrientation = Quaternion.Normalize(
                Quaternion.CreateFromYawPitchRoll(yawRadians, -pitchRadians, 0.0f));
            Vector3 cameraForward = Vector3.Transform(-Vector3.UnitZ, orbitOrientation);
            Vector3 cameraRight = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));

            // Shift the look-at target toward the camera's left (-cameraRight) so the avatar projects into the
            // right-center of the 16:9 frame, keeping the left title safe zone completely unobstructed.
            float horizontalLead = Lerp(height * 0.12f, height * 0.23f, settleProgress);
            Vector3 focus = Vector3.Lerp(orbitFocus, heroFocus, settleProgress) - (cameraRight * horizontalLead);
            Vector3 offset = Vector3.Transform(new Vector3(0.0f, 0.0f, radius), orbitOrientation);
            Vector3 position = focus + offset;

            Vector3 lookDirection = Vector3.Normalize(focus - position);
            Quaternion lookRotation = LookRotation(lookDirection, Vector3.UnitY);

            cameraPositions[i] = position;
            cameraRotations[i] = lookRotation;
            cameraFovs[i] = fovDegrees;
        }

        CutsceneTime fullDuration = CutsceneTime.FromSeconds(durationSeconds);

        CutsceneAsset asset = new()
        {
            Duration = fullDuration,
            Bindings =
            [
                new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "camera" },
                new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "avatar" },
                new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "title" },
                new CutsceneBindingReference { Kind = ECutsceneBindingKind.RuntimeSlot, SlotName = "subtitle" },
            ],
            Tracks =
            [
                // Track 0: CameraCutTrack selecting binding slot 0 (camera).
                new CameraCutTrack
                {
                    Name = "CameraCuts",
                    BindingSlot = 0,
                    WhenFinished = ECutsceneFinishBehavior.KeepState,
                    Clips =
                    [
                        new CameraCutClip
                        {
                            Start = CutsceneTime.Zero,
                            Duration = fullDuration,
                            CameraBindingSlot = 0,
                            Extrapolation = ECutsceneClipExtrapolation.Hold,
                        },
                    ],
                },
                // Track 1: Camera TransformTrack (eased orbit settling into push-in hero shot).
                new TransformTrack
                {
                    Name = "CameraTransform",
                    BindingSlot = 0,
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
                                Times = sampleTimes,
                                Values = cameraPositions,
                            },
                            Rotation = new CutsceneQuaternionCurve
                            {
                                Interpolation = ECutsceneCurveInterpolation.Linear,
                                Times = sampleTimes,
                                Values = cameraRotations,
                            },
                        },
                    ],
                },
                // Track 2: CameraPropertyTrack animating VerticalFieldOfView during push-in.
                new CameraPropertyTrack
                {
                    Name = "CameraFov",
                    BindingSlot = 0,
                    WhenFinished = ECutsceneFinishBehavior.KeepState,
                    Clips =
                    [
                        new CameraPropertyClip
                        {
                            Start = CutsceneTime.Zero,
                            Duration = fullDuration,
                            Extrapolation = ECutsceneClipExtrapolation.Hold,
                            FieldOfView = new CutsceneFloatCurve
                            {
                                Interpolation = ECutsceneCurveInterpolation.Linear,
                                Times = sampleTimes,
                                Values = cameraFovs,
                            },
                        },
                    ],
                },
                // Track 3: Avatar GaussianCacheComponent.PlaybackTimeSeconds track.
                new PropertyTrack
                {
                    Name = "AvatarCachePlaybackTime",
                    BindingSlot = 1,
                    ComponentType = nameof(GaussianCacheComponent),
                    MemberPath = nameof(GaussianCacheComponent.PlaybackTimeSeconds),
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
                                Times = [CutsceneTime.Zero, fullDuration],
                                Values = [0.0f, durationSeconds],
                            },
                        },
                    ],
                },
                // Track 4: Title TransformTrack (eased slide-in and scale settle, readable for >5s).
                BuildUiSlideTransformTrack(
                    name: "TitleTransform",
                    bindingSlot: 2,
                    fullDuration: fullDuration,
                    revealStartSeconds: 1.4f,
                    revealEndSeconds: 2.7f,
                    startPosition: new Vector3(24.0f, 300.0f, 0.0f),
                    endPosition: new Vector3(96.0f, 300.0f, 0.0f),
                    startScale: new Vector3(0.92f, 0.92f, 1.0f),
                    endScale: Vector3.One),
                // Track 5: Title Opacity PropertyTrack.
                BuildUiFadePropertyTrack(
                    name: "TitleOpacity",
                    bindingSlot: 2,
                    memberPath: nameof(UITextComponent.Opacity),
                    fullDuration: fullDuration,
                    revealStartSeconds: 1.4f,
                    revealEndSeconds: 2.5f,
                    targetValue: 1.0f),
                // Track 6: Title OutlineOpacity PropertyTrack.
                BuildUiFadePropertyTrack(
                    name: "TitleOutlineOpacity",
                    bindingSlot: 2,
                    memberPath: nameof(UITextComponent.OutlineOpacity),
                    fullDuration: fullDuration,
                    revealStartSeconds: 1.4f,
                    revealEndSeconds: 2.5f,
                    targetValue: 0.95f),
                // Track 7: Title OutlineThickness PropertyTrack (subtle stroke pulse during reveal).
                BuildUiOutlinePulseTrack(
                    name: "TitleOutlineThickness",
                    bindingSlot: 2,
                    fullDuration: fullDuration,
                    revealStartSeconds: 1.4f,
                    peakSeconds: 2.1f,
                    settleSeconds: 2.8f,
                    peakThickness: 4.2f,
                    settledThickness: 2.6f),
                // Track 8: Subtitle TransformTrack (staggered eased slide-in, readable for >4.5s).
                BuildUiSlideTransformTrack(
                    name: "SubtitleTransform",
                    bindingSlot: 3,
                    fullDuration: fullDuration,
                    revealStartSeconds: 2.0f,
                    revealEndSeconds: 3.2f,
                    startPosition: new Vector3(44.0f, 230.0f, 0.0f),
                    endPosition: new Vector3(100.0f, 230.0f, 0.0f),
                    startScale: new Vector3(0.95f, 0.95f, 1.0f),
                    endScale: Vector3.One),
                // Track 9: Subtitle Opacity PropertyTrack.
                BuildUiFadePropertyTrack(
                    name: "SubtitleOpacity",
                    bindingSlot: 3,
                    memberPath: nameof(UITextComponent.Opacity),
                    fullDuration: fullDuration,
                    revealStartSeconds: 2.0f,
                    revealEndSeconds: 3.1f,
                    targetValue: 1.0f),
                // Track 10: Subtitle OutlineOpacity PropertyTrack.
                BuildUiFadePropertyTrack(
                    name: "SubtitleOutlineOpacity",
                    bindingSlot: 3,
                    memberPath: nameof(UITextComponent.OutlineOpacity),
                    fullDuration: fullDuration,
                    revealStartSeconds: 2.0f,
                    revealEndSeconds: 3.1f,
                    targetValue: 0.90f),
                // Track 11: Subtitle OutlineThickness PropertyTrack.
                BuildUiOutlinePulseTrack(
                    name: "SubtitleOutlineThickness",
                    bindingSlot: 3,
                    fullDuration: fullDuration,
                    revealStartSeconds: 2.0f,
                    peakSeconds: 2.6f,
                    settleSeconds: 3.3f,
                    peakThickness: 3.0f,
                    settledThickness: 1.8f),
            ],
        };

        shotScene.Director.Asset = asset;
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

    private static void SeekAndEvaluateCutscene(CutsceneShotScene shotScene, double timeSeconds)
    {
        CutscenePlayer? player = shotScene.Director.Player;
        if (player is null)
            return;

        player.Seek(CutsceneTime.FromSeconds(timeSeconds), ESeekIntent.Jump);
        shotScene.Director.AdvanceFrame(0.0);

        shotScene.CameraTransform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        shotScene.AvatarNode.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        UICanvasTransform canvasTransform = shotScene.CanvasComponent.CanvasTransform;
        canvasTransform.InvalidateLayout();
        canvasTransform.UpdateLayout();
        canvasTransform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
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

            bool isCacheComplete = shotScene.CacheComponent.Cache?.IsComplete ?? false;
            bool ready = isCacheComplete && shotScene.CacheComponent.IsGpuDataReady;
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
                    $"Timed out waiting for GaussianCacheComponent GPU readiness " +
                    $"(IsComplete={isCacheComplete}, IsGpuDataReady={shotScene.CacheComponent.IsGpuDataReady}).");
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
        out string cacheAssetPath,
        out string outputDirectory,
        out string titleText,
        out string subtitleText,
        out float durationSeconds,
        out int fps,
        out ERenderLibrary backend,
        out string ffmpegPath,
        out float yawOffsetDegrees,
        out string? error)
    {
        cacheAssetPath = string.Empty;
        outputDirectory = string.Empty;
        titleText = DefaultTitle;
        subtitleText = DefaultSubtitle;
        durationSeconds = DefaultDurationSeconds;
        fps = DefaultFps;
        backend = ERenderLibrary.Vulkan;
        ffmpegPath = "ffmpeg";
        yawOffsetDegrees = 0.0f;
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
            if (string.Equals(arg, CacheFlag, StringComparison.OrdinalIgnoreCase))
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

        if (string.IsNullOrWhiteSpace(cacheAssetPath))
        {
            error = $"Missing required option {CacheFlag}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error = $"Missing required option {OutputDirFlag}.";
            return false;
        }

        return true;
    }

    private sealed record CutsceneShotScene(
        XRWorld World,
        SceneNode AvatarNode,
        GaussianCacheComponent CacheComponent,
        SceneNode CameraNode,
        Transform CameraTransform,
        CameraComponent CameraComponent,
        SceneNode CanvasRoot,
        UICanvasComponent CanvasComponent,
        SceneNode TitleNode,
        UITextComponent TitleComponent,
        SceneNode SubtitleNode,
        UITextComponent SubtitleComponent,
        CutsceneDirectorComponent Director,
        float DurationSeconds,
        float YawOffsetDegrees);

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
    private sealed class CutsceneLockstepController(CutsceneShotScene shotScene, int totalFrames, int fps)
    {
        private readonly object _gate = new();
        private int _targetFrameIndex;
        private int _appliedFrameIndex = -1;
        private ulong _appliedRenderFrameId;
        private bool _readyToCapture;
        private bool _detached;

        public void Attach()
        {
            lock (_gate)
            {
                _detached = false;
                _targetFrameIndex = 0;
                _appliedFrameIndex = -1;
                _readyToCapture = false;
            }

            ApplyFrameOnUpdateThread(0);
        }

        public void Detach()
        {
            lock (_gate)
            {
                _detached = true;
                _readyToCapture = false;
            }
        }

        public bool ShouldCaptureFrame(int scheduledFrameCount)
        {
            lock (_gate)
            {
                if (_detached || !_readyToCapture || scheduledFrameCount != _targetFrameIndex)
                    return false;

                ulong currentRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                return currentRenderFrameId >= _appliedRenderFrameId + (ulong)SettleRenderFramesPerStep
                    && shotScene.CacheComponent.IsGpuDataReady;
            }
        }

        public void OnFrameScheduled(int captureIndex)
        {
            int nextFrame;
            lock (_gate)
            {
                _readyToCapture = false;
                nextFrame = captureIndex + 1;
                _targetFrameIndex = nextFrame;
                if (_detached || nextFrame >= totalFrames)
                    return;
            }

            Engine.EnqueueUpdateThreadTask(() => ApplyFrameOnUpdateThread(nextFrame));
        }

        private void ApplyFrameOnUpdateThread(int frameToApply)
        {
            lock (_gate)
            {
                if (_detached || frameToApply >= totalFrames || _appliedFrameIndex == frameToApply)
                    return;
            }

            double timeSeconds = frameToApply / (double)fps;
            SeekAndEvaluateCutscene(shotScene, timeSeconds);

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
