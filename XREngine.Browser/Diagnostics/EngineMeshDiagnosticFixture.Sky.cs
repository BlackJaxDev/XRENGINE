using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private SkyboxComponent? _skyComponent;
    private VisualScene3D? _skyVisual;
    private Transform? _skyCameraTransform;
    private int _skyCase;

    private void InitializeSkyScene()
    {
        _skyComponent = CreateLightNode("Gradient sky background").AddComponent(static () => new SkyboxComponent
        {
            Mode = ESkyboxMode.Gradient,
            TopColor = new Vector3(1.2f, 0.15f, 0.05f),
            BottomColor = new Vector3(0.05f, 0.2f, 0.9f),
            AutoCycle = false,
            SyncDirectionalLightWithSun = false,
            SyncDirectionalLightWithMoon = false,
            SyncGlobalAmbientLighting = false,
        }) ?? throw new InvalidOperationException("EngineMeshDiagnostic.SkyComponentMissing.");
        _renderWorld.AddWorldObject(_skyComponent);
    }

    private void ConfigureSkyCamera(XRCamera camera, Transform transform)
    {
        camera.Parameters = new XRPerspectiveCameraParameters(90, null, 0.1f, 10);
        _skyCameraTransform = transform;
        SetSkyCase(0);
    }

    /// <summary>Changes authored sky values and camera pose without replacing the scene.</summary>
    public void SetSkyCase(int sampleCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sky || _skyComponent is null || _skyCameraTransform is null)
            throw new InvalidOperationException("EngineMeshDiagnostic.SkyCaseRequired.");
        if (sampleCase is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyComponent.Mode = sampleCase == 5 ? ESkyboxMode.SolidColor : ESkyboxMode.Gradient;
        _skyComponent.Intensity = sampleCase == 4 ? 4 : 1;
        _skyCameraTransform.Translation = sampleCase == 1 ? new Vector3(100, 200, 300) : Vector3.Zero;
        _skyCameraTransform.Rotation = sampleCase switch
        {
            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6),
            3 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            _ => Quaternion.Identity,
        };
    }

    public string GetSkyState()
    {
        if (!_sky || _skyComponent?.Material is not { } material)
            throw new InvalidOperationException("EngineMeshDiagnostic.SkyCaseRequired.");
        return FormattableString.Invariant($"{{\"sampleCase\":{_skyCase},\"semantic\":\"{material.EngineSemantic.Semantic}\",\"semanticVersion\":{material.EngineSemantic.Version},\"authoredShaderCount\":{material.Shaders.Count},\"intensity\":{_skyComponent.Intensity},\"mode\":\"{_skyComponent.Mode}\",\"pipeline\":\"DefaultRenderPipeline\"}}");
    }
}
