using System.Numerics;
using System.Text.Json;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private XRMesh? _effectsOccluderMesh;
    private XRMesh? _effectsEmitterMesh;
    private XRMaterial? _effectsOccluderMaterial;
    private XRMaterial? _effectsEmitterMaterial;
    private AmbientOcclusionSettings? _effectsAo;
    private BloomSettings? _effectsBloom;
    private int _effectsCase;

    private void InitializeEffectsScene()
    {
        _material.Parameter<ShaderVector3>("BaseColor")!.Value = new Vector3(0.6f, 0.6f, 0.6f);
        _material.Parameter<ShaderFloat>("Roughness")!.Value = 0.8f;
        _material.Parameter<ShaderFloat>("Metallic")!.Value = 0;
        _material.Parameter<ShaderFloat>("Specular")!.Value = 0;
        _material.Parameter<ShaderFloat>("Opacity")!.Value = 1;
        _material.Parameter<ShaderFloat>("Emission")!.Value = 0;

        // Separate real ModelComponents make an occlusion edge and an HDR emission
        // island without a diagnostic-only renderer or an authored shader override.
        _effectsOccluderMesh = CreateEffectsQuad(0.16f);
        _effectsOccluderMaterial = XRMaterial.CreateColorMaterialDeferred(new ColorF4(0.25f, 0.25f, 0.25f, 1));
        ConfigureEffectsMaterial(_effectsOccluderMaterial, emission: 0);
        AddEffectsModel("GTAO foreground occluder", _effectsOccluderMesh, _effectsOccluderMaterial,
            new Vector3(-0.28f, 0, -1.72f));

        _effectsEmitterMesh = CreateEffectsQuad(0.08f);
        _effectsEmitterMaterial = XRMaterial.CreateColorMaterialDeferred(new ColorF4(1, 1, 1, 1));
        ConfigureEffectsMaterial(_effectsEmitterMaterial, emission: 12);
        AddEffectsModel("HDR bloom emitter", _effectsEmitterMesh, _effectsEmitterMaterial,
            new Vector3(0.42f, 0.12f, -1.65f));
    }

    private static XRMesh CreateEffectsQuad(float halfExtent)
        => new(
            [new Vertex(new Vector3(-halfExtent, -halfExtent, 0), Vector3.UnitZ),
             new Vertex(new Vector3(halfExtent, -halfExtent, 0), Vector3.UnitZ),
             new Vertex(new Vector3(halfExtent, halfExtent, 0), Vector3.UnitZ),
             new Vertex(new Vector3(-halfExtent, halfExtent, 0), Vector3.UnitZ)],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });

    private static void ConfigureEffectsMaterial(XRMaterial material, float emission)
    {
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.ExcludeFromCpuOcclusion = true;
        material.Parameter<ShaderFloat>("Roughness")!.Value = 0.8f;
        material.Parameter<ShaderFloat>("Metallic")!.Value = 0;
        material.Parameter<ShaderFloat>("Specular")!.Value = 0;
        material.Parameter<ShaderFloat>("Opacity")!.Value = 1;
        material.Parameter<ShaderFloat>("Emission")!.Value = emission;
    }

    private void AddEffectsModel(string name, XRMesh mesh, XRMaterial material, Vector3 translation)
    {
        SceneNode node = new(name);
        Transform transform = node.SetTransform<Transform>();
        transform.Translation = translation;
        _scene.RootNodes.Add(node);
        ModelComponent component = node.AddComponent<ModelComponent>()
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.EffectsComponentMissing.");
        component.Model = new Model(new SubMesh(mesh, material));
        _renderWorld.AddWorldObject(component);
    }

    private void ConfigureEffectsCamera(XRCamera camera)
    {
        _material.Parameter<ShaderVector3>("BaseColor")!.Value = new Vector3(0.6f, 0.6f, 0.6f);
        _material.Parameter<ShaderFloat>("Roughness")!.Value = 0.8f;
        _material.Parameter<ShaderFloat>("Metallic")!.Value = 0;
        _material.Parameter<ShaderFloat>("Specular")!.Value = 0;
        _material.Parameter<ShaderFloat>("Opacity")!.Value = 1;
        _material.Parameter<ShaderFloat>("Emission")!.Value = 0;
        PipelinePostProcessState state = camera.GetPostProcessState(_pipeline)
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.PostProcessStateMissing.");
        _effectsAo = RequireSettings<AmbientOcclusionSettings>(state);
        _effectsBloom = RequireSettings<BloomSettings>(state);
        SetEffectsCase(0);
    }

    /// <summary>Exercises numeric effect settings on the same lit models, shaders, and camera.</summary>
    public void SetEffectsCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_effects || _effectsAo is null || _effectsBloom is null || _directional is null ||
            _point is null || _spot is null || _effectsEmitterMaterial is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.EffectsCaseRequired.");
        if (sampleCase is < 0 or > 18)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _effectsCase = sampleCase;
        _effectsAo.Enabled = sampleCase is not (1 or 3 or 17 or 18);
        _effectsAo.Type = AmbientOcclusionSettings.EType.GroundTruthAmbientOcclusion;
        _effectsAo.Radius = sampleCase == 4 ? 0.18f : 2.2f;
        _effectsAo.Power = sampleCase == 5 ? 3.0f : 1.35f;
        _effectsAo.GroundTruth.MultiBounceEnabled = sampleCase != 6;
        _effectsBloom.Enabled = sampleCase is not (2 or 3);
        _effectsBloom.Threshold = sampleCase == 7 ? 100.0f : 0.138f;
        _effectsBloom.Strength = sampleCase == 8 ? 0.0f : 0.5805f;
        _effectsBloom.DebugBloomOnly = sampleCase == 9;
        _effectsBloom.Radius = sampleCase == 10 ? 3.0f : 1.495f;
        _directional.DiffuseIntensity = sampleCase is 11 or 17 ? 2.0f : 0.0f;
        _point.DiffuseIntensity = _spot.DiffuseIntensity = 0.0f;
        _effectsEmitterMaterial.Parameter<ShaderFloat>("Emission")!.Value =
            sampleCase is 16 or 18 ? 0.0f : 12.0f;
        _effectsOccluderMaterial!.RenderOptions.CullMode = sampleCase switch
        {
            13 => ECullMode.Front,
            14 => ECullMode.Both,
            15 => ECullMode.Back,
            _ => ECullMode.None,
        };
        _effectsOccluderMaterial.RenderOptions.Winding = sampleCase is 12 or 15
            ? EWinding.Clockwise : EWinding.CounterClockwise;
        if (_material.ShaderStateRevision != _initialShaderRevision || _material.Shaders.Count != 0 ||
            _effectsEmitterMaterial.Shaders.Count != 0)
            throw new InvalidOperationException("EngineMeshDiagnostic.ShaderIdentityChanged: numeric effect changes must retain source-free material identity.");
    }

    public string GetEffectsState()
    {
        if (!_effects || _effectsAo is null || _effectsBloom is null || _effectsEmitterMaterial is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.EffectsCaseRequired.");
        XRRenderPipelineInstance instance = _viewport.RenderPipelineInstance;
        return JsonSerializer.Serialize(new
        {
            sampleCase = _effectsCase,
            aoEnabled = _effectsAo.Enabled,
            aoRadius = _effectsAo.Radius,
            aoPower = _effectsAo.Power,
            multiBounce = _effectsAo.GroundTruth.MultiBounceEnabled,
            bloomEnabled = _effectsBloom.Enabled,
            bloomThreshold = _effectsBloom.Threshold,
            bloomStrength = _effectsBloom.Strength,
            bloomDebugOnly = _effectsBloom.DebugBloomOnly,
            bloomRadius = _effectsBloom.Radius,
            emitterEmission = _effectsEmitterMaterial.Parameter<ShaderFloat>("Emission")!.Value,
            occluderCull = _effectsOccluderMaterial?.RenderOptions.CullMode.ToString(),
            occluderWinding = _effectsOccluderMaterial?.RenderOptions.Winding.ToString(),
            shaderRevision = _material.ShaderStateRevision,
            initialShaderRevision = _initialShaderRevision,
            emitterShaderRevision = _effectsEmitterMaterial.ShaderStateRevision,
            authoredShaderCount = _material.Shaders.Count + _effectsEmitterMaterial.Shaders.Count,
            renderDraws = _renderer.LastEngineMeshDrawCount,
            resourceGeneration = instance.ResourceGeneration,
            normal = instance.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebNormalTextureName) is not null,
            gtaoRaw = instance.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebGtaoRawTextureName) is not null,
            gtaoHorizontal = instance.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebGtaoHorizontalTextureName) is not null,
            gtaoFinal = instance.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebGtaoFinalTextureName) is not null,
            bloomCombined = instance.GetTexture<XRTexture2D>(DefaultRenderPipeline.WebBloomCombinedTextureName) is not null,
            pipelineDecline = instance.LastRenderDeclineReason,
            resourceFailure = instance.LastResourceGenerationFailure,
        });
    }

    private void DisposeEffectsScene()
    {
        _effectsOccluderMesh?.Destroy();
        _effectsEmitterMesh?.Destroy();
        _effectsOccluderMaterial?.Destroy();
        _effectsEmitterMaterial?.Destroy();
    }
}
