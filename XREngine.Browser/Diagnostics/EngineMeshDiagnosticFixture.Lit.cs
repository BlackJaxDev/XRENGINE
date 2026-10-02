using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private DirectionalLightComponent? _directional;
    private PointLightComponent? _point;
    private SpotLightComponent? _spot;
    private ColorGradingSettings? _colorGrading;
    private long _initialShaderRevision;
    private int _litCase;

    private void InitializeLights()
    {
        _initialShaderRevision = _material.ShaderStateRevision;
        _directional = CreateLightNode("Directional engine light").AddComponent(static () => new DirectionalLightComponent { CastsShadows = false })
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.DirectionalLightMissing.");
        _point = CreateLightNode("Point engine light").AddComponent(static () => new PointLightComponent { CastsShadows = false })
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.PointLightMissing.");
        _spot = CreateLightNode("Spot engine light").AddComponent(static () => new SpotLightComponent { CastsShadows = false })
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.SpotLightMissing.");
        _directional.CastsShadows = _point.CastsShadows = _spot.CastsShadows = false;
        _directional.Color = _point.Color = _spot.Color = new ColorF3(0.7f, 0.5f, 0.3f);
        _point.Radius = 10;
        _point.Brightness = 3;
        _spot.Distance = 10;
        _spot.Brightness = 3;
        _spot.Exponent = 2;
        _spot.InnerCutoffAngleDegrees = 20;
        _spot.OuterCutoffAngleDegrees = 35;
        // The static scene activates components without beginning world play.
        // LightComponent registers into the attached render world's real lists.
        if (_renderWorld.Lights.DynamicDirectionalLights.Count != 1 ||
            _renderWorld.Lights.DynamicPointLights.Count != 1 || _renderWorld.Lights.DynamicSpotLights.Count != 1)
            throw new InvalidOperationException("EngineMeshDiagnostic.LightRegistration: expected one registered light of each type.");
    }

    private SceneNode CreateLightNode(string name)
    {
        SceneNode node = new(name);
        node.SetTransform<Transform>();
        _scene.RootNodes.Add(node);
        return node;
    }

    private void ConfigureLitCamera(XRCamera camera)
    {
        ((DefaultRenderPipeline)_pipeline).GlobalIlluminationMode = EGlobalIlluminationMode.None;
        camera.AntiAliasingModeOverride = EAntiAliasingMode.None;
        camera.OutputHDROverride = false;
        RuntimeEngine.Rendering.Settings.ForceMeshSubmissionStrategy = EMeshSubmissionStrategy.CpuDirect;
        PipelinePostProcessState state = camera.GetPostProcessState(_pipeline)
            ?? throw new InvalidOperationException("EngineMeshDiagnostic.PostProcessStateMissing.");
        RequireSettings<BloomSettings>(state).Enabled = false;
        RequireSettings<AmbientOcclusionSettings>(state).Enabled = false;
        RequireSettings<MotionBlurSettings>(state).Enabled = false;
        RequireSettings<DepthOfFieldSettings>(state).Enabled = false;
        RequireSettings<VolumetricFogSettings>(state).Enabled = false;
        RequireSettings<VignetteSettings>(state).Enabled = false;
        RequireSettings<ChromaticAberrationSettings>(state).Enabled = false;
        RequireSettings<FogSettings>(state).DepthFogIntensity = 0;
        RequireSettings<LensDistortionSettings>(state).Intensity = 0;
        _colorGrading = RequireSettings<ColorGradingSettings>(state);
        _colorGrading.AutoExposure = false;
        _colorGrading.ExposureMode = ColorGradingSettings.ExposureControlMode.Artist;
        _colorGrading.Exposure = 1;
        _colorGrading.Gamma = 2.2f;
        TonemappingSettings tonemap = RequireSettings<TonemappingSettings>(state);
        tonemap.Tonemapping = ETonemappingType.Mobius;
        tonemap.MobiusTransition = 0.6f;
    }

    private static T RequireSettings<T>(PipelinePostProcessState state) where T : class
        => state.GetStage<T>()?.TryGetBacking(out T? settings) == true && settings is not null ? settings
            : throw new InvalidOperationException($"EngineMeshDiagnostic.SettingsMissing: {typeof(T).Name}.");

    /// <summary>Mutates existing engine surface/light values without replacing a material or shader.</summary>
    public void SetLitCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_lit || _directional is null || _point is null || _spot is null || _colorGrading is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.LitCaseRequired.");
        if (sampleCase is < 0 or > 13)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _litCase = sampleCase;
        _material.Parameter<ShaderVector3>("BaseColor")!.Value = sampleCase == 5
            ? new Vector3(0.1f, 0.45f, 0.25f) : new Vector3(0.4f, 0.2f, 0.1f);
        _material.Parameter<ShaderFloat>("Roughness")!.Value = sampleCase == 6 ? 0.9f : 0.6f;
        _material.Parameter<ShaderFloat>("Metallic")!.Value = sampleCase == 7 ? 1 : 0.3f;
        _material.Parameter<ShaderFloat>("Specular")!.Value = sampleCase == 8 ? 0 : 0.5f;
        _material.Parameter<ShaderFloat>("Opacity")!.Value = sampleCase == 9 ? 0.35f : 0.7f;
        _material.Parameter<ShaderFloat>("Emission")!.Value = sampleCase == 10 ? 8 : 0.25f;
        _directional.DiffuseIntensity = sampleCase is 0 or 2 or 3 ? 0 : sampleCase == 12 ? 0.5f : 2;
        _point.DiffuseIntensity = sampleCase is 2 or 4 ? 2 : 0;
        _spot.DiffuseIntensity = sampleCase is 3 or 4 ? 2 : 0;
        _colorGrading.Exposure = sampleCase == 11 ? 0.25f : 1;
        if (_material.ShaderStateRevision != _initialShaderRevision || _material.Shaders.Count != 0)
            throw new InvalidOperationException("EngineMeshDiagnostic.ShaderIdentityChanged: numeric updates must retain source-free material identity.");
    }

    public string GetLitState()
    {
        if (!_lit)
            throw new InvalidOperationException("EngineMeshDiagnostic.LitCaseRequired.");
        return FormattableString.Invariant($"{{\"sampleCase\":{_litCase},\"shaderRevision\":{_material.ShaderStateRevision},\"initialShaderRevision\":{_initialShaderRevision},\"authoredShaderCount\":{_material.Shaders.Count},\"semantic\":\"StandardLitColorV1\",\"pipeline\":\"DefaultRenderPipeline\",\"directionalLights\":{_renderWorld.Lights.DynamicDirectionalLights.Count},\"pointLights\":{_renderWorld.Lights.DynamicPointLights.Count},\"spotLights\":{_renderWorld.Lights.DynamicSpotLights.Count},\"castsShadows\":false}}");
    }
}
