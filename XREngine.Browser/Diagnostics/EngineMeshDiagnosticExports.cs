using System.Runtime.InteropServices.JavaScript;
using System.Text;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.WebGPU;
using XREngine.Runtime.Bootstrap;
using XREngine.Scene.Physics;

namespace XREngine.Browser.Diagnostics;

/// <summary>Explicit page entry points for isolated engine rendering qualification without world play.</summary>
public static partial class EngineMeshDiagnosticExports
{
    private static int _nextSession = 0x60000000;
    private static int _session;
    private static BrowserCanvasRenderTarget? _target;
    private static WebGpuRendererHost? _renderer;
    private static ShaderProgramArtifact? _artifact;
    private static ShaderProgramArtifact? _tonemapArtifact;
    private static IDisposable? _materialConstruction;
    private static EngineMeshDiagnosticFixture? _fixture;
    private static RendererBackendCatalog? _catalog;
    private static IDisposable? _registration;
    private static IDisposable? _renderingServices;
    private static IDisposable? _startupPolicy;
    private static IDisposable? _capabilities;
    private static BrowserEngineAssetSource? _assetSource;
    private static IRuntimeAssetSource? _previousStorageSource;
    private static IAssetFileSystem? _previousFileSystem;
    private static bool _assetOwnerBound;
    private static bool _storageSourceInstalled;
    private static bool _fileSystemInstalled;
    private static bool _engineInitialized;
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private static CancellationTokenSource? _creationCancellation;
    private static int _creationEpoch;

    [JSExport]
    public static Task<int> CreateAsync(string canvasId, string assetManifestUrl, string descriptorJson, string wgsl)
        => CreateCoreAsync(canvasId, assetManifestUrl, descriptorJson, wgsl, null, null);

    [JSExport]
    public static Task<int> CreateLitAsync(string canvasId, string assetManifestUrl, string descriptorJson, string wgsl,
        string tonemapDescriptorJson, string tonemapWgsl)
        => CreateCoreAsync(canvasId, assetManifestUrl, descriptorJson, wgsl, tonemapDescriptorJson, tonemapWgsl);

    private static async Task<int> CreateCoreAsync(string canvasId, string assetManifestUrl, string descriptorJson, string wgsl,
        string? tonemapDescriptorJson, string? tonemapWgsl)
    {
        if (_session != 0)
            throw new InvalidOperationException("EngineMeshDiagnostic.AlreadyActive: stop the existing diagnostic session first.");
        int requestedEpoch = Interlocked.Increment(ref _creationEpoch);
        using CancellationTokenSource cancellation = new();
        Interlocked.Exchange(ref _creationCancellation, cancellation)?.Cancel();
        await CreateGate.WaitAsync();
        bool ownsStartup = false;
        string stage = "read shader artifact";
        try
        {
            if (_session != 0)
                throw new InvalidOperationException("EngineMeshDiagnostic.AlreadyActive: stop the existing diagnostic session first.");
            EnsureCurrentCreation(requestedEpoch, cancellation.Token);
            ownsStartup = true;
            ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(
                Encoding.UTF8.GetBytes(descriptorJson), Encoding.UTF8.GetBytes(wgsl));
            bool lit = tonemapDescriptorJson is not null && tonemapWgsl is not null;
            if (lit ? artifact.Pass != "opaque-forward" : artifact.Pass is not ("depth-probe" or "texture-probe"))
                throw new InvalidDataException("EngineMeshDiagnostic.ArtifactRequired: select a cooked engine raster diagnostic artifact.");
            ShaderProgramArtifact? tonemap = lit ? ShaderProgramArtifactReader.Read(
                Encoding.UTF8.GetBytes(tonemapDescriptorJson!), Encoding.UTF8.GetBytes(tonemapWgsl!)) : null;
            if (tonemap is not null && tonemap.Pass != "tonemap")
                throw new InvalidDataException("EngineMeshDiagnostic.TonemapRequired: select the cooked engine tonemap artifact.");
            stage = "open asset catalog";
            _assetSource = await BrowserEngineAssetSource.OpenAsync(assetManifestUrl, cancellation.Token);
            EnsureCurrentCreation(requestedEpoch, cancellation.Token);
            _previousStorageSource = DirectStorageIO.Source;
            DirectStorageIO.Source = _assetSource;
            _storageSourceInstalled = true;
            _previousFileSystem = AssetFileSystemServices.Current;
            AssetFileSystemServices.Current = _assetSource.FileSystem;
            _fileSystemInstalled = true;
            stage = "install capabilities";
            _capabilities = RuntimeApplicationCapabilityServices.Install(new RuntimeApplicationCapabilities(
                IsConfigured: true, AllowsLocalInput: false, AllowsWindows: false,
                AllowsAudio: false, AllowsVr: false, AllowsRendererBackends: true));
            stage = "install startup policy";
            _startupPolicy = RuntimeEngineStartupPolicyServices.Install(BrowserEngineStartupPolicy.Instance);
            stage = "initialize engine";
            Engine.InitializeForCallerThread(BrowserEngineStartupPolicy.Instance.CreateDefaultGameSettings());
            _engineInitialized = true;
            _materialConstruction = BrowserEngineMaterialConstruction.Install();
            Engine.Assets.BindRuntimeSource(_assetSource);
            _assetOwnerBound = true;
            stage = "register renderer";
            _catalog = new RendererBackendCatalog();
            _registration = _catalog.Register(new WebGpuRendererBackendModule());
            stage = "install rendering services";
            _renderingServices = RuntimeCallerThreadRenderingBootstrap.Install(_catalog,
                new PhysicsBackendCatalog(), static () => throw new NotSupportedException(
                    "EngineMeshDiagnostic.PipelineRequired: every diagnostic camera must use its explicit engine pipeline."));
            stage = "create renderer";
            _target = new BrowserCanvasRenderTarget(canvasId);
            _renderer = (WebGpuRendererHost)_catalog.CreateRequired(RuntimeGraphicsApiKind.WebGPU,
                new RendererBackendCreateContext(_target));
            stage = "bind shader artifact";
            ShaderProgramArtifactCatalog artifacts = new(tonemap is null ? [artifact] : [artifact, tonemap]);
            EngineMaterialVariantCatalog? variants = lit ? new EngineMaterialVariantCatalog(
                [new EngineMaterialVariantEntry(new EngineMaterialVariantKey(EngineMaterialSemanticIdentity.StandardLitColorV1,
                    ShaderCompileTarget.WebGPUWgsl, "opaque-forward", "static-position-normal-v1", "linear-hdr-v1"), artifact.Identity)], artifacts) : null;
            _renderer.BindShaderArtifacts(artifacts, variants);
            _artifact = artifact;
            _tonemapArtifact = tonemap;
            _session = checked(++_nextSession);
            return _session;
        }
        catch (Exception error)
        {
            Exception? cleanupError = null;
            if (ownsStartup)
                try { StopCore(); }
                catch (Exception exception) { cleanupError = exception; }
            string detail = cleanupError is null
                ? error.ToString()
                : $"{error}{Environment.NewLine}Rollback failure: {cleanupError}";
            throw new InvalidOperationException($"EngineMeshDiagnostic.CreateFailed [{stage}]: {detail}", error);
        }
        finally
        {
            Interlocked.CompareExchange(ref _creationCancellation, null, cancellation);
            CreateGate.Release();
        }
    }

    private static void EnsureCurrentCreation(int requestedEpoch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedEpoch != Volatile.Read(ref _creationEpoch))
            throw new OperationCanceledException("EngineMeshDiagnostic.ObsoleteCreation: a newer start or stop superseded this request.");
    }

    [JSExport]
    public static void CancelPendingCreate()
    {
        CancellationTokenSource? cancellation = Volatile.Read(ref _creationCancellation);
        if (cancellation is null)
            return;
        Interlocked.Increment(ref _creationEpoch);
        cancellation.Cancel();
    }

    [JSExport]
    public static void InitializeGraphics(int session, string colorFormat, int width, int height, int generation)
    {
        RequireSession(session);
        if (_fixture is not null)
            throw new InvalidOperationException("EngineMeshDiagnostic.GraphicsActive: the fixture is already initialized.");
        string stage = "configure surface";
        try
        {
            _target!.SetColorFormat(colorFormat);
            _target.UpdateSurface(new RuntimeSurfaceState(width, height, width, height, 1, generation, true, true, true));
            stage = "bind device capabilities";
            _renderer!.MarkReady(session);
            stage = "initialize renderer";
            _renderer.Initialize();
            stage = "construct engine fixture";
            _fixture = new EngineMeshDiagnosticFixture(_renderer, _artifact!, checked((uint)width), checked((uint)height), _tonemapArtifact);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"EngineMeshDiagnostic.InitializeGraphicsFailed [{stage}]: {error}", error);
        }
    }

    [JSExport]
    public static bool Frame(int session)
    {
        RequireSession(session);
        try
        {
            return _fixture?.Frame() ?? false;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"EngineMeshDiagnostic.FrameFailed: {error}", error);
        }
    }

    [JSExport]
    public static string GetFrameStatus(int session)
    {
        RequireSession(session);
        return _fixture?.GetFrameStatus() ?? "The engine fixture is not initialized.";
    }

    [JSExport]
    public static void SetTextureCase(int session, int sampleCase)
    {
        RequireSession(session);
        (_fixture ?? throw new InvalidOperationException("EngineMeshDiagnostic.FixtureRequired."))
            .SetTextureCase(sampleCase);
    }

    [JSExport]
    public static void SetLitCase(int session, int sampleCase)
    {
        RequireSession(session);
        (_fixture ?? throw new InvalidOperationException("EngineMeshDiagnostic.FixtureRequired.")).SetLitCase(sampleCase);
    }

    [JSExport]
    public static string GetLitState(int session)
    {
        RequireSession(session);
        return (_fixture ?? throw new InvalidOperationException("EngineMeshDiagnostic.FixtureRequired.")).GetLitState();
    }

    [JSExport]
    public static void ResizeGraphics(int session, int width, int height, int generation)
    {
        RequireSession(session);
        _ = _fixture ?? throw new InvalidOperationException("EngineMeshDiagnostic.FixtureRequired.");
        _target!.UpdateSurface(new RuntimeSurfaceState(width, height, width, height, 1, generation, true, true, true));
        _renderer!.SynchronizeEngineViewport(invalidateResources: true);
    }

    [JSExport]
    public static void Stop(int session)
    {
        if (_session != 0 && session != 0)
            RequireSession(session);
        CancelPendingCreate();
        if (_session == 0 || session == 0)
            return;
        StopCore();
    }

    private static void RequireSession(int session)
    {
        if (session <= 0 || session != _session)
            throw new InvalidOperationException("EngineMeshDiagnostic.ObsoleteSession: the diagnostic canvas no longer owns this session.");
    }

    private static void StopCore()
    {
        List<Exception>? failures = null;
        Attempt(() => _fixture?.Dispose(), ref failures);
        _fixture = null;
        Attempt(() => _renderer?.Dispose(), ref failures);
        _renderer = null;
        Attempt(() => _renderingServices?.Dispose(), ref failures);
        _renderingServices = null;
        Attempt(() => _registration?.Dispose(), ref failures);
        _registration = null;
        Attempt(() => _catalog?.Dispose(), ref failures);
        _catalog = null;
        if (_engineInitialized)
            Attempt(Engine.StopCallerThreadSession, ref failures);
        _engineInitialized = false;
        Attempt(() => _materialConstruction?.Dispose(), ref failures);
        _materialConstruction = null;
        if (_assetOwnerBound && _assetSource is { } source)
            Attempt(() => Engine.Assets.UnbindRuntimeSource(source), ref failures);
        _assetOwnerBound = false;
        Attempt(() => _startupPolicy?.Dispose(), ref failures);
        _startupPolicy = null;
        Attempt(() => _capabilities?.Dispose(), ref failures);
        _capabilities = null;
        if (_storageSourceInstalled && ReferenceEquals(DirectStorageIO.Source, _assetSource))
            DirectStorageIO.Source = _previousStorageSource;
        _previousStorageSource = null;
        _storageSourceInstalled = false;
        if (_fileSystemInstalled && ReferenceEquals(AssetFileSystemServices.Current, _assetSource?.FileSystem))
            AssetFileSystemServices.Current = _previousFileSystem;
        _previousFileSystem = null;
        _fileSystemInstalled = false;
        Attempt(() => _assetSource?.Dispose(), ref failures);
        _assetSource = null;
        _target = null;
        _artifact = null;
        _tonemapArtifact = null;
        _session = 0;
        if (failures is { Count: > 0 })
            throw new AggregateException("Engine mesh diagnostic teardown failed.", failures);
    }

    private static void Attempt(Action action, ref List<Exception>? failures)
    {
        try { action(); }
        catch (Exception error) { (failures ??= []).Add(error); }
    }
}
