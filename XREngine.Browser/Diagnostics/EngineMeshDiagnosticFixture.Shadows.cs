using System.Numerics;
using System.Text.Json;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private XRMesh? _shadowCasterMesh;
    private Transform? _shadowCasterTransform;
    private Transform? _shadowLightTransform;
    private int _shadowCase;

    private static XRMesh CreateShadowReceiverMesh()
        => new(
            [new Vertex(new Vector3(-0.75f, -0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.75f, -0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.75f, 0.75f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(-0.75f, 0.75f, 0), Vector3.UnitZ)],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });

    private void InitializeShadowScene()
    {
        _material.Parameter<ShaderVector3>("BaseColor")!.Value = new Vector3(0.6f, 0.6f, 0.6f);
        _material.Parameter<ShaderFloat>("Roughness")!.Value = 0.8f;
        _material.Parameter<ShaderFloat>("Metallic")!.Value = 0;
        _material.Parameter<ShaderFloat>("Specular")!.Value = 0;
        _material.Parameter<ShaderFloat>("Opacity")!.Value = 1;
        _material.Parameter<ShaderFloat>("Emission")!.Value = 0;

        _shadowCasterMesh = new XRMesh(
            [new Vertex(new Vector3(-0.12f, -0.35f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.12f, -0.35f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(0.12f, 0.35f, 0), Vector3.UnitZ),
             new Vertex(new Vector3(-0.12f, 0.35f, 0), Vector3.UnitZ)],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });
        SceneNode casterNode = new("Directional shadow caster");
        _shadowCasterTransform = casterNode.SetTransform<Transform>();
        _shadowCasterTransform.Translation = new Vector3(0, 0, -1);
        _scene.RootNodes.Add(casterNode);
        ModelComponent caster = casterNode.AddComponent<ModelComponent>()
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.ShadowCasterMissing.");
        caster.Model = new Model(new SubMesh(_shadowCasterMesh, _material));
        _renderWorld.AddWorldObject(caster);

        SceneNode lightNode = CreateLightNode("Standalone directional shadow light");
        _shadowLightTransform = lightNode.Transform as Transform
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.ShadowLightTransformMissing.");
        _shadowLightTransform.Translation = new Vector3(0, 0, -1.5f);
        _shadowLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.35f);
        _directional = lightNode.AddComponent(static () => new DirectionalLightComponent
        {
            UseShadowAtlas = false,
            EnableCascadedShadows = false,
            EnableContactShadows = false,
        }) ?? throw new InvalidOperationException("EngineMeshDiagnostic.ShadowLightMissing.");
        // A long depth interval keeps the close 0.25-unit blocker gap below
        // PCSS's authored 0.015-UV maximum, while the 1.4-unit gap reaches it.
        // Both quads remain centered well inside the 100-unit shadow volume.
        _directional.Scale = new Vector3(4, 4, 100);
        _directional.SetShadowMapResolution(256, 256);
        _directional.Color = new ColorF3(1, 1, 1);
        _directional.DiffuseIntensity = 3;
        _directional.CastsShadows = true;
        if (_renderWorld.Lights.DynamicDirectionalLights.Count != 1 ||
            _renderWorld.Lights.DynamicPointLights.Count != 0 ||
            _renderWorld.Lights.DynamicSpotLights.Count != 0 ||
            _directional.ShadowCamera is not { DepthMode: XRCamera.EDepthMode.Normal })
            throw new InvalidOperationException("EngineMeshDiagnostic.ShadowLightRegistration: expected one normal-Z standalone light.");
    }

    private bool ShadowFrameReady()
        => _directional is { StandaloneShadowRenderPassCount: > 0, PrimaryShadowCasterCount: >= 1 } ||
           _directional is { CastsShadows: false };

    /// <summary>Changes authored scene state while preserving the cooked material variant and engine render path.</summary>
    public void SetShadowCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_shadow || _directional is null || _shadowCasterTransform is null || _shadowLightTransform is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.ShadowCaseRequired.");
        if (sampleCase is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _shadowCase = sampleCase;
        _shadowCasterTransform.Translation = new Vector3(sampleCase == 1 ? 0.35f : 0,
            0, sampleCase == 3 ? -1.75f : sampleCase == 4 ? -0.6f : -1);
        _shadowLightTransform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY,
            sampleCase == 2 ? -0.35f : 0.35f);
        _directional.CastsShadows = sampleCase != 5;
    }

    public void SetShadowMapSize(int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_shadow || _directional is null || size is not (256 or 512))
            throw new InvalidOperationException("EngineMeshDiagnostic.ShadowMapSizeRequired: select 256 or 512.");
        _directional.SetShadowMapResolution((uint)size, (uint)size);
    }

    public string GetShadowState()
    {
        if (!_shadow || _directional is null || _shadowCasterTransform is null || _shadowLightTransform is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.ShadowCaseRequired.");
        float angle = _shadowCase == 2 ? -0.35f : 0.35f;
        Vector3 caster = _shadowCasterTransform.Translation;
        // The ray from the caster to the receiver travels one unit in -Z and
        // tan(angle) units in -X for the authored Y-axis light rotation.
        float projectedX = caster.X - (caster.Z + 2) * MathF.Tan(angle);
        return JsonSerializer.Serialize(new
        {
            sampleCase = _shadowCase,
            castsShadows = _directional.CastsShadows,
            casterX = caster.X,
            casterZ = caster.Z,
            projectedShadowX = projectedX,
            mapWidth = _directional.ShadowMapResolutionWidth,
            mapHeight = _directional.ShadowMapResolutionHeight,
            shadowRequests = _directional.StandaloneShadowRenderRequestCount,
            shadowPasses = _directional.StandaloneShadowRenderPassCount,
            shadowCasters = _directional.PrimaryShadowCasterCount,
            directionalLights = _renderWorld.Lights.DynamicDirectionalLights.Count,
            pointLights = _renderWorld.Lights.DynamicPointLights.Count,
            spotLights = _renderWorld.Lights.DynamicSpotLights.Count,
            semantic = "StandardLitColorV1",
            pipeline = "DefaultRenderPipeline",
            shaderRevision = _material.ShaderStateRevision,
            authoredShaderCount = _material.Shaders.Count,
        });
    }

    private void DisposeShadowScene() => _shadowCasterMesh?.Destroy();

    private string GetShadowRenderStatus()
    {
        XRCamera? camera = _directional?.ShadowCamera;
        if (camera is null || camera.Viewports.Count == 0)
            return "shadow viewport=unavailable";
        XRViewport viewport = camera.Viewports[0];
        XRRenderPipelineInstance pipeline = viewport.RenderPipelineInstance;
        return $"shadow pipeline={pipeline.Pipeline?.GetType().Name ?? "none"}; " +
            $"shadow decline={pipeline.LastRenderDeclineReason ?? "none"}; " +
            $"shadow resource failure={pipeline.LastResourceGenerationFailure ?? "none"}; " +
            $"shadow world={viewport.World is not null}; shadow visual={viewport.World?.VisualScene is not null}; " +
            $"shadow camera={viewport.ActiveCamera is not null}; shadow recursion={RuntimeRenderingHostServices.BackendInterop.IsViewportCurrentlyRendering(viewport)}; " +
            $"shadow viewports={camera.Viewports.Count}; shadow commands={pipeline.Pipeline?.CommandChain.Count ?? -1}; " +
            $"shadow resource generation={pipeline.ResourceGeneration}; shadow command generation={pipeline.Pipeline?.CommandGeneration ?? 0}";
    }
}
