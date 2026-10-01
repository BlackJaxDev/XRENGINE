using XREngine.Core.Files;
using XREngine.Audio.WebAudio;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.JavaScript;
using XREngine.Runtime.Bootstrap;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;

namespace XREngine.Browser;

/// <summary>Page-facing owner of a fetched engine world and its caller-thread frame loop.</summary>
public static partial class BrowserEngineExports
{
    private static readonly SemaphoreSlim Lifecycle = new(1, 1);
    private static Func<AbstractPhysicsScene>? _physicsSceneFactory;
    private static CancellationTokenSource? _loading;
    private static BrowserEngineAssetSource? _source;
    private static IDisposable? _assetRegistrations;
    private static BrowserEngineSession? _session;
    private static bool _assetOwnerBound;
    private static ShaderProgramArtifactCatalog? _shaderArtifacts;
    private static IRuntimeAssetSource? _previousStorageSource;
    private static bool _storageSourceInstalled;
    private static IAssetFileSystem? _previousFileSystem;
    private static bool _fileSystemInstalled;
    private static int _epoch;

    /// <summary>Installed only by a browser-compatible physics leaf before page startup.</summary>
    internal static void InstallPhysicsSceneFactory(Func<AbstractPhysicsScene> factory)
        => _physicsSceneFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal static ShaderProgramArtifactCatalog? ShaderArtifacts => _shaderArtifacts;

    [JSExport]
    public static async Task<string> StartAsync(string manifestUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);
        int requestedEpoch = Interlocked.Increment(ref _epoch);
        await Lifecycle.WaitAsync();
        bool ownsStartup = false;
        try
        {
            if (requestedEpoch != Volatile.Read(ref _epoch))
                throw new OperationCanceledException("Browser world startup was superseded.");
            if (_session is not null || _source is not null)
                throw new InvalidOperationException("Stop the active browser engine world before starting another.");

            Func<AbstractPhysicsScene> physicsFactory = _physicsSceneFactory
                ?? throw new NotSupportedException(
                    "Browser Jolt physics is not installed. A real XRWorld cannot begin play without its selected physics backend.");

            ownsStartup = true;
            _loading = new CancellationTokenSource();
            CancellationToken token = _loading.Token;
            _source = await BrowserEngineAssetSource.OpenAsync(manifestUrl, token);
            token.ThrowIfCancellationRequested();
            _previousStorageSource = DirectStorageIO.Source;
            DirectStorageIO.Source = _source;
            _storageSourceInstalled = true;
            _previousFileSystem = AssetFileSystemServices.Current;
            AssetFileSystemServices.Current = _source.FileSystem;
            _fileSystemInstalled = true;
            Engine.Assets.BindRuntimeSource(_source);
            _assetOwnerBound = true;
            _assetRegistrations = RuntimeAssetBootstrap.InstallEngineAssetServices();
            BrowserGameComposition.Initialize();
            IGameLaunchBootstrap? bootstrap = BrowserGameComposition.CreateBootstrap();
            if (bootstrap?.ApplicationProfile.AllowsVr == true)
                throw new NotSupportedException("Browser VR application profiles require desktop OpenXR/OpenVR leaves.");
            bootstrap?.InitializeRegistrations();
            _shaderArtifacts = await _source.LoadShaderArtifactsAsync(token);
            XRWorld world = await Engine.Assets.LoadFromRuntimeSourceAsync(
                _source.StartupWorldPath, typeof(XRWorld), cancellationToken: token) as XRWorld
                ?? throw new InvalidDataException("The cooked startup asset did not deserialize to XRWorld.");
            GameStartupSettings cookedSettings = _source.StartupSettingsPath is { } settingsPath
                ? await Engine.Assets.LoadFromRuntimeSourceAsync(
                    settingsPath, typeof(GameStartupSettings), cancellationToken: token) as GameStartupSettings
                    ?? throw new InvalidDataException("The cooked startup settings asset did not deserialize to GameStartupSettings.")
                : BrowserEngineStartupPolicy.Instance.CreateDefaultGameSettings();
            GameStartupSettings browserCookedSettings = cookedSettings.DeepClone();
            browserCookedSettings.DefaultUserSettings.PhysicsLibrary = EPhysicsLibrary.Jolt;
            GameStartupSettings configuredSettings = bootstrap?.ConfigureStartup(browserCookedSettings) ?? browserCookedSettings;
            GameState initialState = bootstrap?.CreateInitialGameState() ?? new GameState();
            token.ThrowIfCancellationRequested();
            if (requestedEpoch != Volatile.Read(ref _epoch))
                throw new OperationCanceledException("Browser world startup was superseded.");

            _session = new BrowserEngineSession(physicsFactory);
            await _session.StartAsync(world, configuredSettings, initialState, token);
            token.ThrowIfCancellationRequested();
            return $"{world.Name ?? "<unnamed>"}: {_session.World?.RootNodes.Count ?? 0} root nodes playing; " +
                $"Jolt physics; fixed rate {configuredSettings.FixedFramesPerSecond:F0} Hz; " +
                $"canvas request {_session.CanvasWidth}×{_session.CanvasHeight}";
        }
        catch (Exception startupError)
        {
            // An admission failure belongs to the caller, not the active world. Only a
            // start that acquired lifecycle resources may tear them down.
            if (!ownsStartup)
                throw;
            try
            {
                await StopCoreAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Browser engine startup and rollback both failed.",
                    startupError, cleanupError);
            }
            throw;
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    [JSExport]
    public static bool Step(double elapsedSeconds)
        => _loading?.IsCancellationRequested == true ? false : _session?.Step(elapsedSeconds) ?? false;

    /// <summary>Call directly from a trusted page gesture to resume browser output.</summary>
    [JSExport]
    public static Task<bool> UnlockAudioAsync()
        => WebAudioTransport.UnlockAsync();

    [JSExport]
    public static string GetAudioState() => WebAudioTransport.State;

    [JSExport]
    public static void ResetFrameTiming()
        => _session?.ResetFrameTiming();

    [JSExport]
    public static void InputKey(int key, bool down) => _session?.InputKey(key, down);
    [JSExport]
    public static void InputText(string text) => _session?.InputText(text);
    [JSExport]
    public static void InputPointer(float x, float y) => _session?.InputPointer(x, y);
    [JSExport]
    public static void InputMouseButton(int button, bool down) => _session?.InputMouseButton(button, down);
    [JSExport]
    public static void InputScroll(float x, float y) => _session?.InputScroll(x, y);
    [JSExport]
    public static bool GetInputCaptureDesired() => _session?.CaptureDesired ?? false;
    [JSExport]
    public static void ResetInput() => _session?.ResetInput();
    [JSExport]
    public static void PublishInput(bool focused, bool captured, bool gamepadConnected, int gamepadButtonMask,
        float leftTrigger, float rightTrigger, float leftX, float leftY, float rightX, float rightY)
        => _session?.PublishInput(focused, captured, gamepadConnected, gamepadButtonMask,
            leftTrigger, rightTrigger, leftX, leftY, rightX, rightY);

    [JSExport]
    public static async Task StopAsync()
    {
        Interlocked.Increment(ref _epoch);
        CancellationTokenSource? loading = Volatile.Read(ref _loading);
        try { loading?.Cancel(); }
        catch (ObjectDisposedException) { /* Startup already released this generation. */ }
        await Lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    private static async Task StopCoreAsync()
    {
        List<Exception> errors = [];
        static void Capture(List<Exception> failures, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }

        if (_session is not null)
        {
            try
            {
                await _session.DisposeAsync();
            }
            catch (Exception) when (_session?.HasEngineOwnership == true)
            {
                // The engine or scheduler still owns callbacks. Keep the world,
                // source, and registrations alive for a later shutdown retry.
                throw;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            _session = null;
        }

        BrowserEngineAssetSource? source = _source;
        if (_assetOwnerBound && source is not null)
        {
            Capture(errors, () => Engine.Assets.UnbindRuntimeSource(source));
            _assetOwnerBound = false;
        }
        IDisposable? registrations = _assetRegistrations;
        _assetRegistrations = null;
        if (registrations is not null)
            Capture(errors, registrations.Dispose);
        _shaderArtifacts = null;
        Capture(errors, () =>
        {
            if (_storageSourceInstalled && ReferenceEquals(DirectStorageIO.Source, source))
                DirectStorageIO.Source = _previousStorageSource;
        });
        _previousStorageSource = null;
        _storageSourceInstalled = false;
        Capture(errors, () =>
        {
            if (_fileSystemInstalled && ReferenceEquals(AssetFileSystemServices.Current, source?.FileSystem))
                AssetFileSystemServices.Current = _previousFileSystem;
        });
        _previousFileSystem = null;
        _fileSystemInstalled = false;
        _source = null;
        if (source is not null)
            Capture(errors, source.Dispose);
        CancellationTokenSource? loading = _loading;
        _loading = null;
        if (loading is not null)
            Capture(errors, loading.Dispose);

        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Browser engine shutdown encountered multiple cleanup failures.", errors);
    }
}
