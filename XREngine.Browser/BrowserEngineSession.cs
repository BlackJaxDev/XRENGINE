using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Audio;
using XREngine.Input;
using XREngine.Networking;
using System.Runtime.ExceptionServices;
using XREngine.Rendering;
using XREngine.Rendering.WebGPU;
using XREngine.Scene.Physics;
using XREngine.Components;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Data.Rendering;
using XREngine.Data.Core;

namespace XREngine.Browser;

/// <summary>
/// Owns one real engine world on the browser event thread. Its physics factory must come
/// from an installed browser backend; the reference scene is never used as a fallback.
/// </summary>
internal sealed class BrowserEngineSession(PhysicsBackendCatalog physicsBackends) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PhysicsBackendCatalog _physicsBackends = physicsBackends
        ?? throw new ArgumentNullException(nameof(physicsBackends));
    private IDisposable? _capabilities;
    private IDisposable? _startupPolicy;
    private IDisposable? _assets;
    private IDisposable? _adapters;
    private IDisposable? _renderingServices;
    private BrowserCanvasRenderTarget? _canvas;
    private WebGpuRendererHost? _renderer;
    private XRViewport? _renderViewport;
    private int _rendererSession;
    private static int _nextRendererSession = 0x40000000;
    private bool _previousDebugOpaquePipeline;
    private bool _debugOpaquePipelineChanged;
    private INetworkTransportBackend? _previousNetworkTransport;
    private INetworkTransportBackend? _installedNetworkTransport;
    private bool _networkTransportInstalled;
    private XRWorld? _world;
    private RuntimeWorld? _runtimeWorld;
    private GameState? _gameState;
    private int _canvasWidth;
    private int _canvasHeight;
    private BrowserEngineInputViewport? _inputViewport;
    private IPawnController? _localPlayer;
    private int _epoch;
    private bool _engineInitialized;
    private bool _audioConfigured;
    private bool _previousAudioV2;
    private EAudioTransport _previousAudioTransport;
    private EAudioEffects _previousAudioEffects;
    private bool _running;
    private bool _disposed;
    private ShaderProgramArtifact? _tonemapArtifact;
    private readonly List<ObjectCacheOwnership> _sessionObjects = [];
    private GameStartupSettings? _previousGameSettings;
    private UserSettings? _previousUserSettings;

    public RuntimeWorld? World => _runtimeWorld;
    public bool IsRunning => _running;
    public bool HasEngineOwnership => _engineInitialized || _sessionObjects.Count != 0;
    public int CanvasWidth => _canvasWidth;
    public int CanvasHeight => _canvasHeight;
    public int RendererSession => _rendererSession;
    public bool HasPresentedCanvasFrame => _renderer?.IsBackendReplacementFrameReady ?? false;
    public int CanvasPreparationState => HasPresentedCanvasFrame ? 1
        : _renderViewport?.RenderPipelineInstance.LastResourceGenerationFailure is not null ? -1 : 0;

    /// <summary>Formats cold startup diagnostics without adding work to successful frame submission.</summary>
    public string GetRenderingStatus()
    {
        XRRenderPipelineInstance? pipeline = _renderViewport?.RenderPipelineInstance;
        return $"Renderer={_renderer?.State.ToString() ?? "absent"}; " +
            $"draws={_renderer?.LastEngineMeshDrawCount ?? 0}; " +
            $"pipeline decline={pipeline?.LastRenderDeclineReason ?? "none"}; " +
            $"resource failure={pipeline?.LastResourceGenerationFailure ?? "none"}.";
    }

    /// <summary>Composes a fetched XRWorld through the shared world host and begins gameplay.</summary>
    public async Task StartAsync(XRWorld world, GameStartupSettings authoredSettings, GameState initialState,
        CancellationToken cancellationToken = default, string? canvasId = null,
        IShaderProgramArtifactResolver? shaderArtifacts = null, EngineMaterialVariantCatalog? materialVariants = null,
        ShaderProgramArtifact? tonemapArtifact = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(authoredSettings);
        ArgumentNullException.ThrowIfNull(initialState);
        if (initialState.Windows is { Count: > 0 } || initialState.Worlds is { Count: > 0 })
            throw new NotSupportedException("Browser startup accepts one configured canvas world; preconstructed GameState windows or worlds are unsupported.");
        int requestedEpoch = Interlocked.Increment(ref _epoch);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (requestedEpoch != Volatile.Read(ref _epoch))
                return;
            if (_running || _engineInitialized)
                throw new InvalidOperationException("A browser engine world is already active.");

            GameStartupSettings settings = OwnConstruction(() => ProjectBrowserStartup(authoredSettings, world,
                out _canvasWidth, out _canvasHeight));
            _tonemapArtifact = tonemapArtifact;

            _capabilities = RuntimeApplicationCapabilityServices.Install(new RuntimeApplicationCapabilities(
                IsConfigured: true,
                AllowsLocalInput: true,
                AllowsWindows: false,
                AllowsAudio: true,
                AllowsVr: false,
                AllowsRendererBackends: canvasId is not null));
            _startupPolicy = RuntimeEngineStartupPolicyServices.Install(BrowserEngineStartupPolicy.Instance);
            _previousNetworkTransport = NetworkTransportServices.Current;
            _installedNetworkTransport = new WebSocketNetworkTransportBackend();
            NetworkTransportServices.Current = _installedNetworkTransport;
            _networkTransportInstalled = true;
            _previousGameSettings = Engine.PersistentGameSettings;
            _previousUserSettings = Engine.UserSettings;
            OwnConstruction(() => Engine.InitializeForCallerThread(settings));
            _engineInitialized = true;
            // Camera and material factories are shared data services even without a
            // physical output. Both world hosts resolve physics from this same catalog.
            _renderingServices = RuntimeCallerThreadRenderingBootstrap.Install(
                BrowserRendererComposition.BackendCatalog, _physicsBackends,
                CreateDefaultPipeline);
            if (canvasId is not null)
            {
                _canvas = new BrowserCanvasRenderTarget(canvasId);
                _renderer = OwnConstruction(() => BrowserRendererComposition.CreateRequired(_canvas)) as WebGpuRendererHost
                    ?? throw new InvalidOperationException("WebGPU.EngineRenderer.Required: the browser canvas requires the shared engine WebGPU renderer.");
                _renderer.BindShaderArtifacts(shaderArtifacts, materialVariants);
                _rendererSession = Interlocked.Increment(ref _nextRendererSession);
                if (EngineRenderingSettingsApplication.AdvancedRenderPipelineMode == EAdvancedRenderPipelineMode.Required)
                    throw new AdvancedRenderPipelineNotSupportedException(
                        AdvancedRenderPipelineSelectionResolver.Resolve(EAdvancedRenderPipelineMode.Required,
                            _renderer.GetAdvancedRenderPipelineCapabilities(), stereo: false));
                if (Engine.EditorPreferences?.Debug is { } debugOptions)
                {
                    _previousDebugOpaquePipeline = debugOptions.UseDebugOpaquePipeline;
                    debugOptions.UseDebugOpaquePipeline = false;
                    _debugOpaquePipelineChanged = true;
                }
            }
            if (Engine.EffectiveSettings.GPURenderDispatch ||
                Engine.EffectiveSettings.ForceMeshSubmissionStrategy is { } forced &&
                forced != XREngine.Data.Rendering.EMeshSubmissionStrategy.CpuDirect)
            {
                throw new NotSupportedException(
                    "WebGPU.MeshSubmission.Unsupported: this browser engine profile requires CpuDirect scene submission.");
            }
            _previousAudioV2 = AudioSettings.AudioArchitectureV2;
            _previousAudioTransport = AudioSettings.DefaultTransport;
            _previousAudioEffects = AudioSettings.DefaultEffects;
            AudioSettings.AudioArchitectureV2 = true;
            AudioSettings.DefaultTransport = EAudioTransport.WebAudio;
            AudioSettings.DefaultEffects = EAudioEffects.Passthrough;
            AudioSettings.ApplyTo(Engine.Audio);
            _audioConfigured = true;
            _assets = RuntimeAssetBootstrap.InstallEngineAssetServices();
            _adapters = RuntimeAdapterBootstrap.InstallEngineHostServices(
                RuntimeAdapterProfile.Animation | RuntimeAdapterProfile.Audio | RuntimeAdapterProfile.Input,
                CreatePhysicsScene,
                composeRenderedWorlds: canvasId is not null);
            _world = world;
            // Establish the caller's physics ownership before attaching components
            // whose activation may allocate native bodies. No frame is dispatched here.
            Engine.Time.Timer.StartCallerThreadLoop();
            _runtimeWorld = OwnConstruction(() => Engine.GetOrCreateWorld(world));
            _gameState = initialState;
            (_gameState.Worlds ??= []).Add(_runtimeWorld);
            _gameState.Windows = [];
            // Capture synchronous activation allocations, then close the thread-affine
            // publication boundary before awaiting any game-provided async startup work.
            await OwnConstruction(() => Engine.PlayMode.BeginStandalonePlayAsync());
            if (requestedEpoch != Volatile.Read(ref _epoch) || cancellationToken.IsCancellationRequested)
            {
                StopCore();
                return;
            }

            _inputViewport = new BrowserEngineInputViewport();
            _localPlayer = RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(ELocalPlayerIndex.One)
                ?? throw new InvalidOperationException("Browser local-player controller services are not installed.");
            if (_renderer is not null)
            {
                _renderViewport = OwnConstruction(() => new XRViewport(null, checked((uint)_canvasWidth), checked((uint)_canvasHeight))
                {
                    WorldInstanceOverride = _runtimeWorld.GetRenderWorld(),
                });
                _renderViewport.BindInputSource(_inputViewport);
                _renderViewport.BindLocalPlayer(_localPlayer);
                RefreshCamera();
                _renderer.BindEngineViewport(_renderViewport);
                Engine.Time.Timer.RenderFrame += RenderCanvasFrame;
            }
            else
                _localPlayer.Viewport = _inputViewport;
            _running = true;
        }
        catch (Exception startupError)
        {
            try
            {
                StopCore();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Browser engine session startup and rollback both failed.",
                    startupError, cleanupError);
            }
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private T OwnConstruction<T>(Func<T> create)
    {
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        T value = create();
        _sessionObjects.Add(publication.CompleteWithOwnership());
        return value;
    }

    private void OwnConstruction(Action create)
        => OwnConstruction(() => { create(); return true; });

    private AbstractPhysicsScene CreatePhysicsScene()
        => _physicsBackends.CreateRequired(EPhysicsLibrary.Jolt);

    private RenderPipeline CreateDefaultPipeline()
    {
        DefaultRenderPipeline pipeline = new();
        if (_tonemapArtifact is { } artifact)
            pipeline.BindWebTonemapArtifact(artifact);
        return pipeline;
    }

    /// <summary>Runs one production engine frame on the browser event thread.</summary>
    public bool Step(double elapsedSeconds)
    {
        if (!_running)
            return false;
        RefreshCamera();
        return Engine.Time.Timer.StepFrame(elapsedSeconds);
    }

    private void RefreshCamera()
    {
        if (_renderViewport is null)
            return;
        CameraComponent? camera = (_localPlayer?.ControlledPawnComponent as IRuntimeInputControllablePawn)
            ?.RuntimeCameraComponent as CameraComponent;
        if (camera?.Camera.RenderPipeline is DefaultRenderPipeline pipeline && _tonemapArtifact is { } artifact)
            pipeline.BindWebTonemapArtifact(artifact);
        if (camera is not null && !ReferenceEquals(_renderViewport.CameraComponent, camera))
            _renderViewport.CameraComponent = camera;
    }

    private void RenderCanvasFrame()
    {
        if (_canvas?.Surface.CanRender == true)
            _renderer?.RenderFrame(Engine.Time.Timer.Render.Delta);
    }

    public void InitializeGraphics(string colorFormat)
    {
        if (_renderer is null || _canvas is null || _rendererSession == 0)
            throw new InvalidOperationException("WebGPU.EngineCanvas.Required: start a canvas world first.");
        _canvas.SetColorFormat(colorFormat);
        _renderer.MarkReady(_rendererSession);
        _renderer.Initialize();
    }

    public void UpdateSurface(RuntimeSurfaceState surface)
    {
        if (_canvas is null || _renderViewport is null || _renderer is null)
            throw new InvalidOperationException("WebGPU.EngineCanvas.Required: start a canvas world first.");
        bool replaced = surface.Generation != _canvas.Surface.Generation;
        _canvas.UpdateSurface(surface);
        _renderer.SynchronizeEngineViewport(replaced);
        if (!surface.CanRender)
            ResetFrameTiming();
    }

    public void RendererFailed(bool deviceLost) => _renderer?.MarkFailed(deviceLost);

    public bool CaptureDesired => _inputViewport?.CaptureDesired ?? false;
    public void InputKey(int key, bool down) => _inputViewport?.Key(key, down);
    public void InputText(string text) => _inputViewport?.Text(text);
    public void InputPointer(float x, float y) => _inputViewport?.Pointer(x, y);
    public void InputMouseButton(int button, bool down) => _inputViewport?.MouseButton(button, down);
    public void InputScroll(float x, float y) => _inputViewport?.Scroll(x, y);
    public void PublishInput(bool focused, bool captured, bool gamepadConnected, int gamepadButtonMask,
        float leftTrigger, float rightTrigger, float leftX, float leftY, float rightX, float rightY)
    {
        _inputViewport?.Gamepad(gamepadConnected, gamepadButtonMask, leftTrigger, rightTrigger,
            leftX, leftY, rightX, rightY);
        _inputViewport?.Publish(focused, captured);
    }

    public void ResetInput() => _inputViewport?.Reset();

    /// <summary>Discards a suspended page's elapsed time without a physics catch-up burst.</summary>
    public void ResetFrameTiming()
    {
        if (_running)
            Engine.Time.Timer.ResetFrameTiming();
    }

    /// <summary>Invalidates pending startup and tears down after it has released ownership.</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _epoch);
        await _lifecycle.WaitAsync();
        try
        {
            StopCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _disposed = true;
    }

    private void StopCore()
    {
        List<Exception> errors = [];
        static void Capture(List<Exception> failures, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }

        _running = false;
        if (_renderer is not null)
            Engine.Time.Timer.RenderFrame -= RenderCanvasFrame;
        if (_renderer is not null)
            Capture(errors, () => _renderer.BindEngineViewport(null));
        XRViewport? renderViewport = _renderViewport;
        if (renderViewport is not null)
            Capture(errors, renderViewport.Destroy);
        _renderViewport = null;
        if (_inputViewport is not null)
            Capture(errors, _inputViewport.Reset);
        if (_localPlayer is not null &&
            (ReferenceEquals(_localPlayer.Viewport, _inputViewport) ||
             ReferenceEquals(_localPlayer.Viewport, renderViewport)))
            Capture(errors, () => _localPlayer.Viewport = null);
        _localPlayer = null;
        _inputViewport = null;
        if (_engineInitialized)
        {
            Capture(errors, Engine.PlayMode.EndStandalonePlay);
            if (_world is not null)
                Capture(errors, () => RuntimeWorldHostServices.Current?.Remove(_world));
            try
            {
                Engine.StopCallerThreadSession();
            }
            catch (Exception error)
            {
                // The engine or scheduler still owns callbacks. Keep all installed
                // providers and the fetched source for a later shutdown retry.
                errors.Add(error);
                if (errors.Count == 1)
                    ExceptionDispatchInfo.Capture(error).Throw();
                string reason = error.Message.Contains("jobs remain pending", StringComparison.Ordinal)
                    ? "Caller-thread jobs remain pending during browser engine shutdown."
                    : "Browser engine shutdown is incomplete.";
                throw new AggregateException(reason, errors);
            }
            _engineInitialized = false;
        }

        if (_renderer is not null)
            Capture(errors, _renderer.Dispose);
        _renderer = null;
        _canvas = null;
        _rendererSession = 0;
        _tonemapArtifact = null;
        if (_gameState?.Worlds is { } worlds)
            Capture(errors, () => worlds.RemoveAll(candidate => ReferenceEquals(candidate, _runtimeWorld)));
        _runtimeWorld = null;
        _world = null;
        _gameState = null;
        _canvasWidth = 0;
        _canvasHeight = 0;
        if (_previousGameSettings is { } previousGameSettings)
        {
            try
            {
                Engine.GameSettings = previousGameSettings;
                _previousGameSettings = null;
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (_previousUserSettings is { } previousUserSettings)
        {
            try
            {
                Engine.UserSettings = previousUserSettings;
                _previousUserSettings = null;
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (_previousGameSettings is not null || _previousUserSettings is not null)
            throw new AggregateException("Browser settings restoration is incomplete.", errors);
        for (int index = _sessionObjects.Count - 1; index >= 0; index--)
        {
            try
            {
                _sessionObjects[index].Dispose();
                _sessionObjects.RemoveAt(index);
            }
            catch (Exception error) { errors.Add(error); }
        }
        Capture(errors, XRObjectBase.ProcessPendingDestructions);
        if (_sessionObjects.Count != 0)
            throw new AggregateException("Browser session object cleanup is incomplete.", errors);
        if (_audioConfigured)
        {
            AudioSettings.AudioArchitectureV2 = _previousAudioV2;
            AudioSettings.DefaultTransport = _previousAudioTransport;
            AudioSettings.DefaultEffects = _previousAudioEffects;
            Capture(errors, () => AudioSettings.ApplyTo(Engine.Audio));
            _audioConfigured = false;
        }
        if (_adapters is { } adapters)
            Capture(errors, adapters.Dispose);
        _adapters = null;
        if (_renderingServices is not null)
            Capture(errors, _renderingServices.Dispose);
        _renderingServices = null;
        if (_debugOpaquePipelineChanged)
        {
            Capture(errors, () => Engine.EditorPreferences.Debug.UseDebugOpaquePipeline = _previousDebugOpaquePipeline);
            _debugOpaquePipelineChanged = false;
        }
        if (_networkTransportInstalled)
        {
            if (ReferenceEquals(NetworkTransportServices.Current, _installedNetworkTransport))
                NetworkTransportServices.Current = _previousNetworkTransport;
            _previousNetworkTransport = null;
            _installedNetworkTransport = null;
            _networkTransportInstalled = false;
        }
        if (_assets is { } assets)
            Capture(errors, assets.Dispose);
        _assets = null;
        if (_startupPolicy is { } startupPolicy)
            Capture(errors, startupPolicy.Dispose);
        _startupPolicy = null;
        if (_capabilities is { } capabilities)
            Capture(errors, capabilities.Dispose);
        _capabilities = null;

        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Browser engine session teardown encountered multiple failures.", errors);
    }

    private static GameStartupSettings ProjectBrowserStartup(GameStartupSettings authored, XRWorld world,
        out int canvasWidth, out int canvasHeight)
    {
        if (authored.NetworkingType != ENetworkingType.Local)
            throw new NotSupportedException("Browser realtime networking requires the WebSocket transport leaf.");
        if (authored is IVRGameStartupSettings { StartVrOnLaunch: true })
            throw new NotSupportedException("Browser VR startup is unavailable; OpenXR and OpenVR are desktop leaves.");
        if (authored.StartupWindows.Count > 1)
            throw new NotSupportedException("Browser startup currently supports one canvas output.");
        if (authored.AudioEffectsOverride is { HasOverride: true, Value: not EAudioEffects.Passthrough })
            throw new NotSupportedException("WebAudio.EffectsUnsupported: authored SteamAudio or OpenAL EFX cannot run in the browser.");
        if (authored.AudioTransportOverride is { HasOverride: true, Value: not EAudioTransport.WebAudio })
            throw new NotSupportedException("WebAudio.TransportUnsupported: authored native audio cannot run in the browser.");

        canvasWidth = 1280;
        canvasHeight = 720;
        if (authored.StartupWindows.Count == 1)
        {
            GameWindowStartupSettings output = authored.StartupWindows[0];
            if (output.TargetWorld is not null && !ReferenceEquals(output.TargetWorld, world))
                throw new InvalidOperationException("The browser startup window targets a different XRWorld than the fetched startup asset.");
            if (output.Width <= 0 || output.Height <= 0)
                throw new InvalidOperationException("Browser canvas startup dimensions must be positive.");
            if (output.WindowState != EWindowState.Windowed)
                throw new NotSupportedException("Browser fullscreen/window-state requests require a page gesture and are not installed at startup.");
            if (output.TransparentFramebuffer || output.OutputHDR == true)
                throw new NotSupportedException("Browser canvas transparency and HDR output are not installed for this render tier.");
            if (output.LocalPlayers != ELocalPlayerIndexMask.One)
                throw new NotSupportedException("Browser split-screen local player outputs are not installed.");
            canvasWidth = output.Width;
            canvasHeight = output.Height;
        }

        GameStartupSettings projected = authored.DeepClone();
        projected.StartupWindows = [];
        projected.RunWithoutWindows = true;
        projected.LogOutputToFile = false;
        projected.DefaultUserSettings.PhysicsLibrary = EPhysicsLibrary.Jolt;
        projected.RecalcChildMatricesLoopTypeOverride.SetOverride(ELoopType.Sequential);
        projected.TickGroupedItemsInParallelOverride.SetOverride(false);
        projected.AudioArchitectureV2Override.SetOverride(true);
        projected.AudioTransportOverride.SetOverride(EAudioTransport.WebAudio);
        projected.AudioEffectsOverride.SetOverride(EAudioEffects.Passthrough);
        BrowserEngineStartupPolicy.Instance.PrepareSettings(projected);
        return projected;
    }
}
