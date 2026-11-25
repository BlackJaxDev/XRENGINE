using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// The world of one cutscene recording and the scene objects that the recorder changes or reads. The recorder pauses the
/// world, so no normal tick moves anything. <see cref="ApplyFrame"/> then moves the cutscene and the physics chains in fixed
/// steps, so each recorded frame shows the exact state at its time.
/// </summary>
/// <remarks>
/// Create the object, then call <see cref="Prepare"/> and <see cref="OpenCutscene"/> on the update thread. All other members
/// except <see cref="IsReadyForCapture"/> and <see cref="DescribeReadiness"/> also run on the update thread.
/// </remarks>
internal sealed class CutsceneRecordingScene
{
    private readonly List<IRenderCaptureReadiness> _readinessSources = [];
    private readonly List<PhysicsChainComponent> _physicsChains = [];
    private readonly List<UICanvasComponent> _canvases = [];
    private readonly List<ModelComponent> _skinnedModels = [];
    private readonly List<SceneNode> _rootNodes = [];
    private readonly CutsceneCapturePreparation _preparation;
    private CutscenePlayer? _player;
    private int _lastAppliedFrame = -1;
    private bool _discontinuityInStep;

    /// <summary>Creates the recording state for a world that has begun play.</summary>
    public CutsceneRecordingScene(
        XRWindow window,
        XRViewport viewport,
        RuntimeWorld runtimeWorld,
        CutsceneDirectorComponent director,
        CutsceneRecordSettings settings,
        CutsceneCapturePreparation preparation)
    {
        Window = window;
        Viewport = viewport;
        RuntimeWorld = runtimeWorld;
        Director = director;
        Settings = settings;
        _preparation = preparation;
    }

    /// <summary>The window that renders the recorded viewport.</summary>
    public XRWindow Window { get; }

    /// <summary>The viewport that the recorder reads back.</summary>
    public XRViewport Viewport { get; }

    /// <summary>The runtime world of the recording. The recorder pauses it and ticks its groups directly.</summary>
    public RuntimeWorld RuntimeWorld { get; }

    /// <summary>The director that plays the recorded cutscene.</summary>
    public CutsceneDirectorComponent Director { get; }

    /// <summary>The settings of the recording.</summary>
    public CutsceneRecordSettings Settings { get; }

    /// <summary>The number of physics chains that the recorder steps.</summary>
    public int PhysicsChainCount => _physicsChains.Count;

    /// <summary>The number of components whose capture readiness the recorder checks.</summary>
    public int ReadinessSourceCount => _readinessSources.Count;

    /// <summary>The duration of one captured frame.</summary>
    public CutsceneTime FrameDuration => CutsceneTime.FromSeconds(1.0 / Settings.FramesPerSecond);

    /// <summary>
    /// Finds the components that the recording uses, applies the capture settings to the cameras, the physics chains, and
    /// the viewport, and pauses the world. Call it once on the update thread, after the world began play.
    /// </summary>
    public void Prepare()
    {
        _rootNodes.Clear();
        foreach (XRScene scene in RuntimeWorld.TargetWorld?.Scenes ?? [])
        {
            foreach (SceneNode root in scene.SceneRoots)
                _rootNodes.Add(root);
        }

        // The recording uses the desktop Advanced pipeline, so a recorded frame looks like the editor view and keeps
        // Advanced-only passes, for example ordered late surfaces that blend with a scene color snapshot. The window
        // viewport already owns an Advanced pipeline with a bound window output, so the recording reuses it.
        AdvancedRenderPipeline capturePipeline = Viewport.RenderPipeline as AdvancedRenderPipeline ?? new AdvancedRenderPipeline(false);
        // Editor selection outlines must not appear in the recorded frames.
        capturePipeline.EnableEditorHighlightModule = false;
        CameraComponent? firstCamera = null;
        UICanvasComponent? screenCanvas = null;
        for (int i = 0; i < _rootNodes.Count; i++)
        {
            _rootNodes[i].IterateComponents(component =>
            {
                if (component is IRenderCaptureReadiness readiness)
                    _readinessSources.Add(readiness);

                switch (component)
                {
                    case PhysicsChainComponent chain:
                        ConfigurePhysicsChain(chain);
                        break;
                    case CameraComponent camera:
                        ConfigureCamera(camera, capturePipeline);
                        firstCamera ??= camera;
                        break;
                    case UICanvasComponent canvas:
                        _canvases.Add(canvas);
                        if (canvas.CanvasTransform.DrawSpace == ECanvasDrawSpace.Screen)
                        {
                            canvas.CanvasTransform.SetSize(new System.Numerics.Vector2(Settings.Width, Settings.Height));
                            screenCanvas ??= canvas;
                        }
                        break;
                    case ModelComponent model:
                        _skinnedModels.Add(model);
                        break;
                }
            }, iterateChildHierarchy: true);
        }

        // A camera shows the screen-space UI of the world, so each shot camera gets the same canvas as the viewport.
        if (screenCanvas is not null)
        {
            for (int i = 0; i < _rootNodes.Count; i++)
            {
                _rootNodes[i].IterateComponents<CameraComponent>(
                    camera => camera.UserInterface = screenCanvas,
                    iterateChildHierarchy: true);
            }
        }

        Viewport.SetRenderPipelineFromCamera = false;
        Viewport.AllowAutomaticInternalResolution = false;
        Viewport.RenderPipeline = capturePipeline;
        Viewport.SetInternalResolution(Settings.Width, Settings.Height, true);
        Viewport.AllowUIRender = screenCanvas is not null;
        Viewport.ScreenSpaceUserInterfaceOverride = screenCanvas;
        if (firstCamera is not null)
            Viewport.CameraComponent = firstCamera;

        _preparation.RequestWorldTextures(_rootNodes);
        RuntimeWorld.PausePlay();
    }

    /// <summary>
    /// Opens the cutscene of the director for the recording: the director shows its cameras in <see cref="Viewport"/>, the
    /// clock follows the steps of <see cref="ApplyFrame"/>, and the director loads no residency, because the recorder pins
    /// every texture. Throws when the cutscene does not open.
    /// </summary>
    public IReadOnlyList<CutsceneCompileDiagnostic> OpenCutscene(CancellationToken cancellationToken)
    {
        Director.ResidencyEnabled = false;
        Director.TargetViewport = Viewport;
        Director.OpenAsync(cancellationToken).GetAwaiter().GetResult();
        if (!Director.IsOpen || Director.Player is not { } player)
        {
            string reasons = string.Join("; ", Director.Diagnostics.Select(static diagnostic => diagnostic.Message));
            throw new InvalidOperationException($"The cutscene director did not open the cutscene: {reasons}");
        }

        _player = player;
        player.Clock.Mode = ECutsceneClockMode.Wall;
        player.Discontinuity += OnDiscontinuity;
        player.Play();
        return Director.Diagnostics;
    }

    /// <summary>The length of the recording: the settings value, or the duration of the cutscene asset.</summary>
    public double GetDurationSeconds()
    {
        if (Settings.DurationSeconds is { } seconds)
            return seconds;

        return Director.Asset?.Duration.ToSeconds() ?? 0.0;
    }

    /// <summary>
    /// Returns the start time of each camera cut clip of the cutscene, in order. The recorder renders each time before the
    /// capture, so the programs and textures of every shot are ready.
    /// </summary>
    public List<double> GetCameraCutStartSeconds()
    {
        List<double> starts = [];
        if (Director.Asset is not { } asset)
            return starts;

        foreach (CutsceneTrack track in asset.Tracks)
        {
            if (track is not CameraCutTrack)
                continue;

            foreach (CutsceneClip clip in track.Clips)
                starts.Add(clip.Start.ToSeconds());
        }

        starts.Sort();
        return starts;
    }

    /// <summary>
    /// Shows the frame <paramref name="frameIndex"/>. When the frame directly follows the last applied frame, the cutscene
    /// advances in <see cref="CutsceneRecordSettings.PhysicsSubstepsPerFrame"/> equal steps, and the physics chains tick once
    /// after each step. Otherwise, the frame is a jump: the cutscene seeks to the time, the chains reset to the new pose,
    /// and they settle for <see cref="CutsceneRecordSettings.SettleSubstepsAfterDiscontinuity"/> steps. A camera cut inside
    /// a step resets and settles the chains the same way.
    /// </summary>
    /// <param name="frameIndex">The frame to show. Its cutscene time is <c>frameIndex / FramesPerSecond</c>.</param>
    /// <param name="forceJump">True to treat the frame as a jump also when it follows the last applied frame.</param>
    public void ApplyFrame(int frameIndex, bool forceJump = false)
    {
        if (_player is not { } player)
            throw new InvalidOperationException("Open the cutscene before the first frame.");

        double frameSeconds = frameIndex / (double)Settings.FramesPerSecond;
        bool jump = forceJump || _lastAppliedFrame < 0 || frameIndex != _lastAppliedFrame + 1;
        if (jump)
        {
            player.Seek(CutsceneTime.FromSeconds(frameSeconds), ESeekIntent.Jump);
            if (player.State != ECutsceneTransportState.Playing)
                player.Play();
            _discontinuityInStep = false;
            Director.AdvanceFrame(0.0);
            ResetAndSettlePhysicsChains();
        }
        else
        {
            int substeps = Settings.PhysicsSubstepsPerFrame;
            double stepSeconds = 1.0 / (Settings.FramesPerSecond * (double)substeps);
            for (int step = 0; step < substeps; step++)
            {
                _discontinuityInStep = false;
                Director.AdvanceFrame(stepSeconds);
                if (_discontinuityInStep)
                    ResetAndSettlePhysicsChains();
                else
                    TickPhysicsChainsOnce();
            }
        }

        _lastAppliedFrame = frameIndex;
        UpdateMatricesAndLayout();
    }

    /// <summary>Forgets the last applied frame, so the next <see cref="ApplyFrame"/> call is a jump.</summary>
    public void ForgetLastFrame()
        => _lastAppliedFrame = -1;

    /// <summary>
    /// True when the textures, the programs, and every <see cref="IRenderCaptureReadiness"/> component of the world are
    /// ready. Any thread can read it.
    /// </summary>
    public bool IsReadyForCapture
    {
        get
        {
            if (!_preparation.AreTexturesAndProgramsReady(Window))
                return false;

            for (int i = 0; i < _readinessSources.Count; i++)
            {
                if (!_readinessSources[i].IsReadyForCapture)
                    return false;
            }

            return true;
        }
    }

    /// <summary>A text for a timeout message that tells which part of the world is not ready.</summary>
    public string DescribeReadiness()
    {
        List<string> parts = [_preparation.DescribeTexturesAndPrograms(Window)];
        for (int i = 0; i < _readinessSources.Count; i++)
        {
            if (!_readinessSources[i].IsReadyForCapture)
                parts.Add(_readinessSources[i].DescribeCaptureReadiness());
        }

        return string.Join(", ", parts);
    }

    /// <summary>Stops listening to the player. Call it on the update thread after the recording.</summary>
    public void Detach()
    {
        if (_player is { } player)
            player.Discontinuity -= OnDiscontinuity;
        _player = null;
    }

    private void OnDiscontinuity(CutsceneDiscontinuity discontinuity)
    {
        if (discontinuity.Kind == ECutsceneDiscontinuityKind.Full)
            _discontinuityInStep = true;
    }

    private void ConfigurePhysicsChain(PhysicsChainComponent chain)
    {
        if (!chain.IsActive)
            return;

        if (Settings.DeterministicPhysicsChains)
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
        }

        chain.SetRuntimeVisibility(true);
        _physicsChains.Add(chain);
    }

    private void ConfigureCamera(CameraComponent cameraComponent, RenderPipeline capturePipeline)
    {
        XRCamera camera = cameraComponent.Camera;
        camera.RenderPipeline = capturePipeline;
        cameraComponent.InternalResolutionMode = EInternalResolutionMode.FullResolution;
        if (cameraComponent.CameraParameters is XRPerspectiveCameraParameters perspective)
        {
            cameraComponent.CameraParameters = new XRPerspectiveCameraParameters(
                perspective.VerticalFieldOfView,
                Settings.Width / (float)Settings.Height,
                perspective.NearZ,
                perspective.FarZ);
        }

        if (Settings.DisableAntiAliasing)
        {
            camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
            camera.MsaaSampleCountOverride = 1u;
            camera.TsrRenderScaleOverride = 1.0f;
        }

        if (Settings.ManualExposure is { } exposure)
            cameraComponent.SetManualExposure(exposure);
        if (Settings.DisableBloom)
            cameraComponent.SetBloomEnabled(false);
    }

    // A reset waits for the next simulation step. The late tick applies it, and the settle steps then let the chains hang
    // under gravity from the new pose.
    private void ResetAndSettlePhysicsChains()
    {
        if (_physicsChains.Count == 0)
            return;

        for (int i = 0; i < _physicsChains.Count; i++)
        {
            PhysicsChainComponent chain = _physicsChains[i];
            chain.SetRuntimeVisibility(true);
            chain.ResetSimulationToCurrentPose();
        }

        RuntimeWorld.TickGroup(ETickGroup.Late);
        UpdateRootMatrices();
        for (int step = 0; step < Settings.SettleSubstepsAfterDiscontinuity; step++)
            TickPhysicsChainsOnce();
    }

    private void TickPhysicsChainsOnce()
    {
        if (_physicsChains.Count == 0)
            return;

        for (int i = 0; i < _physicsChains.Count; i++)
            _physicsChains[i].SetRuntimeVisibility(true);

        RuntimeWorld.TickGroup(ETickGroup.PostPhysics);
        RuntimeWorld.TickGroup(ETickGroup.Late);
        UpdateRootMatrices();
    }

    private void UpdateRootMatrices()
    {
        for (int i = 0; i < _rootNodes.Count; i++)
            _rootNodes[i].Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
    }

    // The world is paused, so the recorder updates the matrices, the skinning palettes, and the UI layout of the frame.
    private void UpdateMatricesAndLayout()
    {
        UpdateRootMatrices();
        for (int i = 0; i < _skinnedModels.Count; i++)
            RefreshSkinning(_skinnedModels[i]);

        for (int i = 0; i < _canvases.Count; i++)
        {
            UICanvasTransform canvasTransform = _canvases[i].CanvasTransform;
            canvasTransform.InvalidateLayout();
            canvasTransform.UpdateLayout();
            canvasTransform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        }
    }

    private static void RefreshSkinning(ModelComponent model)
    {
        if (!model.IsActive)
            return;

        foreach (RenderableMesh renderableMesh in model.Meshes)
        {
            foreach (var lod in renderableMesh.LODs)
            {
                XRMeshRenderer? renderer = lod?.Renderer;
                if (renderer?.Mesh?.HasSkinning == true && renderer.EnsureSkinningBuffers(logWarnings: false))
                    renderer.RefreshBoneMatricesFromRenderState();
            }
        }
    }
}
