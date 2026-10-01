using System.Runtime.InteropServices.JavaScript;
using System.Text;
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
    private static EngineMeshDiagnosticFixture? _fixture;
    private static RendererBackendCatalog? _catalog;
    private static IDisposable? _registration;
    private static IDisposable? _renderingServices;
    private static IDisposable? _startupPolicy;
    private static IDisposable? _capabilities;
    private static bool _engineInitialized;

    [JSExport]
    public static int Create(string canvasId, string descriptorJson, string wgsl)
    {
        if (_session != 0)
            throw new InvalidOperationException("EngineMeshDiagnostic.AlreadyActive: stop the existing diagnostic session first.");
        ShaderProgramArtifact artifact = ShaderProgramArtifactReader.Read(
            Encoding.UTF8.GetBytes(descriptorJson), Encoding.UTF8.GetBytes(wgsl));
        if (artifact.Pass != "depth-probe")
            throw new InvalidDataException("EngineMeshDiagnostic.ArtifactRequired: select the cooked depth-probe artifact.");
        try
        {
            _capabilities = RuntimeApplicationCapabilityServices.Install(new RuntimeApplicationCapabilities(
                IsConfigured: true, AllowsLocalInput: false, AllowsWindows: false,
                AllowsAudio: false, AllowsVr: false, AllowsRendererBackends: true));
            _startupPolicy = RuntimeEngineStartupPolicyServices.Install(BrowserEngineStartupPolicy.Instance);
            Engine.InitializeForCallerThread(BrowserEngineStartupPolicy.Instance.CreateDefaultGameSettings());
            _engineInitialized = true;
            _catalog = new RendererBackendCatalog();
            _registration = _catalog.Register(new WebGpuRendererBackendModule());
            _renderingServices = RuntimeCallerThreadRenderingBootstrap.Install(_catalog,
                new PhysicsBackendCatalog(), static () => throw new NotSupportedException(
                    "EngineMeshDiagnostic.PipelineRequired: every diagnostic camera must use its explicit engine pipeline."));
            _target = new BrowserCanvasRenderTarget(canvasId);
            _renderer = (WebGpuRendererHost)_catalog.CreateRequired(RuntimeGraphicsApiKind.WebGPU,
                new RendererBackendCreateContext(_target));
            _renderer.BindShaderArtifacts(new ShaderProgramArtifactCatalog([artifact]));
            _artifact = artifact;
            _session = checked(++_nextSession);
            return _session;
        }
        catch
        {
            StopCore();
            throw;
        }
    }

    [JSExport]
    public static void InitializeGraphics(int session, string colorFormat, int width, int height, int generation)
    {
        RequireSession(session);
        if (_fixture is not null)
            throw new InvalidOperationException("EngineMeshDiagnostic.GraphicsActive: the fixture is already initialized.");
        _target!.SetColorFormat(colorFormat);
        _target.UpdateSurface(new RuntimeSurfaceState(width, height, width, height, 1, generation, true, true, true));
        _renderer!.MarkReady(session);
        _renderer.Initialize();
        _fixture = new EngineMeshDiagnosticFixture(_renderer, _artifact!, checked((uint)width), checked((uint)height));
    }

    [JSExport]
    public static bool Frame(int session)
    {
        RequireSession(session);
        return _fixture?.Frame() ?? false;
    }

    [JSExport]
    public static void Stop(int session)
    {
        if (_session == 0) return;
        RequireSession(session);
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
        Attempt(() => _startupPolicy?.Dispose(), ref failures);
        _startupPolicy = null;
        Attempt(() => _capabilities?.Dispose(), ref failures);
        _capabilities = null;
        _target = null;
        _artifact = null;
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
