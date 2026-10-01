using System.Diagnostics;
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
/// Static engine-object depth fixture. It does not begin play, install physics,
/// or represent production RuntimeWorld/browser gameplay acceptance.
/// </summary>
internal sealed class EngineMeshDiagnosticFixture : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly RuntimeSceneHost _scene = new();
    private readonly RuntimeWorldRenderer _renderWorld;
    private readonly XRViewport _viewport;
    private readonly XRMesh _mesh;
    private readonly XRMaterial _material;
    private readonly XRShader _vertex;
    private readonly XRShader _fragment;
    private readonly EngineMeshDiagnosticPipeline _pipeline;
    private bool _disposed;

    public EngineMeshDiagnosticFixture(WebGpuRendererHost renderer, ShaderProgramArtifact artifact, uint width, uint height)
    {
        if (!RuntimeWorkScheduler.IsCallerThread)
            throw new InvalidOperationException("EngineMeshDiagnostic.HostRequired: install the real caller-thread rendering host before constructing the fixture.");
        _ = RuntimeRenderingHostServices.Factories;
        if (artifact.Pass != "depth-probe" || artifact.VertexEntryPoint is null || artifact.FragmentEntryPoint is null)
            throw new ArgumentException("The fixture requires the explicitly cooked depth-probe diagnostic artifact.", nameof(artifact));
        _renderer = renderer;
        using IDisposable suppressWrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        _vertex = new XRShader(EShaderType.Vertex)
        {
            Name = "Engine depth probe vertex", SourceLanguage = ShaderSourceLanguage.Slang,
            EntryPoint = artifact.VertexEntryPoint, CookedArtifact = artifact,
        };
        _fragment = new XRShader(EShaderType.Fragment)
        {
            Name = "Engine depth probe fragment", SourceLanguage = ShaderSourceLanguage.Slang,
            EntryPoint = artifact.FragmentEntryPoint, CookedArtifact = artifact,
        };
        _material = new XRMaterial(_vertex, _fragment)
        {
            Name = "Engine depth probe", RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            RenderOptions = new RenderingParameters
            {
                CullMode = ECullMode.None,
                ExcludeFromCpuOcclusion = true,
                DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, UpdateDepth = true, Function = EComparison.Less },
            },
        };
        _pipeline = new EngineMeshDiagnosticPipeline(_material);
        _mesh = new XRMesh(
            [new Vertex(new Vector3(-0.35f, -0.35f, 0)), new Vertex(new Vector3(0.35f, -0.35f, 0)), new Vertex(new Vector3(0, 0.35f, 0))],
            new List<ushort> { 0, 1, 2 });
        VisualScene3D visual = new();
        visual.ApplyRenderDispatchPreference(false);
        visual.SetBounds(new AABB(new Vector3(-2, -2, -5), new Vector3(2, 2, 1)));
        _renderWorld = new RuntimeWorldRenderer(_scene, visual);
        AddModel("Near left triangle", new Vector3(-0.45f, 0, -1));
        AddModel("Far right triangle", new Vector3(0.45f, 0, -3));
        AddModel("Occluded far left triangle", new Vector3(-0.45f, 0, -3));

        SceneNode cameraNode = new("Engine depth diagnostic camera");
        Transform cameraTransform = cameraNode.SetTransform<Transform>();
        _scene.RootNodes.Add(cameraNode);
        XRCamera camera = new(cameraTransform,
            new XROrthographicCameraParameters(2, 2, 0, 4) { InheritAspectRatio = false })
        {
            RenderPipeline = _pipeline,
        };
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

    public bool Frame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RuntimeWorkScheduler.Jobs.ProcessCallerThreadJobs(maxJobs: 64, budgetMilliseconds: 2);
        _scene.SwapBuffers();
        ulong frame = RuntimeEngine.Rendering.BeginRenderFrame();
        long start = Stopwatch.GetTimestamp();
        try
        {
            _renderWorld.GlobalPreCollectVisible();
            _renderWorld.GlobalCollectVisible();
            _viewport.CollectVisible(collectMirrors: false, allowScreenSpaceUICollectVisible: false);
            _renderWorld.GlobalSwapBuffers(frame);
            _viewport.SwapBuffers();
            _renderer.RenderFrame(0);
            return _renderer.IsBackendReplacementFrameReady && _renderer.LastEngineMeshDrawCount == 3;
        }
        finally
        {
            RuntimeEngine.Rendering.CompleteRenderFrame(frame, Stopwatch.GetTimestamp() - start);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.BindEngineViewport(null);
        _viewport.Destroy();
        _renderWorld.Dispose();
        _scene.Dispose();
        _mesh.Destroy();
        _material.Destroy();
        _vertex.Destroy();
        _fragment.Destroy();
        _pipeline.Destroy();
    }
}
