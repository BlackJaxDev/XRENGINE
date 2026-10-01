using XREngine.Runtime.Bootstrap;
using XREngine.Scene;
using XREngine.Audio;
using XREngine.Input;
using XREngine.Networking;
using System.Runtime.ExceptionServices;

namespace XREngine.Browser;

/// <summary>
/// Owns one real engine world on the browser event thread. Its physics factory must come
/// from an installed browser backend; the reference scene is never used as a fallback.
/// </summary>
internal sealed class BrowserEngineSession(Func<AbstractPhysicsScene> createPhysicsScene) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Func<AbstractPhysicsScene> _createPhysicsScene = createPhysicsScene
        ?? throw new ArgumentNullException(nameof(createPhysicsScene));
    private IDisposable? _capabilities;
    private IDisposable? _startupPolicy;
    private IDisposable? _assets;
    private IDisposable? _adapters;
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

    public RuntimeWorld? World => _runtimeWorld;
    public bool IsRunning => _running;
    public bool HasEngineOwnership => _engineInitialized;
    public int CanvasWidth => _canvasWidth;
    public int CanvasHeight => _canvasHeight;

    /// <summary>Composes a fetched XRWorld through the shared world host and begins gameplay.</summary>
    public async Task StartAsync(XRWorld world, GameStartupSettings authoredSettings, GameState initialState,
        CancellationToken cancellationToken = default)
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

            GameStartupSettings settings = ProjectBrowserStartup(authoredSettings, world,
                out _canvasWidth, out _canvasHeight);

            _capabilities = RuntimeApplicationCapabilityServices.Install(new RuntimeApplicationCapabilities(
                IsConfigured: true,
                AllowsLocalInput: true,
                AllowsWindows: false,
                AllowsAudio: true,
                AllowsVr: false,
                AllowsRendererBackends: false));
            _startupPolicy = RuntimeEngineStartupPolicyServices.Install(BrowserEngineStartupPolicy.Instance);
            _previousNetworkTransport = NetworkTransportServices.Current;
            _installedNetworkTransport = new WebSocketNetworkTransportBackend();
            NetworkTransportServices.Current = _installedNetworkTransport;
            _networkTransportInstalled = true;
            Engine.InitializeForCallerThread(settings);
            _engineInitialized = true;
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
                _createPhysicsScene,
                composeRenderedWorlds: false);
            _world = world;
            _runtimeWorld = Engine.GetOrCreateWorld(world);
            _gameState = initialState;
            (_gameState.Worlds ??= []).Add(_runtimeWorld);
            _gameState.Windows = [];
            Engine.Time.Timer.StartCallerThreadLoop();
            await Engine.PlayMode.BeginStandalonePlayAsync();
            if (requestedEpoch != Volatile.Read(ref _epoch) || cancellationToken.IsCancellationRequested)
            {
                StopCore();
                return;
            }

            _inputViewport = new BrowserEngineInputViewport();
            _localPlayer = RuntimePlayerControllerServices.Current?.GetOrCreateLocalPlayer(ELocalPlayerIndex.One)
                ?? throw new InvalidOperationException("Browser local-player controller services are not installed.");
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

    /// <summary>Runs one production engine frame on the browser event thread.</summary>
    public bool Step(double elapsedSeconds)
    {
        if (!_running)
            return false;
        return Engine.Time.Timer.StepFrame(elapsedSeconds);
    }

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
        if (_inputViewport is not null)
            Capture(errors, _inputViewport.Reset);
        if (_localPlayer is not null && ReferenceEquals(_localPlayer.Viewport, _inputViewport))
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

        if (_gameState?.Worlds is { } worlds)
            Capture(errors, () => worlds.RemoveAll(candidate => ReferenceEquals(candidate, _runtimeWorld)));
        _runtimeWorld = null;
        _world = null;
        _gameState = null;
        _canvasWidth = 0;
        _canvasHeight = 0;
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
