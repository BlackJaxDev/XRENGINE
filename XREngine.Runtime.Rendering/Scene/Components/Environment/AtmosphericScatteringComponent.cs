using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Environment;

/// <summary>
/// Planetary atmosphere component that renders a sky background and contributes aerial perspective.
/// </summary>
[Serializable]
[Category("Rendering")]
[DisplayName("Atmospheric Scattering")]
[Description("Renders planetary atmospheric sky scattering and aerial perspective.")]
[XRComponentEditor("XREngine.Editor.ComponentEditors.AtmosphericScatteringComponentEditor")]
public sealed class AtmosphericScatteringComponent : XRComponent, IRenderable
{
    public enum ESunSource
    {
        PrimaryDirectionalLight,
        ExplicitDirectionalLight,
        DirectionOverride,
    }

    private const float MinPositiveRadius = 0.001f;
    private const float MinScaleHeight = 0.001f;
    private const float EarthGroundRadius = 6_371_000.0f;
    private const float EarthAtmosphereHeight = 100_000.0f;
    private const string UniformEnabled = AtmosphericScatteringSettings.StructUniformName + ".Enabled";
    private const string UniformRenderSky = AtmosphericScatteringSettings.StructUniformName + ".RenderSky";
    private const string UniformAerialPerspective = AtmosphericScatteringSettings.StructUniformName + ".AerialPerspective";
    private const string UniformQuality = AtmosphericScatteringSettings.StructUniformName + ".Quality";
    private const string UniformViewSamples = AtmosphericScatteringSettings.StructUniformName + ".ViewSamples";
    private const string UniformOpticalDepthSamples = AtmosphericScatteringSettings.StructUniformName + ".OpticalDepthSamples";
    private const string UniformMaxDistance = AtmosphericScatteringSettings.StructUniformName + ".MaxDistance";
    private const string UniformJitterStrength = AtmosphericScatteringSettings.StructUniformName + ".JitterStrength";
    private const string UniformTemporalEnabled = AtmosphericScatteringSettings.StructUniformName + ".TemporalEnabled";
    private const string UniformDebugMode = AtmosphericScatteringSettings.StructUniformName + ".DebugMode";
    private const string UniformPlanetCenter = AtmosphericScatteringSettings.StructUniformName + ".PlanetCenter";
    private const string UniformGroundRadius = AtmosphericScatteringSettings.StructUniformName + ".GroundRadius";
    private const string UniformAtmosphereHeight = AtmosphericScatteringSettings.StructUniformName + ".AtmosphereHeight";
    private const string UniformOuterRadius = AtmosphericScatteringSettings.StructUniformName + ".OuterRadius";
    private const string UniformSunDirection = AtmosphericScatteringSettings.StructUniformName + ".SunDirection";
    private const string UniformSunIntensity = AtmosphericScatteringSettings.StructUniformName + ".SunIntensity";
    private const string UniformSunColor = AtmosphericScatteringSettings.StructUniformName + ".SunColor";
    private const string UniformRayleighScaleHeight = AtmosphericScatteringSettings.StructUniformName + ".RayleighScaleHeight";
    private const string UniformMieScaleHeight = AtmosphericScatteringSettings.StructUniformName + ".MieScaleHeight";
    private const string UniformRayleighScattering = AtmosphericScatteringSettings.StructUniformName + ".RayleighScattering";
    private const string UniformMieScattering = AtmosphericScatteringSettings.StructUniformName + ".MieScattering";
    private const string UniformMieAnisotropy = AtmosphericScatteringSettings.StructUniformName + ".MieAnisotropy";
    private const string UniformExposureScale = AtmosphericScatteringSettings.StructUniformName + ".ExposureScale";
    private const string UniformGroundAlbedo = AtmosphericScatteringSettings.StructUniformName + ".GroundAlbedo";

    private readonly RenderCommandMesh3D _renderCommand;
    private readonly RenderInfo3D _renderInfo;

    private XRMesh? _mesh;
    private XRMeshRenderer? _meshRenderer;
    private XRMaterial? _material;

    private bool _enabled = true;
    private int _priority;
    private bool _renderSky = true;
    private bool _aerialPerspective = true;
    private float _groundRadius = EarthGroundRadius;
    private float _atmosphereHeight = EarthAtmosphereHeight;
    private float _groundLevelOffset;
    private ESunSource _sunSource = ESunSource.PrimaryDirectionalLight;
    private DirectionalLightComponent? _sunDirectionalLight;
    private Vector3 _sunDirectionOverride = Vector3.UnitY;
    private float _sunIntensity = 20.0f;
    private ColorF3 _sunColor = new(1.0f, 1.0f, 1.0f);
    private float _rayleighScaleHeight = 8_000.0f;
    private float _mieScaleHeight = 1_200.0f;
    private Vector3 _rayleighScattering = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
    private Vector3 _mieScattering = new(3.996e-6f);
    private float _mieAnisotropy = 0.76f;
    private float _exposureScale = 1.0f;
    private float _groundAlbedo = 0.30f;

    private float _cachedOuterRadius = EarthGroundRadius + EarthAtmosphereHeight;
    private Vector4 _cachedRadii = new(EarthGroundRadius, EarthGroundRadius + EarthAtmosphereHeight, EarthAtmosphereHeight, 0.0f);
    private Vector4 _cachedScaleHeights = new(8_000.0f, 1_200.0f, 0.76f, 1.0f);
    private Vector3 _cachedRayleighScattering = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
    private Vector3 _cachedMieScattering = new(3.996e-6f);
    private int _revision;
    private IRuntimeRenderWorld? _registeredWorld;

    public AtmosphericScatteringComponent()
    {
        _renderCommand = new RenderCommandMesh3D(EDefaultRenderPass.Background);
        _renderInfo = RenderInfo3D.New(this, _renderCommand);
        RenderedObjects = [_renderInfo];
        UpdateRenderVisibility();
    }

    [Browsable(false)]
    public RenderInfo[] RenderedObjects { get; }

    [Browsable(false)]
    public XRMaterial? Material => _material;

    [Browsable(false)]
    public float OuterRadius => _cachedOuterRadius;

    [Browsable(false)]
    public int Revision => _revision;

    [Browsable(false)]
    public bool HasRenderableAtmosphere
        => _enabled
        && IsActiveInHierarchy
        && _groundRadius > 0.0f
        && _atmosphereHeight > 0.0f;

    [Browsable(false)]
    public bool HasAerialPerspective
        => HasRenderableAtmosphere && _aerialPerspective;

    [Category("Atmosphere")]
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    [Category("Atmosphere")]
    public int Priority
    {
        get => _priority;
        set => SetField(ref _priority, value);
    }

    [Category("Atmosphere")]
    public bool RenderSky
    {
        get => _renderSky;
        set => SetField(ref _renderSky, value);
    }

    [Category("Atmosphere")]
    public bool AerialPerspective
    {
        get => _aerialPerspective;
        set => SetField(ref _aerialPerspective, value);
    }

    [Category("Atmosphere")]
    [DisplayName("Ground Radius")]
    [Description("Planet ground radius in world units. This is not derived from transform scale.")]
    public float GroundRadius
    {
        get => _groundRadius;
        set => SetField(ref _groundRadius, MathF.Max(MinPositiveRadius, value));
    }

    [Category("Atmosphere")]
    [DisplayName("Atmosphere Height")]
    public float AtmosphereHeight
    {
        get => _atmosphereHeight;
        set => SetField(ref _atmosphereHeight, MathF.Max(MinPositiveRadius, value));
    }

    [Category("Atmosphere")]
    [DisplayName("Ground Level Offset")]
    [Description("Additional offset above the authored ground point. The transform origin is treated as local ground level.")]
    public float GroundLevelOffset
    {
        get => _groundLevelOffset;
        set => SetField(ref _groundLevelOffset, value);
    }

    [Category("Atmosphere Sun")]
    public ESunSource SunSource
    {
        get => _sunSource;
        set => SetField(ref _sunSource, value);
    }

    [Category("Atmosphere Sun")]
    [DisplayName("Sun Directional Light")]
    public DirectionalLightComponent? SunDirectionalLight
    {
        get => _sunDirectionalLight;
        set => SetField(ref _sunDirectionalLight, value);
    }

    [Category("Atmosphere Sun")]
    [DisplayName("Sun Direction Override")]
    [Description("World-space direction from the atmosphere toward the sun.")]
    public Vector3 SunDirectionOverride
    {
        get => _sunDirectionOverride;
        set => SetField(ref _sunDirectionOverride, NormalizeOrDefault(value, Vector3.UnitY));
    }

    [Category("Atmosphere Sun")]
    public float SunIntensity
    {
        get => _sunIntensity;
        set => SetField(ref _sunIntensity, MathF.Max(0.0f, value));
    }

    [Category("Atmosphere Sun")]
    public ColorF3 SunColor
    {
        get => _sunColor;
        set => SetField(ref _sunColor, value);
    }

    [Category("Atmosphere Scattering")]
    [DisplayName("Rayleigh Scale Height")]
    public float RayleighScaleHeight
    {
        get => _rayleighScaleHeight;
        set => SetField(ref _rayleighScaleHeight, MathF.Max(MinScaleHeight, value));
    }

    [Category("Atmosphere Scattering")]
    [DisplayName("Mie Scale Height")]
    public float MieScaleHeight
    {
        get => _mieScaleHeight;
        set => SetField(ref _mieScaleHeight, MathF.Max(MinScaleHeight, value));
    }

    [Category("Atmosphere Scattering")]
    public Vector3 RayleighScattering
    {
        get => _rayleighScattering;
        set => SetField(ref _rayleighScattering, Max(value, Vector3.Zero));
    }

    [Category("Atmosphere Scattering")]
    public Vector3 MieScattering
    {
        get => _mieScattering;
        set => SetField(ref _mieScattering, Max(value, Vector3.Zero));
    }

    [Category("Atmosphere Scattering")]
    public float MieAnisotropy
    {
        get => _mieAnisotropy;
        set => SetField(ref _mieAnisotropy, Math.Clamp(value, -0.99f, 0.99f));
    }

    [Category("Atmosphere Scattering")]
    public float ExposureScale
    {
        get => _exposureScale;
        set => SetField(ref _exposureScale, MathF.Max(0.0f, value));
    }

    [Category("Atmosphere Scattering")]
    public float GroundAlbedo
    {
        get => _groundAlbedo;
        set => SetField(ref _groundAlbedo, Math.Clamp(value, 0.0f, 1.0f));
    }

    public Vector3 GetPlanetCenter()
    {
        Vector3 up = Transform is null ? Vector3.UnitY : NormalizeOrDefault(Transform.RenderUp, Vector3.UnitY);
        Vector3 origin = Transform?.RenderTranslation ?? Vector3.Zero;
        return origin - up * (_groundRadius + _groundLevelOffset);
    }

    public bool ContainsCamera(Vector3 cameraPosition)
    {
        Vector3 toCamera = cameraPosition - GetPlanetCenter();
        return toCamera.LengthSquared() <= _cachedOuterRadius * _cachedOuterRadius;
    }

    public float DistanceToAtmosphereShell(Vector3 cameraPosition)
    {
        float distanceFromCenter = (cameraPosition - GetPlanetCenter()).Length();
        return MathF.Max(0.0f, distanceFromCenter - _cachedOuterRadius);
    }

    internal void SetUniforms(XRRenderProgram program, AtmosphericScatteringSettings settings, bool selected)
    {
        bool enabled = selected && HasRenderableAtmosphere && settings.Enabled;
        bool renderSky = enabled && _renderSky && settings.RenderSky;
        bool aerialPerspective = enabled && _aerialPerspective && settings.AerialPerspective;
        Vector3 sunDirection = Vector3.UnitY;
        Vector3 sunColor = Vector3.One;
        ResolveSun(out sunDirection, out sunColor);

        program.Uniform(UniformEnabled, enabled);
        program.Uniform(UniformRenderSky, renderSky);
        program.Uniform(UniformAerialPerspective, aerialPerspective);
        program.Uniform(UniformQuality, (int)settings.Quality);
        program.Uniform(UniformViewSamples, settings.ViewSamples);
        program.Uniform(UniformOpticalDepthSamples, settings.OpticalDepthSamples);
        program.Uniform(UniformMaxDistance, enabled ? settings.MaxDistance : 0.0f);
        program.Uniform(UniformJitterStrength, enabled ? settings.JitterStrength : 0.0f);
        program.Uniform(UniformTemporalEnabled, enabled && settings.TemporalEnabled);
        program.Uniform(UniformDebugMode, (int)settings.DebugMode);
        program.Uniform(UniformPlanetCenter, GetPlanetCenter());
        program.Uniform(UniformGroundRadius, _cachedRadii.X);
        program.Uniform(UniformAtmosphereHeight, _cachedRadii.Z);
        program.Uniform(UniformOuterRadius, _cachedRadii.Y);
        program.Uniform(UniformSunDirection, sunDirection);
        program.Uniform(UniformSunIntensity, enabled ? _sunIntensity : 0.0f);
        program.Uniform(UniformSunColor, sunColor);
        program.Uniform(UniformRayleighScaleHeight, _cachedScaleHeights.X);
        program.Uniform(UniformMieScaleHeight, _cachedScaleHeights.Y);
        program.Uniform(UniformRayleighScattering, _cachedRayleighScattering);
        program.Uniform(UniformMieScattering, _cachedMieScattering);
        program.Uniform(UniformMieAnisotropy, _cachedScaleHeights.Z);
        program.Uniform(UniformExposureScale, enabled ? _exposureScale : 0.0f);
        program.Uniform(UniformGroundAlbedo, _groundAlbedo);
    }

    internal static void SetDisabledUniforms(XRRenderProgram program, AtmosphericScatteringSettings settings)
    {
        program.Uniform(UniformEnabled, false);
        program.Uniform(UniformRenderSky, false);
        program.Uniform(UniformAerialPerspective, false);
        program.Uniform(UniformQuality, (int)settings.Quality);
        program.Uniform(UniformViewSamples, settings.ViewSamples);
        program.Uniform(UniformOpticalDepthSamples, settings.OpticalDepthSamples);
        program.Uniform(UniformMaxDistance, 0.0f);
        program.Uniform(UniformJitterStrength, 0.0f);
        program.Uniform(UniformTemporalEnabled, false);
        program.Uniform(UniformDebugMode, (int)settings.DebugMode);
        program.Uniform(UniformPlanetCenter, Vector3.Zero);
        program.Uniform(UniformGroundRadius, EarthGroundRadius);
        program.Uniform(UniformAtmosphereHeight, EarthAtmosphereHeight);
        program.Uniform(UniformOuterRadius, EarthGroundRadius + EarthAtmosphereHeight);
        program.Uniform(UniformSunDirection, Vector3.UnitY);
        program.Uniform(UniformSunIntensity, 0.0f);
        program.Uniform(UniformSunColor, Vector3.One);
        program.Uniform(UniformRayleighScaleHeight, 8_000.0f);
        program.Uniform(UniformMieScaleHeight, 1_200.0f);
        program.Uniform(UniformRayleighScattering, new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f));
        program.Uniform(UniformMieScattering, new Vector3(3.996e-6f));
        program.Uniform(UniformMieAnisotropy, 0.76f);
        program.Uniform(UniformExposureScale, 0.0f);
        program.Uniform(UniformGroundAlbedo, 0.30f);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RebuildAll();
        RefreshRegistration();
    }

    protected override void OnComponentDeactivated()
    {
        Unregister();
        CleanupResources();
        base.OnComponentDeactivated();
    }

    protected override void OnTransformChanged()
    {
        base.OnTransformChanged();
        IncrementRevision();
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        switch (propName)
        {
            case nameof(World):
            case nameof(IsActive):
            case nameof(Enabled):
                RefreshRegistration();
                UpdateRenderVisibility();
                IncrementRevision();
                break;
            case nameof(RenderSky):
                UpdateRenderVisibility();
                IncrementRevision();
                break;
            case nameof(Priority):
            case nameof(AerialPerspective):
            case nameof(GroundRadius):
            case nameof(AtmosphereHeight):
            case nameof(GroundLevelOffset):
            case nameof(SunSource):
            case nameof(SunDirectionalLight):
            case nameof(SunDirectionOverride):
            case nameof(SunIntensity):
            case nameof(SunColor):
            case nameof(RayleighScaleHeight):
            case nameof(MieScaleHeight):
            case nameof(RayleighScattering):
            case nameof(MieScattering):
            case nameof(MieAnisotropy):
            case nameof(ExposureScale):
            case nameof(GroundAlbedo):
                RecomputePrecomputedState();
                IncrementRevision();
                break;
        }
    }

    protected override void OnDestroying()
    {
        Unregister();
        CleanupResources();
        base.OnDestroying();
    }

    private void RebuildAll()
    {
        RebuildMesh();
        RebuildMaterial();
        UpdateRenderCommand();
        UpdateRenderVisibility();
    }

    private void RebuildMesh()
    {
        _mesh?.Destroy();

        VertexTriangle triangle = new(
            new Vertex(new Vector3(-1, -1, 0)),
            new Vertex(new Vector3(3, -1, 0)),
            new Vertex(new Vector3(-1, 3, 0)));

        _mesh = XRMesh.Create(triangle);
        _meshRenderer?.Mesh = _mesh;
    }

    private void RebuildMaterial()
    {
        _material?.SettingUniforms -= SetSkyUniforms;

        XRShader vertexShader = XRShader.EngineShader(Path.Combine("Scene3D", "Skybox.vs"), EShaderType.Vertex);
        XRShader fragmentShader = XRShader.EngineShader(Path.Combine("Scene3D", "Atmosphere", "AtmosphereSky.fs"), EShaderType.Fragment);

        RenderingParameters renderParams = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new DepthTest()
            {
                Enabled = ERenderParamUsage.Enabled,
                UpdateDepth = false,
                Function = EComparison.Lequal,
            },
            RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.RenderTime,
            ExcludeFromGpuIndirect = true,
        };

        _material = new XRMaterial(vertexShader, fragmentShader)
        {
            RenderPass = (int)EDefaultRenderPass.Background,
            RenderOptions = renderParams,
        };
        _material.SettingUniforms += SetSkyUniforms;
        _renderCommand.RenderPass = _material.RenderPass;

        if (_meshRenderer is null)
            _meshRenderer = new XRMeshRenderer(_mesh, _material);
        else
            _meshRenderer.Material = _material;
    }

    private void SetSkyUniforms(XRMaterialBase material, XRRenderProgram program)
    {
        var state = Engine.Rendering.State.RenderingPipelineState?.SceneCamera?.GetActivePostProcessState();
        AtmosphericScatteringSettings settings = state?.GetStage<AtmosphericScatteringSettings>()?.TryGetBacking(out AtmosphericScatteringSettings? backing) == true && backing is not null
            ? backing
            : AtmosphericScatteringSettings.Default;
        bool selected = settings.Enabled && settings.SelectActiveAtmosphere(out var active) && ReferenceEquals(active, this);
        SetUniforms(program, settings, selected);
    }

    private void UpdateRenderCommand()
    {
        if (_meshRenderer is null)
            return;

        _renderCommand.Mesh = _meshRenderer;
        _renderCommand.WorldMatrix = Matrix4x4.Identity;
    }

    private void UpdateRenderVisibility()
        => _renderInfo.IsVisible = _enabled && _renderSky;

    private void CleanupResources()
    {
        _material?.SettingUniforms -= SetSkyUniforms;
        _mesh?.Destroy();
        _mesh = null;
        _material = null;
        _meshRenderer = null;
    }

    private void RefreshRegistration()
    {
        var world = WorldAs<IRuntimeRenderWorld>();
        bool shouldRegister = world is not null && IsActiveInHierarchy && _enabled;

        if (_registeredWorld is not null && (!shouldRegister || !ReferenceEquals(_registeredWorld, world)))
            Unregister();

        if (shouldRegister && _registeredWorld is null && world is not null)
        {
            Registry.Register(world, this);
            _registeredWorld = world;
        }
    }

    private void Unregister()
    {
        if (_registeredWorld is null)
            return;

        Registry.Unregister(_registeredWorld, this);
        _registeredWorld = null;
    }

    private void RecomputePrecomputedState()
    {
        _cachedOuterRadius = _groundRadius + _atmosphereHeight;
        _cachedRadii = new Vector4(_groundRadius, _cachedOuterRadius, _atmosphereHeight, _groundLevelOffset);
        _cachedScaleHeights = new Vector4(_rayleighScaleHeight, _mieScaleHeight, _mieAnisotropy, _exposureScale);
        _cachedRayleighScattering = _rayleighScattering;
        _cachedMieScattering = _mieScattering;
    }

    private void IncrementRevision()
        => _revision = unchecked(_revision + 1);

    private void ResolveSun(out Vector3 directionToSun, out Vector3 color)
    {
        DirectionalLightComponent? light = _sunSource switch
        {
            ESunSource.PrimaryDirectionalLight => ResolvePrimaryDirectionalLight(),
            ESunSource.ExplicitDirectionalLight => _sunDirectionalLight,
            _ => null,
        };

        if (light is not null && light.IsActiveInHierarchy)
        {
            directionToSun = NormalizeOrDefault(-light.Transform.RenderForward, _sunDirectionOverride);
            Vector3 lightColor = light.Color;
            color = Max(lightColor, Vector3.Zero) * MathF.Max(0.0f, light.DiffuseIntensity);
            return;
        }

        directionToSun = NormalizeOrDefault(_sunDirectionOverride, Vector3.UnitY);
        color = Max((Vector3)_sunColor, Vector3.Zero);
    }

    private DirectionalLightComponent? ResolvePrimaryDirectionalLight()
    {
        var world = WorldAs<IRuntimeRenderWorld>();
        var lights = world?.Lights.DynamicDirectionalLights;
        if (lights is null || lights.Count == 0)
            return null;

        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (light.IsActiveInHierarchy)
                return light;
        }

        return null;
    }

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback)
    {
        float lengthSq = value.LengthSquared();
        return lengthSq > 1e-8f ? value / MathF.Sqrt(lengthSq) : fallback;
    }

    private static Vector3 Max(Vector3 value, Vector3 min)
        => new(MathF.Max(value.X, min.X), MathF.Max(value.Y, min.Y), MathF.Max(value.Z, min.Z));

    internal static class Registry
    {
        private static readonly Dictionary<IRuntimeRenderWorld, List<AtmosphericScatteringComponent>> s_perWorld = new();
        private static readonly object s_lock = new();

        public static void Register(IRuntimeRenderWorld world, AtmosphericScatteringComponent component)
        {
            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                {
                    list = [];
                    s_perWorld[world] = list;
                }

                if (!list.Contains(component))
                    list.Add(component);
            }
        }

        public static void Unregister(IRuntimeRenderWorld world, AtmosphericScatteringComponent component)
        {
            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                    return;

                list.Remove(component);
                if (list.Count == 0)
                    s_perWorld.Remove(world);
            }
        }

        public static bool TryGetBestActive(
            IRuntimeRenderWorld world,
            Vector3 cameraPosition,
            out AtmosphericScatteringComponent? component)
        {
            component = null;

            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                    return false;

                for (int i = 0; i < list.Count; i++)
                {
                    var candidate = list[i];
                    if (!candidate.HasRenderableAtmosphere)
                        continue;

                    if (component is null || CandidatePrecedes(candidate, component, cameraPosition))
                        component = candidate;
                }
            }

            return component is not null;
        }

        public static int CopyActive(
            IRuntimeRenderWorld world,
            Vector3 cameraPosition,
            Span<AtmosphericScatteringComponent?> destination)
        {
            destination.Clear();

            lock (s_lock)
            {
                if (!s_perWorld.TryGetValue(world, out var list))
                    return 0;

                int count = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var candidate = list[i];
                    if (!candidate.HasRenderableAtmosphere)
                        continue;

                    int insertIndex = 0;
                    while (insertIndex < count
                        && destination[insertIndex] is { } existing
                        && !CandidatePrecedes(candidate, existing, cameraPosition))
                    {
                        insertIndex++;
                    }

                    if (insertIndex >= destination.Length)
                        continue;

                    int lastIndex = Math.Min(count, destination.Length - 1);
                    for (int shift = lastIndex; shift > insertIndex; shift--)
                        destination[shift] = destination[shift - 1];

                    destination[insertIndex] = candidate;
                    if (count < destination.Length)
                        count++;
                }

                return count;
            }
        }

        internal static void ClearForTests()
        {
            lock (s_lock)
                s_perWorld.Clear();
        }

        private static bool CandidatePrecedes(
            AtmosphericScatteringComponent candidate,
            AtmosphericScatteringComponent existing,
            Vector3 cameraPosition)
        {
            bool candidateContainsCamera = candidate.ContainsCamera(cameraPosition);
            bool existingContainsCamera = existing.ContainsCamera(cameraPosition);
            if (candidateContainsCamera != existingContainsCamera)
                return candidateContainsCamera;

            if (candidate.Priority != existing.Priority)
                return candidate.Priority > existing.Priority;

            float candidateDistance = candidate.DistanceToAtmosphereShell(cameraPosition);
            float existingDistance = existing.DistanceToAtmosphereShell(cameraPosition);
            if (MathF.Abs(candidateDistance - existingDistance) > 1e-4f)
                return candidateDistance < existingDistance;

            return false;
        }
    }
}
