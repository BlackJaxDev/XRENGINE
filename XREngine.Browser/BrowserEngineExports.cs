using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Audio.WebAudio;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.JavaScript;
using XREngine.Runtime.Bootstrap;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Page-facing owner of a fetched engine world and its caller-thread frame loop.</summary>
public static partial class BrowserEngineExports
{
    private static readonly SemaphoreSlim Lifecycle = new(1, 1);
    private static readonly PhysicsBackendCatalog PhysicsBackends = new();
    private static CancellationTokenSource? _loading;
    private static BrowserEngineAssetSource? _source;
    private static IDisposable? _assetRegistrations;
    private static IDisposable? _materialConstruction;
    private static BrowserEngineSession? _session;
    private static bool _assetOwnerBound;
    private static ShaderProgramArtifactCatalog? _shaderArtifacts;
    private static EngineMaterialVariantCatalog? _materialVariants;
    private static IRuntimeAssetSource? _previousStorageSource;
    private static bool _storageSourceInstalled;
    private static IAssetFileSystem? _previousFileSystem;
    private static bool _fileSystemInstalled;
    private static int _epoch;
    private static readonly List<ObjectCacheOwnership> StartupObjects = [];

    /// <summary>Installed only by a browser-compatible physics leaf before page startup.</summary>
    internal static void InstallPhysicsBackend(IPhysicsBackendModule module)
        => PhysicsBackends.Register(module);

    internal static ShaderProgramArtifactCatalog? ShaderArtifacts => _shaderArtifacts;
    internal static EngineMaterialVariantCatalog? MaterialVariants => _materialVariants;

    /// <summary>Starts a headless engine world for explicit world-start diagnostics.</summary>
    [JSExport]
    public static Task<string> StartAsync(string manifestUrl) => StartCoreAsync(manifestUrl, null);

    /// <summary>Starts the authored world with one production canvas output.</summary>
    [JSExport]
    public static Task<string> StartCanvasAsync(string manifestUrl, string canvasId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasId);
        return StartCoreAsync(manifestUrl, canvasId);
    }

    private static async Task<string> StartCoreAsync(string manifestUrl, string? canvasId)
    {
        string? qualityPreset = TakeRequestedCanvasQualityPreset();
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);
        int requestedEpoch = Interlocked.Increment(ref _epoch);
        await Lifecycle.WaitAsync();
        bool ownsStartup = false;
        string stage = "admit startup";
        try
        {
            if (requestedEpoch != Volatile.Read(ref _epoch))
                throw new OperationCanceledException("Browser world startup was superseded.");
            if (_session is not null || _source is not null)
                throw new InvalidOperationException("Stop the active browser engine world before starting another.");

            if (!PhysicsBackends.TryGet(EPhysicsLibrary.Jolt, out _))
                throw new NotSupportedException(
                    "Browser Jolt physics is not installed. A real XRWorld cannot begin play without its selected physics backend.");

            ownsStartup = true;
            _loading = new CancellationTokenSource();
            CancellationToken token = _loading.Token;
            stage = "open asset catalog";
            _source = await BrowserEngineAssetSource.OpenAsync(manifestUrl, token);
            token.ThrowIfCancellationRequested();
            stage = "install published type metadata";
            if (_source.PublishedMetadataPath is not { } metadataPath
                || _source.PublishedMetadataFingerprint is not { } metadataFingerprint)
                throw new InvalidDataException("PublishedMetadata.Missing: the browser world bundle has no published type metadata.");
            using (RuntimeAssetIntegration metadata = await _source.ReadForIntegrationAsync(metadataPath, token))
            {
                token.ThrowIfCancellationRequested();
                if (requestedEpoch != Volatile.Read(ref _epoch))
                    throw new OperationCanceledException("Browser world startup was superseded before type metadata installation.");
                AotRuntimeMetadataStore.InstallVerifiedBrowserMetadata(metadata.Payload, metadataFingerprint);
            }
            XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Published);
            stage = "install runtime asset services";
            _previousStorageSource = DirectStorageIO.Source;
            DirectStorageIO.Source = _source;
            _storageSourceInstalled = true;
            _previousFileSystem = AssetFileSystemServices.Current;
            AssetFileSystemServices.Current = _source.FileSystem;
            _fileSystemInstalled = true;
            Engine.Assets.BindRuntimeSource(_source);
            _assetOwnerBound = true;
            _assetRegistrations = XREngine.Data.RegistrationLeaseGroup.Create(static leases =>
            {
                leases.Add(RuntimeAssetBootstrap.InstallEngineAssetServices());
                leases.Add(RenderingPublishedCookedAssetRegistration.InstallBrowserBitmapFontCodec());
            });
            _materialConstruction = BrowserEngineMaterialConstruction.Install();
            stage = "initialize game registrations";
            BrowserGameComposition.Initialize();
            IGameLaunchBootstrap? bootstrap = BrowserGameComposition.CreateBootstrap();
            if (bootstrap?.ApplicationProfile.AllowsVr == true)
                throw new NotSupportedException("Browser VR application profiles require desktop OpenXR/OpenVR leaves.");
            bootstrap?.InitializeRegistrations();
            stage = "load shader catalog";
            _shaderArtifacts = await _source.LoadShaderArtifactsAsync(token);
            _materialVariants = _source.LoadEngineMaterialVariants(_shaderArtifacts);
            WebComputeArtifactCatalog computeArtifacts = _source.LoadComputeArtifacts(_shaderArtifacts);
            _session = new BrowserEngineSession(PhysicsBackends);
            stage = "preload default UI font";
            FontGlyphSet? defaultUiFont = await _source.LoadDefaultUiFontAsync(token);
            if (defaultUiFont is not null)
                _session.InstallDefaultUiFont(defaultUiFont);
            stage = "load startup world";
            XRWorld world = await Engine.Assets.LoadFromRuntimeSourceAsync(
                _source.StartupWorldPath, typeof(XRWorld), cancellationToken: token) as XRWorld
                ?? throw new InvalidDataException("The cooked startup asset did not deserialize to XRWorld.");
            stage = "bind authored UI fonts";
            await _source.BindUiFontsAsync(world, token);
            _source.RegisterVerifiedWorldIdentity(world);
            AdmitMeshDeformation(world, computeArtifacts);
            stage = "load startup settings";
            GameStartupSettings cookedSettings;
            if (_source.StartupSettingsPath is { } settingsPath)
                cookedSettings = await Engine.Assets.LoadFromRuntimeSourceAsync(
                    settingsPath, typeof(GameStartupSettings), cancellationToken: token) as GameStartupSettings
                    ?? throw new InvalidDataException("The cooked startup settings asset did not deserialize to GameStartupSettings.");
            else
            {
                using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
                cookedSettings = BrowserEngineStartupPolicy.Instance.CreateDefaultGameSettings();
                StartupObjects.Add(publication.CompleteWithOwnership());
            }
            stage = "hydrate essential roots";
            await _source.PreloadEssentialAssetsAsync(token);
            stage = "configure game bootstrap";
            GameStartupSettings configuredSettings;
            GameState initialState;
            using (ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication())
            {
                GameStartupSettings browserCookedSettings = cookedSettings.DeepClone();
                browserCookedSettings.DefaultUserSettings.PhysicsLibrary = EPhysicsLibrary.Jolt;
                configuredSettings = bootstrap?.ConfigureStartup(browserCookedSettings) ?? browserCookedSettings;
                initialState = bootstrap?.CreateInitialGameState() ?? new GameState();
                StartupObjects.Add(publication.CompleteWithOwnership());
            }
            token.ThrowIfCancellationRequested();
            if (requestedEpoch != Volatile.Read(ref _epoch))
                throw new OperationCanceledException("Browser world startup was superseded.");

            stage = "start engine world";
            await _session.StartAsync(world, configuredSettings, initialState, token, canvasId, _shaderArtifacts,
                _materialVariants, _source.LoadTonemapArtifact(_shaderArtifacts), _source.LoadPipelineArtifacts(_shaderArtifacts),
                computeArtifacts, qualityPreset);
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
                throw new AggregateException($"Browser engine startup [{stage}] and rollback both failed. " +
                    $"Startup: {startupError}; rollback: {cleanupError}",
                    startupError, cleanupError);
            }
            if (startupError is OperationCanceledException)
                throw;
            throw new InvalidOperationException($"BrowserEngine.StartupFailed [{stage}]: {startupError}", startupError);
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    [JSExport]
    public static bool Step(double elapsedSeconds)
        => _loading?.IsCancellationRequested == true ? false : _session?.Step(elapsedSeconds) ?? false;

    [JSExport]
    public static int GetRendererSession() => _session?.RendererSession ?? 0;

    [JSExport]
    public static bool HasPresentedCanvasFrame() => _session?.HasPresentedCanvasFrame ?? false;

    /// <summary>Returns zero while preparing, one after a complete current-output submission, and minus one after a resource failure.</summary>
    [JSExport]
    public static int GetCanvasPreparationState() => _session?.CanvasPreparationState ?? -1;

    [JSExport]
    public static string GetCanvasRenderingStatus()
        => _session?.GetRenderingStatus() ?? "No active engine canvas session.";

    [JSExport]
    public static void InitializeCanvasGraphics(int session, string colorFormat)
        => (_session ?? throw new InvalidOperationException("WebGPU.EngineCanvas.Required: no active engine world."))
            .InitializeGraphics(session, colorFormat);

    [JSExport]
    public static void UpdateCanvasSurface(double logicalWidth, double logicalHeight, int physicalWidth,
        int physicalHeight, double pixelRatio, int generation, bool visible, bool focused, bool attached)
        => (_session ?? throw new InvalidOperationException("WebGPU.EngineCanvas.Required: no active engine world."))
            .UpdateSurface(new RuntimeSurfaceState(logicalWidth, logicalHeight, physicalWidth,
                physicalHeight, pixelRatio, generation, visible, focused, attached));

    [JSExport]
    public static void CanvasRendererFailed(int session, bool deviceLost) => _session?.RendererFailed(session, deviceLost);

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
    public static int RefreshTextInput() => _session?.RefreshTextInput() ?? 0;
    [JSExport]
    public static string GetTextInputValue() => _session?.TextInputValue ?? string.Empty;
    [JSExport]
    public static int GetTextInputContentVersion() => _session?.TextInputContentVersion ?? 0;
    [JSExport]
    public static string GetTextInputLabel() => _session?.TextInputLabel ?? string.Empty;
    [JSExport]
    public static int GetTextInputLabelVersion() => _session?.TextInputLabelVersion ?? 0;
    [JSExport]
    public static int GetTextInputCursor() => _session?.TextInputCursor ?? 0;
    [JSExport]
    public static bool GetTextInputSingleLine() => _session?.TextInputSingleLine ?? true;
    [JSExport]
    public static bool GetTextInputReadOnly() => _session?.TextInputReadOnly ?? true;
    [JSExport]
    public static float GetTextInputX() => _session?.TextInputX ?? -1;
    [JSExport]
    public static float GetTextInputY() => _session?.TextInputY ?? -1;
    [JSExport]
    public static float GetTextInputWidth() => _session?.TextInputWidth ?? 0;
    [JSExport]
    public static float GetTextInputHeight() => _session?.TextInputHeight ?? 0;
    [JSExport]
    public static bool EditTextInput(int generation, int expectedVersion, string value, int selectionStart, int selectionEnd)
        => _session?.EditTextInput(generation, expectedVersion, value, selectionStart, selectionEnd) ?? false;
    [JSExport]
    public static bool SelectTextInput(int generation, int expectedVersion, int cursor)
        => _session?.SelectTextInput(generation, expectedVersion, cursor) ?? false;
    [JSExport]
    public static bool ActOnTextInput(int generation, int expectedVersion, bool submit)
        => _session?.ActOnTextInput(generation, expectedVersion, submit) ?? false;
    [JSExport]
    public static int RefreshAccessibleControls() => _session?.RefreshAccessibleControls() ?? 0;
    [JSExport]
    public static int GetAccessibleControlCount() => _session?.AccessibleControlCount ?? 0;
    [JSExport]
    public static int GetAccessibleControlGeneration(int index) => _session?.AccessibleControlGeneration(index) ?? 0;
    [JSExport]
    public static int GetAccessibleControlVersion(int index) => _session?.AccessibleControlVersion(index) ?? 0;
    [JSExport]
    public static int GetAccessibleControlRole(int index) => _session?.AccessibleControlRole(index) ?? 0;
    [JSExport]
    public static string GetAccessibleControlName(int index) => _session?.AccessibleControlName(index) ?? string.Empty;
    [JSExport]
    public static bool GetAccessibleControlReadOnly(int index) => _session?.AccessibleControlReadOnly(index) ?? false;
    [JSExport]
    public static bool GetAccessibleControlMultiline(int index) => _session?.AccessibleControlMultiline(index) ?? false;
    [JSExport]
    public static int GetAccessibleControlChecked(int index) => _session?.AccessibleControlChecked(index) ?? 0;
    [JSExport]
    public static bool GetAccessibleControlFocused(int index) => _session?.AccessibleControlFocused(index) ?? false;
    [JSExport]
    public static float GetAccessibleControlX(int index) => _session?.AccessibleControlX(index) ?? -1;
    [JSExport]
    public static float GetAccessibleControlY(int index) => _session?.AccessibleControlY(index) ?? -1;
    [JSExport]
    public static float GetAccessibleControlWidth(int index) => _session?.AccessibleControlWidth(index) ?? 0;
    [JSExport]
    public static float GetAccessibleControlHeight(int index) => _session?.AccessibleControlHeight(index) ?? 0;
    [JSExport]
    public static bool FocusAccessibleControl(int generation)
        => _session?.FocusAccessibleControl(generation) ?? false;
    [JSExport]
    public static bool ActivateAccessibleControl(int generation)
        => _session?.ActivateAccessibleControl(generation) ?? false;
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
            try
            {
                Engine.Assets.UnbindRuntimeSource(source);
                _assetOwnerBound = false;
            }
            catch (Exception error)
            {
                // The source still owns objects whose cleanup failed. Preserve its
                // services and catalog so a later stop can retry their destruction.
                errors.Add(error);
                throw new AggregateException("Browser asset ownership release is incomplete.", errors);
            }
        }
        for (int index = StartupObjects.Count - 1; index >= 0; index--)
        {
            StartupObjects[index].Dispose();
            StartupObjects.RemoveAt(index);
        }
        XRObjectBase.ProcessPendingDestructions();
        IDisposable? registrations = _assetRegistrations;
        _assetRegistrations = null;
        if (registrations is not null)
            Capture(errors, registrations.Dispose);
        IDisposable? materialConstruction = _materialConstruction;
        _materialConstruction = null;
        if (materialConstruction is not null)
            Capture(errors, materialConstruction.Dispose);
        _shaderArtifacts = null;
        _materialVariants = null;
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
