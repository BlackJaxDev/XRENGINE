using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.WebGPU;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

/// <summary>
/// Static engine-object raster fixture. It does not begin play, install physics,
/// or represent production RuntimeWorld/browser gameplay acceptance.
/// </summary>
internal sealed partial class EngineMeshDiagnosticFixture : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly RuntimeSceneHost _scene = new();
    private readonly RuntimeWorldRenderer _renderWorld;
    private readonly XRViewport _viewport;
    private readonly XRMesh _mesh;
    private readonly XRMaterial _material;
    private readonly XRShader? _vertex;
    private readonly XRShader? _fragment;
    private readonly RenderPipeline _pipeline;
    private readonly bool _lit;
    private readonly bool _shadow;
    private readonly bool _debug;
    private readonly bool _effects;
    private XRTexture2D? _texture;
    private bool _disposed;

    public EngineMeshDiagnosticFixture(WebGpuRendererHost renderer, ShaderProgramArtifact artifact, uint width, uint height,
        ShaderProgramArtifact? tonemapArtifact = null, bool shadow = false, bool debug = false,
        WebPipelineArtifactCatalog? effectArtifacts = null)
    {
        if (!RuntimeWorkScheduler.IsCallerThread)
            throw new InvalidOperationException("EngineMeshDiagnostic.HostRequired: install the real caller-thread rendering host before constructing the fixture.");
        _ = RuntimeRenderingHostServices.Factories;
        bool textured = artifact.Pass == "texture-probe";
        _lit = tonemapArtifact is not null;
        _shadow = shadow;
        _debug = debug;
        _effects = effectArtifacts is not null;
        if (_effects && (shadow || debug))
            throw new ArgumentException("The effects fixture uses its own unshadowed lit scene.", nameof(effectArtifacts));
        if (_shadow && !_lit)
            throw new ArgumentException("A directional shadow fixture requires the cooked HDR presentation artifact.", nameof(tonemapArtifact));
        if ((_lit ? artifact.Pass != "opaque-forward" : artifact.Pass is not ("depth-probe" or "texture-probe")) ||
            artifact.VertexEntryPoint is null || artifact.FragmentEntryPoint is null)
            throw new ArgumentException("The fixture requires an explicitly cooked engine raster diagnostic artifact.", nameof(artifact));
        _renderer = renderer;
        if (_debug)
            _renderer.ConfigureEngineMeshResolutionTrace(true);
        // The shadow viewport captures its mesh-submission command chain when
        // the light activates, before the diagnostic camera is configured.
        if (_shadow)
            RuntimeEngine.Rendering.Settings.ForceMeshSubmissionStrategy = EMeshSubmissionStrategy.CpuDirect;
        using IDisposable suppressWrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        if (_lit)
        {
            _material = XRMaterial.CreateColorMaterialDeferred(new ColorF4(0.4f, 0.2f, 0.1f, 0.7f));
            _material.Name = "Mutable standard lit engine surface";
            _pipeline = new DefaultRenderPipeline { WebTonemapArtifact = tonemapArtifact };
            if (effectArtifacts is not null)
                ((DefaultRenderPipeline)_pipeline).BindWebPipelineArtifacts(effectArtifacts);
        }
        else
        {
            _vertex = new XRShader(EShaderType.Vertex)
            {
                Name = artifact.Name + " vertex", SourceLanguage = ShaderSourceLanguage.Slang,
                EntryPoint = artifact.VertexEntryPoint, CookedArtifact = artifact,
            };
            _fragment = new XRShader(EShaderType.Fragment)
            {
                Name = artifact.Name + " fragment", SourceLanguage = ShaderSourceLanguage.Slang,
                EntryPoint = artifact.FragmentEntryPoint, CookedArtifact = artifact,
            };
            _material = new XRMaterial(_vertex, _fragment)
            {
                Name = artifact.Name, RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                RenderOptions = new RenderingParameters
                {
                    CullMode = ECullMode.None,
                    ExcludeFromCpuOcclusion = true,
                    DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, UpdateDepth = true, Function = EComparison.Less },
                },
            };
            _pipeline = new EngineMeshDiagnosticPipeline(_material);
        }
        _material.RenderOptions.CullMode = ECullMode.None;
        _material.RenderOptions.ExcludeFromCpuOcclusion = true;
        if (textured)
        {
            _texture = CreateTexture(0);
            _material.Textures.Add(_texture);
        }
        _mesh = _shadow ? CreateShadowReceiverMesh() : _lit ? new XRMesh(
            [new Vertex(new Vector3(-0.75f, -0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.75f, -0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.75f, 0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(-0.75f, 0.75f, 0), Vector3.UnitZ)],
            new List<ushort> { 0, 1, 2, 0, 2, 3 }) : textured ? new XRMesh(
            [new Vertex(new Vector3(-0.35f, -0.35f, 0), new Vector2(0, 1)),
             new Vertex(new Vector3(0.35f, -0.35f, 0), new Vector2(1, 1)),
             new Vertex(new Vector3(0.35f, 0.35f, 0), new Vector2(1, 0)),
             new Vertex(new Vector3(-0.35f, 0.35f, 0), new Vector2(0, 0))],
            new List<ushort> { 0, 1, 2, 0, 2, 3 }) : new XRMesh(
            [new Vertex(new Vector3(-0.35f, -0.35f, 0)), new Vertex(new Vector3(0.35f, -0.35f, 0)), new Vertex(new Vector3(0, 0.35f, 0))],
            new List<ushort> { 0, 1, 2 });
        VisualScene3D visual = new();
        visual.ApplyRenderDispatchPreference(false);
        visual.SetBounds(new AABB(new Vector3(-2, -2, -5), new Vector3(2, 2, 1)));
        _renderWorld = new RuntimeWorldRenderer(_scene, visual);
        if (_lit)
        {
            AddModel(_shadow ? "Directional shadow receiver" : "Standard lit normal quad", new Vector3(0, 0, -2));
            if (_shadow) InitializeShadowScene();
            else InitializeLights();
            if (_debug) InitializeDebugScene();
            if (_effects) InitializeEffectsScene();
        }
        else
        {
            AddModel("Near left triangle", new Vector3(-0.45f, 0, -1));
            AddModel("Far right triangle", new Vector3(0.45f, 0, -3));
            AddModel("Occluded far left triangle", new Vector3(-0.45f, 0, -3));
        }

        SceneNode cameraNode = new("Engine mesh diagnostic camera");
        Transform cameraTransform = cameraNode.SetTransform<Transform>();
        _scene.RootNodes.Add(cameraNode);
        XROrthographicCameraParameters parameters = new(2, 2, 0, 4) { InheritAspectRatio = false };
        parameters.SetOriginCentered();
        XRCamera camera = new(cameraTransform, parameters)
        {
            RenderPipeline = _pipeline,
        };
        if (_lit)
        {
            ConfigureLitCamera(camera);
            if (!_shadow) SetLitCase(0);
            if (_effects) ConfigureEffectsCamera(camera);
        }
        if (_debug)
            camera.CullingMask = LayerMask.Everything;
        _viewport = new XRViewport(null, width, height)
        {
            Camera = camera,
            WorldInstanceOverride = _renderWorld,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
        };
        _renderer.ClearColor(new ColorF4(0.05f, 0.05f, 0.05f, 1));
        _renderer.ClearDepth(1);
        _renderer.BindEngineViewport(_viewport);
        _scene.SwapBuffers();
        // Register world publication before the viewport's standard collect/swap
        // callbacks so this static scene follows the production frame contract.
        Engine.Time.Timer.CollectVisible += CollectFrame;
        Engine.Time.Timer.SwapBuffers += SwapFrame;
        Engine.Time.Timer.RenderFrame += RenderFrame;
        try
        {
            _viewport.AutomaticallyCollectVisible = true;
            _viewport.AutomaticallySwapBuffers = true;
            Engine.Time.Timer.StartCallerThreadLoop();
        }
        catch
        {
            Engine.Time.Timer.CollectVisible -= CollectFrame;
            Engine.Time.Timer.SwapBuffers -= SwapFrame;
            Engine.Time.Timer.RenderFrame -= RenderFrame;
            _viewport.AutomaticallyCollectVisible = false;
            _viewport.AutomaticallySwapBuffers = false;
            throw;
        }
    }

    private void AddModel(string name, Vector3 translation)
    {
        SceneNode node = new(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = translation;
        _scene.RootNodes.Add(node);
        ModelComponent component = node.AddComponent<ModelComponent>()
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.ComponentMissing: ModelComponent registration failed.");
        component.Model = new Model(new SubMesh(_mesh, _material));
        // Static diagnostics do not activate gameplay. Use the production render-world
        // registration contract explicitly, leaving simulation/physics untouched.
        _renderWorld.AddWorldObject(component);
    }

    private static XRTexture2D CreateTexture(int sampleCase)
    {
        if (sampleCase is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(sampleCase));
        ReadOnlySpan<byte> pixels = sampleCase == 0
            ? [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255]
            : [128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255, 128, 128, 128, 255];
        return new XRTexture2D(2, 2, pixels)
        {
            Name = sampleCase == 0 ? "Engine UV corner probe" : "Engine sRGB midpoint probe",
            SizedInternalFormat = sampleCase == 0 ? ESizedInternalFormat.Rgba8 : ESizedInternalFormat.Srgb8Alpha8,
            AutoGenerateMipmaps = false, MinFilter = ETexMinFilter.Nearest, MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge, VWrap = ETexWrapMode.ClampToEdge,
            MaxAnisotropy = 1, MinLOD = 0, MaxLOD = 0,
        };
    }

    public void SetTextureCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_texture is null) throw new InvalidOperationException("EngineMeshDiagnostic.TextureCaseRequired.");
        XRTexture2D replacement = CreateTexture(sampleCase);
        XRTexture2D previous = _texture;
        _material.Textures[0] = replacement;
        _texture = replacement;
        previous.Destroy(now: true);
    }

    public bool Frame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Step the real caller-thread clock without beginning world play or physics.
        return Engine.Time.Timer.StepFrame(1.0 / 60.0) &&
            _renderer.IsBackendReplacementFrameReady && ((_shadow || _debug || _effects) ? _renderer.LastEngineMeshDrawCount >= 2 :
                _renderer.LastEngineMeshDrawCount == (_lit ? 2 : 3)) &&
            (!_shadow || ShadowFrameReady());
    }

    private void CollectFrame()
    {
        _scene.SwapBuffers();
        _renderWorld.GlobalPreCollectVisible();
        _renderWorld.GlobalCollectVisible();
    }

    private void SwapFrame() => _renderWorld.GlobalSwapBuffers();

    private void RenderFrame() => _renderer.RenderFrame(Engine.Time.Timer.Render.Delta);

    public string GetFrameStatus()
    {
        XRRenderPipelineInstance pipeline = _viewport.RenderPipelineInstance;
        return $"Draws={_renderer.LastEngineMeshDrawCount}; " +
            $"collected meshes={pipeline.ActiveMeshRenderCommands.GetRenderingMeshCommandCount()}; " +
            $"deferred={pipeline.ActiveMeshRenderCommands.HasRenderingCommands((int)EDefaultRenderPass.OpaqueDeferred)}; " +
            $"forward={pipeline.ActiveMeshRenderCommands.HasRenderingCommands((int)EDefaultRenderPass.OpaqueForward)}; " +
            $"HDR FBO={pipeline.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.ForwardPassFBOName) is not null}; " +
            (_shadow ? $"shadow requests={_directional?.StandaloneShadowRenderRequestCount}; shadow passes={_directional?.StandaloneShadowRenderPassCount}; shadow casters={_directional?.PrimaryShadowCasterCount}; {GetShadowRenderStatus()}; " : "") +
            (_debug ? $"debug callbacks={RuntimeEngine.Rendering.Debug.LastDebugDrawComponentCallbackCount}; case={_debugCase}; " : "") +
            (_effects ? $"effects case={_effectsCase}; AO target={pipeline.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebGtaoFinalTextureName) is not null}; bloom target={pipeline.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebBloomCombinedTextureName) is not null}; " : "") +
            $"pipeline decline={pipeline.LastRenderDeclineReason ?? "none"}; " +
            $"resource failure={pipeline.LastResourceGenerationFailure ?? "none"}.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Engine.Time.Timer.Stop();
        Engine.Time.Timer.CollectVisible -= CollectFrame;
        Engine.Time.Timer.SwapBuffers -= SwapFrame;
        Engine.Time.Timer.RenderFrame -= RenderFrame;
        _renderer.BindEngineViewport(null);
        RestoreDebugPreferences();
        _viewport.Destroy();
        _renderWorld.Dispose();
        _scene.Dispose();
        _mesh.Destroy();
        DisposeShadowScene();
        DisposeEffectsScene();
        _material.Destroy();
        _texture?.Destroy();
        _vertex?.Destroy();
        _fragment?.Destroy();
        _pipeline.Destroy();
    }
}
