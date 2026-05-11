using System.Numerics;
using XREngine.Components.Scene.Environment;

namespace XREngine.Rendering;

public sealed class AtmosphericScatteringSettings : PostProcessSettings
{
    public const string StructUniformName = "Atmosphere";
    public const int MaxCandidateCount = 4;

    internal static AtmosphericScatteringSettings Default { get; } = new();

    public enum EQualityMode
    {
        Low = 0,
        Balanced = 1,
        High = 2,
        Reference = 3,
    }

    public enum EDebugMode
    {
        Off = 0,
        ActiveMask = 1,
        RaySegment = 2,
        Altitude = 3,
        OpticalDepth = 4,
        Transmittance = 5,
        RayleighOnly = 6,
        MieOnly = 7,
        SunVisibility = 8,
        CameraInsideOutside = 9,
    }

    private readonly AtmosphericScatteringComponent?[] _activeAtmospheres = new AtmosphericScatteringComponent?[MaxCandidateCount];

    private bool _enabled = true;
    private bool _renderSky = true;
    private bool _aerialPerspective = true;
    private EQualityMode _quality = EQualityMode.Balanced;
    private int _viewSamples = 8;
    private int _opticalDepthSamples;
    private float _maxDistance = 200_000.0f;
    private float _jitterStrength = 0.5f;
    private bool _temporalEnabled = true;
    private EDebugMode _debugMode = EDebugMode.Off;
    private AtmosphericScatteringComponent? _lastActiveAtmosphere;
    private int _lastActiveAtmosphereRevision = -1;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool RenderSky
    {
        get => _renderSky;
        set => SetField(ref _renderSky, value);
    }

    public bool AerialPerspective
    {
        get => _aerialPerspective;
        set => SetField(ref _aerialPerspective, value);
    }

    public EQualityMode Quality
    {
        get => _quality;
        set => SetField(ref _quality, value);
    }

    public int ViewSamples
    {
        get => _viewSamples;
        set => SetField(ref _viewSamples, Math.Clamp(value, 1, 64));
    }

    /// <summary>
    /// Per-view-sample light-ray optical-depth samples. Zero uses the analytic scale approximation.
    /// </summary>
    public int OpticalDepthSamples
    {
        get => _opticalDepthSamples;
        set => SetField(ref _opticalDepthSamples, Math.Clamp(value, 0, 32));
    }

    public float MaxDistance
    {
        get => _maxDistance;
        set => SetField(ref _maxDistance, MathF.Max(0.0f, value));
    }

    public float JitterStrength
    {
        get => _jitterStrength;
        set => SetField(ref _jitterStrength, Math.Clamp(value, 0.0f, 1.0f));
    }

    public bool TemporalEnabled
    {
        get => _temporalEnabled;
        set => SetField(ref _temporalEnabled, value);
    }

    public EDebugMode DebugMode
    {
        get => _debugMode;
        set => SetField(ref _debugMode, value);
    }

    internal AtmosphericScatteringComponent? LastActiveAtmosphere => _lastActiveAtmosphere;

    internal int LastActiveAtmosphereRevision => _lastActiveAtmosphereRevision;

    internal bool SelectActiveAtmosphere(out AtmosphericScatteringComponent? active)
    {
        active = null;

        for (int i = 0; i < MaxCandidateCount; i++)
            _activeAtmospheres[i] = null;

        if (!Enabled || MaxDistance <= 0.0f)
            return false;

        var world = Engine.Rendering.State.RenderingWorld;
        if (world is null)
            return false;

        Vector3 cameraPosition = Engine.Rendering.State.RenderingPipelineState?.SceneCamera?.Transform.RenderTranslation
            ?? Engine.Rendering.State.RenderingCamera?.Transform.RenderTranslation
            ?? Vector3.Zero;

        int count = AtmosphericScatteringComponent.Registry.CopyActive(world, cameraPosition, _activeAtmospheres);
        if (count <= 0)
            return false;

        active = _activeAtmospheres[0];
        return active is not null;
    }

    internal static bool TrySelectActiveAtmosphereForCurrentFrame(out AtmosphericScatteringComponent? active)
    {
        active = null;

        var world = Engine.Rendering.State.RenderingWorld;
        if (world is null)
            return false;

        Vector3 cameraPosition = Engine.Rendering.State.RenderingPipelineState?.SceneCamera?.Transform.RenderTranslation
            ?? Engine.Rendering.State.RenderingCamera?.Transform.RenderTranslation
            ?? Vector3.Zero;

        return AtmosphericScatteringComponent.Registry.TryGetBestActive(world, cameraPosition, out active);
    }

    public override void SetUniforms(XRRenderProgram program)
    {
        AtmosphericScatteringComponent? active = null;
        bool selected = SelectActiveAtmosphere(out active);
        _lastActiveAtmosphere = active;
        _lastActiveAtmosphereRevision = active?.Revision ?? -1;

        if (selected && active is not null)
        {
            active.SetUniforms(program, this, selected: true);
            return;
        }

        AtmosphericScatteringComponent.SetDisabledUniforms(program, this);
    }
}
