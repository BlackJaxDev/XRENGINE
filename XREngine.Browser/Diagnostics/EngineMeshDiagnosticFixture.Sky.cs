using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    private readonly EngineSkyDiagnosticProfile _skyProfile;
    private XRTexture? _skyTexture;
    private SkyboxComponent? _skyComponent;
    private VisualScene3D? _skyVisual;
    private Transform? _skyCameraTransform;
    private int _skyCase;

    private void InitializeSkyScene()
    {
        _skyTexture = _skyProfile switch
        {
            EngineSkyDiagnosticProfile.Gradient => null,
            EngineSkyDiagnosticProfile.Cubemap => CreateSkyCube(),
            EngineSkyDiagnosticProfile.Equirectangular => CreateSkyImage(false),
            EngineSkyDiagnosticProfile.Octahedral => CreateSkyImage(true),
            _ => throw new ArgumentOutOfRangeException(nameof(_skyProfile)),
        };
        _skyComponent = CreateLightNode("Diagnostic sky background").AddComponent(() => new SkyboxComponent
        {
            Mode = _skyTexture is null ? ESkyboxMode.Gradient : ESkyboxMode.Texture,
            Projection = _skyProfile switch
            {
                EngineSkyDiagnosticProfile.Equirectangular => ESkyboxProjection.Equirectangular,
                EngineSkyDiagnosticProfile.Octahedral => ESkyboxProjection.Octahedral,
                _ => ESkyboxProjection.Cubemap,
            },
            Texture = _skyTexture,
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
        if (_skyProfile != EngineSkyDiagnosticProfile.Gradient)
        {
            SetSkyImageCase(sampleCase);
            return;
        }
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
        return FormattableString.Invariant($"{{\"sampleCase\":{_skyCase},\"semantic\":\"{material.EngineSemantic.Semantic}\",\"semanticVersion\":{material.EngineSemantic.Version},\"authoredShaderCount\":{material.Shaders.Count},\"intensity\":{_skyComponent.Intensity},\"mode\":\"{_skyComponent.Mode}\",\"projection\":\"{_skyComponent.Projection}\",\"rotation\":{_skyComponent.Rotation},\"pipeline\":\"DefaultRenderPipeline\"}}");
    }

    private void SetSkyImageCase(int sampleCase)
    {
        if (sampleCase is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(sampleCase));
        _skyCase = sampleCase;
        _skyComponent!.Intensity = sampleCase == 8 ? 2 : 1;
        _skyComponent.Rotation = sampleCase == 7 ? 90 : 0;
        _skyCameraTransform!.Translation = sampleCase == 6 ? new Vector3(100, 200, 300) : Vector3.Zero;
        _skyCameraTransform.Rotation = sampleCase switch
        {
            1 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2),
            2 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            3 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),
            4 => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            5 => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
            _ => Quaternion.Identity,
        };
    }
}
