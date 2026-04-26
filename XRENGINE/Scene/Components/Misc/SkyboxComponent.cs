using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    /// <summary>
    /// Specifies the projection type used to interpret the skybox texture.
    /// </summary>
    public enum ESkyboxProjection
    {
        /// <summary>
        /// Equirectangular projection (2:1 aspect ratio panorama).
        /// Uses a 2D texture with spherical UV mapping.
        /// </summary>
        Equirectangular,

        /// <summary>
        /// Octahedral projection (1:1 aspect ratio).
        /// Uses a 2D texture with octahedral UV mapping.
        /// </summary>
        Octahedral,

        /// <summary>
        /// Standard cubemap with 6 faces.
        /// Uses a cube texture sampled by direction.
        /// </summary>
        Cubemap,

        /// <summary>
        /// Cubemap array for layered environment maps.
        /// Uses a cube array texture with a layer index.
        /// </summary>
        CubemapArray
    }

    /// <summary>
    /// Specifies how the skybox is rendered.
    /// </summary>
    public enum ESkyboxMode
    {
        /// <summary>
        /// Renders the skybox by sampling the provided texture using the selected projection.
        /// </summary>
        Texture,

        /// <summary>
        /// Renders a Y-aligned world-space gradient using two colors.
        /// </summary>
        Gradient,

        /// <summary>
        /// Renders a solid color skybox.
        /// </summary>
        SolidColor,

        /// <summary>
        /// Renders a procedural sky with day/night cycle, sun, moon, atmospheric scattering, and animated clouds.
        /// </summary>
        DynamicProcedural,
    }

    /// <summary>
    /// Renders a skybox background as a fullscreen quad that samples the environment texture
    /// using the camera's view direction and frustum. Supports equirectangular, octahedral,
    /// cubemap, and cubemap array textures.
    /// </summary>
    [Serializable]
    [Category("Rendering")]
    [DisplayName("Skybox")]
    [Description("Renders a skybox background using equirectangular, octahedral, cubemap, or cubemap array textures.")]
    public class SkyboxComponent : XRComponent, IRenderable
    {
        private RenderInfo3D? _renderInfo;
        private RenderCommandMesh3D? _renderCommand;
        private XRMeshRenderer? _meshRenderer;
        private XRMaterial? _material;
        private XRMesh? _mesh;

        private bool _debugHooksAttached;
        private bool _loggedActivated;
        private bool _loggedCollected;
        private bool _loggedRendered;

        private ESkyboxMode _mode = ESkyboxMode.Texture;
        private ESkyboxProjection _projection = ESkyboxProjection.Equirectangular;
        private XRTexture? _texture;
        private float _intensity = 1.0f;
        private float _rotation = 0.0f;
        private int _cubemapArrayLayer = 0;

        private Vector3 _topColor = new(0.52f, 0.74f, 1.0f);
        private Vector3 _bottomColor = new(0.05f, 0.06f, 0.08f);
        private bool _autoCycle = true;
        private float _timeOfDay = 0.25f;
        private float _dayLengthSeconds = 240.0f;
        private float _cloudCoverage = 0.45f;
        private float _cloudScale = 1.4f;
        private float _cloudSpeed = 0.02f;
        private float _cloudSharpness = 1.75f;
        private float _starIntensity = 1.0f;
        private float _horizonHaze = 1.0f;
        private float _sunDiscSize = 0.9994f;
        private float _moonDiscSize = 0.99965f;

        private bool _syncDirectionalLightWithSun = false;
        private DirectionalLightComponent? _sunDirectionalLight;

        private bool _syncDirectionalLightWithMoon = false;
        private DirectionalLightComponent? _moonDirectionalLight;

        private bool _horizonAutoDisable = true;
        private float _horizonDisableThreshold = -0.05f;
        private float _horizonFadeRange = 0.15f;

        private float _sunIntensity = 6.0f;
        private float _moonIntensity = 0.35f;
        private bool _syncGlobalAmbientLighting = true;
        private float _sunGlobalAmbientScale = 0.018f;
        private float _moonGlobalAmbientScale = 0.08f;
        private float _minimumGlobalAmbientIntensity = 0.006f;
        private ColorF3 _minimumGlobalAmbientColor = new(0.12f, 0.16f, 0.28f);

        private bool _sunColorTemperatureEnabled = false;
        private bool _animateSunColorTemperature = true;
        private float _sunColorTemperatureKelvin = 5800.0f;
        private float _sunHorizonColorTemperatureKelvin = 2200.0f;
        private float _sunZenithColorTemperatureKelvin = 5800.0f;
        private bool _moonColorTemperatureEnabled = false;
        private bool _animateMoonColorTemperature = true;
        private float _moonColorTemperatureKelvin = 7500.0f;
        private float _moonHorizonColorTemperatureKelvin = 4500.0f;
        private float _moonZenithColorTemperatureKelvin = 7500.0f;

        // Cached shaders
        private static XRShader? s_vertexShader;
        private static XRShader? s_equirectShader;
        private static XRShader? s_octahedralShader;
        private static XRShader? s_cubemapShader;
        private static XRShader? s_cubemapArrayShader;
        private static XRShader? s_gradientShader;
        private static XRShader? s_dynamicShader;

        /// <summary>
        /// Creates a new skybox component with default settings.
        /// </summary>
        public SkyboxComponent()
        {
            _renderCommand = new RenderCommandMesh3D(EDefaultRenderPass.Background);
            _renderInfo = RenderInfo3D.New(this, _renderCommand);
            RenderedObjects = [_renderInfo];
        }

        /// <summary>
        /// Controls how the skybox is rendered (texture, gradient, or solid color).
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Mode")]
        [Description("Controls how the skybox is rendered (texture, gradient, or solid color).")]
        public ESkyboxMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }

        /// <summary>
        /// The projection type used to interpret the skybox texture.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Projection")]
        [Description("The projection type used to interpret the skybox texture.")]
        public ESkyboxProjection Projection
        {
            get => _projection;
            set => SetField(ref _projection, value);
        }

        /// <summary>
        /// The texture used for the skybox.
        /// Should match the projection type (2D for equirectangular/octahedral, cube for cubemap).
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Texture")]
        [Description("The texture used for the skybox. Should match the projection type.")]
        public XRTexture? Texture
        {
            get => _texture;
            set => SetField(ref _texture, value);
        }

        /// <summary>
        /// Top color used when <see cref="Mode"/> is <see cref="ESkyboxMode.Gradient"/> or <see cref="ESkyboxMode.SolidColor"/>.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Top Color")]
        [Description("Top color used for Gradient/SolidColor modes.")]
        public Vector3 TopColor
        {
            get => _topColor;
            set => SetField(ref _topColor, value);
        }

        /// <summary>
        /// Bottom color used when <see cref="Mode"/> is <see cref="ESkyboxMode.Gradient"/>.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Bottom Color")]
        [Description("Bottom color used for Gradient mode.")]
        public Vector3 BottomColor
        {
            get => _bottomColor;
            set => SetField(ref _bottomColor, value);
        }

        /// <summary>
        /// Intensity multiplier for the skybox color.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Intensity")]
        [Description("Intensity multiplier for the skybox color.")]
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Rotation of the skybox around the Y axis in degrees.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Rotation")]
        [Description("Rotation of the skybox around the Y axis in degrees.")]
        public float Rotation
        {
            get => _rotation;
            set => SetField(ref _rotation, value % 360.0f);
        }

        /// <summary>
        /// Layer index for cubemap array projection.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Cubemap Array Layer")]
        [Description("Layer index when using cubemap array projection.")]
        public int CubemapArrayLayer
        {
            get => _cubemapArrayLayer;
            set => SetField(ref _cubemapArrayLayer, Math.Max(0, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Auto Cycle")]
        [Description("Automatically advances the procedural day/night cycle over time.")]
        public bool AutoCycle
        {
            get => _autoCycle;
            set => SetField(ref _autoCycle, value);
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Time Of Day")]
        [Description("Normalized time in [0,1). 0.25 is noon, 0.75 is midnight.")]
        public float TimeOfDay
        {
            get => _timeOfDay;
            set => SetField(ref _timeOfDay, value - MathF.Floor(value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Day Length Seconds")]
        [Description("Duration of a full day-night-day cycle in seconds.")]
        public float DayLengthSeconds
        {
            get => _dayLengthSeconds;
            set => SetField(ref _dayLengthSeconds, Math.Max(1.0f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Cloud Coverage")]
        [Description("Controls the amount of visible clouds.")]
        public float CloudCoverage
        {
            get => _cloudCoverage;
            set => SetField(ref _cloudCoverage, Math.Clamp(value, 0.0f, 1.0f));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Cloud Scale")]
        [Description("Controls procedural cloud feature size.")]
        public float CloudScale
        {
            get => _cloudScale;
            set => SetField(ref _cloudScale, Math.Max(0.05f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Cloud Speed")]
        [Description("Controls cloud drift speed.")]
        public float CloudSpeed
        {
            get => _cloudSpeed;
            set => SetField(ref _cloudSpeed, Math.Max(0.0f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Cloud Sharpness")]
        [Description("Controls cloud edge softness and contrast.")]
        public float CloudSharpness
        {
            get => _cloudSharpness;
            set => SetField(ref _cloudSharpness, Math.Max(0.1f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Star Intensity")]
        [Description("Controls night star field visibility.")]
        public float StarIntensity
        {
            get => _starIntensity;
            set => SetField(ref _starIntensity, Math.Max(0.0f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Horizon Haze")]
        [Description("Controls near-horizon atmospheric haze intensity.")]
        public float HorizonHaze
        {
            get => _horizonHaze;
            set => SetField(ref _horizonHaze, Math.Max(0.0f, value));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Sun Disc Size")]
        [Description("Higher values produce a tighter/smaller sun disc.")]
        public float SunDiscSize
        {
            get => _sunDiscSize;
            set => SetField(ref _sunDiscSize, Math.Clamp(value, 0.9f, 0.99999f));
        }

        [Category("Skybox Dynamic")]
        [DisplayName("Moon Disc Size")]
        [Description("Higher values produce a tighter/smaller moon disc.")]
        public float MoonDiscSize
        {
            get => _moonDiscSize;
            set => SetField(ref _moonDiscSize, Math.Clamp(value, 0.9f, 0.99999f));
        }

        /// <summary>
        /// When enabled, rotates a directional light in the scene to match the procedural sun's
        /// direction each tick. Only active when <see cref="Mode"/> is <see cref="ESkyboxMode.DynamicProcedural"/>.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sync Directional Light With Sun")]
        [Description("When enabled (and Mode is DynamicProcedural), rotates a directional light to follow the procedural sun. Uses SunDirectionalLight if set, otherwise the scene's first directional light.")]
        public bool SyncDirectionalLightWithSun
        {
            get => _syncDirectionalLightWithSun;
            set => SetField(ref _syncDirectionalLightWithSun, value);
        }

        /// <summary>
        /// Specific directional light to synchronize with the procedural sun.
        /// When null and <see cref="SyncDirectionalLightWithSun"/> is enabled, the scene's first directional light is used.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Directional Light")]
        [Description("Specific directional light to sync with the sun. Leave unset to use the scene's first directional light.")]
        public DirectionalLightComponent? SunDirectionalLight
        {
            get => _sunDirectionalLight;
            set => SetField(ref _sunDirectionalLight, value);
        }

        /// <summary>
        /// When enabled, rotates a directional light in the scene to match the procedural moon's
        /// direction each tick. Only active when <see cref="Mode"/> is <see cref="ESkyboxMode.DynamicProcedural"/>.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sync Directional Light With Moon")]
        [Description("When enabled (and Mode is DynamicProcedural), rotates a directional light to follow the procedural moon. Uses MoonDirectionalLight if set, otherwise the scene's second directional light (falling back to the first if only one exists).")]
        public bool SyncDirectionalLightWithMoon
        {
            get => _syncDirectionalLightWithMoon;
            set => SetField(ref _syncDirectionalLightWithMoon, value);
        }

        /// <summary>
        /// Specific directional light to synchronize with the procedural moon.
        /// When null and <see cref="SyncDirectionalLightWithMoon"/> is enabled, the scene's second directional light is used
        /// (falling back to the first if only one exists and it is not already claimed by the sun).
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Directional Light")]
        [Description("Specific directional light to sync with the moon. Leave unset to auto-select a second directional light from the scene.")]
        public DirectionalLightComponent? MoonDirectionalLight
        {
            get => _moonDirectionalLight;
            set => SetField(ref _moonDirectionalLight, value);
        }

        /// <summary>
        /// When enabled, the synchronized sun/moon directional lights are automatically deactivated once their
        /// direction passes below <see cref="HorizonDisableThreshold"/>, so they skip shadow mapping and lighting work
        /// while they can't see the scene.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Horizon Auto Disable")]
        [Description("Automatically disables synced sun/moon directional lights when they sink past HorizonDisableThreshold.")]
        public bool HorizonAutoDisable
        {
            get => _horizonAutoDisable;
            set => SetField(ref _horizonAutoDisable, value);
        }

        /// <summary>
        /// Threshold on the light direction's Y component (world up is +Y). When the sun/moon direction's Y component
        /// drops below this value, the corresponding light is deactivated if <see cref="HorizonAutoDisable"/> is on.
        /// Use a small negative value to let the light continue working slightly past the visual horizon for twilight.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Horizon Disable Threshold")]
        [Description("Y-component threshold (−1..1) below which the synced light is disabled. Default −0.05 disables shortly after the light passes the horizon.")]
        public float HorizonDisableThreshold
        {
            get => _horizonDisableThreshold;
            set => SetField(ref _horizonDisableThreshold, Math.Clamp(value, -1.0f, 1.0f));
        }

        /// <summary>
        /// Width (on the light direction's Y component) of the smooth fade band above <see cref="HorizonDisableThreshold"/>.
        /// The synced light's intensity ramps from 0 at the threshold to its full configured value at (threshold + range),
        /// eliminating the visible pop when the sun/moon crosses the horizon.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Horizon Fade Range")]
        [Description("Range (0..1) above Horizon Disable Threshold over which the light smoothly fades in/out. Larger values produce a longer twilight fade.")]
        public float HorizonFadeRange
        {
            get => _horizonFadeRange;
            set => SetField(ref _horizonFadeRange, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Target <see cref="LightComponent.DiffuseIntensity"/> for the synced sun light at full elevation.
        /// The actual applied intensity is scaled by the smooth horizon fade factor.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Intensity")]
        [Description("Full-elevation DiffuseIntensity for the synced sun directional light. Sunlight should be bright.")]
        public float SunIntensity
        {
            get => _sunIntensity;
            set => SetField(ref _sunIntensity, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Target <see cref="LightComponent.DiffuseIntensity"/> for the synced moon light at full elevation.
        /// The actual applied intensity is scaled by the smooth horizon fade factor. Moonlight should be
        /// significantly dimmer than sunlight.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Intensity")]
        [Description("Full-elevation DiffuseIntensity for the synced moon directional light. Moonlight should be much dimmer than sunlight.")]
        public float MoonIntensity
        {
            get => _moonIntensity;
            set => SetField(ref _moonIntensity, Math.Max(0.0f, value));
        }

        /// <summary>
        /// When enabled, the procedural sky drives the world's global ambient term from sun/moon elevation.
        /// Deferred and forward/uber lighting consume this same world ambient value.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sync Global Ambient Lighting")]
        [Description("When enabled, DynamicProcedural sky updates the world ambient light from procedural sun/moon elevation.")]
        public bool SyncGlobalAmbientLighting
        {
            get => _syncGlobalAmbientLighting;
            set => SetField(ref _syncGlobalAmbientLighting, value);
        }

        /// <summary>
        /// Fraction of <see cref="SunIntensity"/> contributed to the world global ambient term at full sun elevation.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Global Ambient Scale")]
        [Description("Fraction of SunIntensity contributed to the world global ambient term when the sun is above the horizon.")]
        public float SunGlobalAmbientScale
        {
            get => _sunGlobalAmbientScale;
            set => SetField(ref _sunGlobalAmbientScale, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Fraction of <see cref="MoonIntensity"/> contributed to the world global ambient term at full moon elevation.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Global Ambient Scale")]
        [Description("Fraction of MoonIntensity contributed to the world global ambient term when the moon is above the horizon.")]
        public float MoonGlobalAmbientScale
        {
            get => _moonGlobalAmbientScale;
            set => SetField(ref _moonGlobalAmbientScale, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Low floor added to the procedural global ambient so the scene never collapses to pure black.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Minimum Global Ambient Intensity")]
        [Description("Low ambient floor added by the procedural sky even when both sun and moon are below the horizon.")]
        public float MinimumGlobalAmbientIntensity
        {
            get => _minimumGlobalAmbientIntensity;
            set => SetField(ref _minimumGlobalAmbientIntensity, Math.Max(0.0f, value));
        }

        /// <summary>
        /// Tint for the low procedural global ambient floor.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Minimum Global Ambient Color")]
        [Description("Tint for the low ambient floor added by the procedural sky.")]
        public ColorF3 MinimumGlobalAmbientColor
        {
            get => _minimumGlobalAmbientColor;
            set => SetField(ref _minimumGlobalAmbientColor, value);
        }

        /// <summary>
        /// When true, the synced sun directional light's Color is driven every tick by <see cref="SunColorTemperatureKelvin"/>.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Color Temperature Enabled")]
        [Description("When enabled, drives the synced sun light's Color from SunColorTemperatureKelvin each tick.")]
        public bool SunColorTemperatureEnabled
        {
            get => _sunColorTemperatureEnabled;
            set => SetField(ref _sunColorTemperatureEnabled, value);
        }

        /// <summary>
        /// When true, the synced sun directional light's color temperature animates with solar elevation,
        /// warming near the horizon and cooling toward its zenith.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Animate Sun Color Temperature")]
        [Description("When enabled, animates the synced sun light's color temperature from Sun Horizon Color Temperature to Sun Zenith Color Temperature based on sun elevation.")]
        public bool AnimateSunColorTemperature
        {
            get => _animateSunColorTemperature;
            set => SetField(ref _animateSunColorTemperature, value);
        }

        /// <summary>
        /// Fixed color temperature in Kelvin applied to the synced sun directional light when <see cref="SunColorTemperatureEnabled"/> is on
        /// and <see cref="AnimateSunColorTemperature"/> is off.
        /// Typical sunlight ~5500–6500 K; warm/golden-hour lighting ~2500–3500 K.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Color Temperature (K)")]
        [Description("Fixed color temperature (Kelvin) for the synced sun light when Animate Sun Color Temperature is off. Typical range 1000–12000.")]
        public float SunColorTemperatureKelvin
        {
            get => _sunColorTemperatureKelvin;
            set => SetField(ref _sunColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// Horizon color temperature in Kelvin applied to the synced sun light when <see cref="AnimateSunColorTemperature"/> is enabled.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Horizon Color Temperature (K)")]
        [Description("Animated sun temperature at and just below the horizon. Lower values are warmer.")]
        public float SunHorizonColorTemperatureKelvin
        {
            get => _sunHorizonColorTemperatureKelvin;
            set => SetField(ref _sunHorizonColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// Zenith color temperature in Kelvin applied to the synced sun light when <see cref="AnimateSunColorTemperature"/> is enabled.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Sun Zenith Color Temperature (K)")]
        [Description("Animated sun temperature high in the sky. Higher values are cooler/whiter.")]
        public float SunZenithColorTemperatureKelvin
        {
            get => _sunZenithColorTemperatureKelvin;
            set => SetField(ref _sunZenithColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// When true, the synced moon directional light's Color is driven every tick by <see cref="MoonColorTemperatureKelvin"/>.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Color Temperature Enabled")]
        [Description("When enabled, drives the synced moon light's Color from MoonColorTemperatureKelvin each tick.")]
        public bool MoonColorTemperatureEnabled
        {
            get => _moonColorTemperatureEnabled;
            set => SetField(ref _moonColorTemperatureEnabled, value);
        }

        /// <summary>
        /// When true, the synced moon directional light's color temperature animates with lunar elevation,
        /// warming near the horizon and cooling overhead.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Animate Moon Color Temperature")]
        [Description("When enabled, animates the synced moon light's color temperature from Moon Horizon Color Temperature to Moon Zenith Color Temperature based on moon elevation.")]
        public bool AnimateMoonColorTemperature
        {
            get => _animateMoonColorTemperature;
            set => SetField(ref _animateMoonColorTemperature, value);
        }

        /// <summary>
        /// Fixed color temperature in Kelvin applied to the synced moon directional light when <see cref="MoonColorTemperatureEnabled"/> is on
        /// and <see cref="AnimateMoonColorTemperature"/> is off.
        /// Moonlight is a reflection of sunlight but appears cooler due to the Purkinje effect; typical 7000–9000 K.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Color Temperature (K)")]
        [Description("Fixed color temperature (Kelvin) for the synced moon light when Animate Moon Color Temperature is off. Typical range 5000–12000.")]
        public float MoonColorTemperatureKelvin
        {
            get => _moonColorTemperatureKelvin;
            set => SetField(ref _moonColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// Horizon color temperature in Kelvin applied to the synced moon light when <see cref="AnimateMoonColorTemperature"/> is enabled.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Horizon Color Temperature (K)")]
        [Description("Animated moon temperature at and just above the horizon. Lower values are warmer.")]
        public float MoonHorizonColorTemperatureKelvin
        {
            get => _moonHorizonColorTemperatureKelvin;
            set => SetField(ref _moonHorizonColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// Zenith color temperature in Kelvin applied to the synced moon light when <see cref="AnimateMoonColorTemperature"/> is enabled.
        /// </summary>
        [Category("Skybox Dynamic")]
        [DisplayName("Moon Zenith Color Temperature (K)")]
        [Description("Animated moon temperature high in the sky. Higher values are cooler/bluer.")]
        public float MoonZenithColorTemperatureKelvin
        {
            get => _moonZenithColorTemperatureKelvin;
            set => SetField(ref _moonZenithColorTemperatureKelvin, Math.Clamp(value, 1000.0f, 40000.0f));
        }

        /// <summary>
        /// The material used to render the skybox.
        /// </summary>
        public XRMaterial? Material => _material;

        public RenderInfo[] RenderedObjects { get; }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Mode):
                case nameof(Projection):
                case nameof(Texture):
                    RebuildMaterial();
                    break;
                case nameof(Intensity):
                case nameof(Rotation):
                case nameof(CubemapArrayLayer):
                case nameof(TopColor):
                case nameof(BottomColor):
                case nameof(AutoCycle):
                case nameof(TimeOfDay):
                case nameof(DayLengthSeconds):
                case nameof(CloudCoverage):
                case nameof(CloudScale):
                case nameof(CloudSpeed):
                case nameof(CloudSharpness):
                case nameof(StarIntensity):
                case nameof(HorizonHaze):
                case nameof(SunDiscSize):
                case nameof(MoonDiscSize):
                    // These are set via uniforms, no rebuild needed
                    break;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            AttachDebugHooks();
            RegisterTick(ETickGroup.Normal, ETickOrder.Scene, TickSky);

            if (!_loggedActivated)
            {
                _loggedActivated = true;
                Debug.Rendering("[Skybox] Activated. Mode={0} Pass={1}", _mode, _renderCommand?.RenderPass ?? -1);
            }

            RebuildAll();
        }

        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(ETickGroup.Normal, ETickOrder.Scene, TickSky);
            CleanupResources();
        }

        private void TickSky()
        {
            if (_mode != ESkyboxMode.DynamicProcedural)
                return;

            if (_autoCycle)
            {
                float dt = Math.Max(0.0f, Engine.Delta);
                float dayLength = Math.Max(1.0f, _dayLengthSeconds);
                _timeOfDay = (_timeOfDay + dt / dayLength) % 1.0f;
            }

            DirectionalLightComponent? sun = _syncDirectionalLightWithSun ? ResolveSunLight() : null;
            DirectionalLightComponent? moon = _syncDirectionalLightWithMoon ? ResolveMoonLight(sun) : null;

            Vector3 sunDirection = GetSunDirection();
            float sunKelvin = ResolveAnimatedColorTemperatureKelvin(
                sunDirection,
                _animateSunColorTemperature,
                _sunColorTemperatureKelvin,
                _sunHorizonColorTemperatureKelvin,
                _sunZenithColorTemperatureKelvin);

            Vector3 moonDirection = GetMoonDirection();
            float moonKelvin = ResolveAnimatedColorTemperatureKelvin(
                moonDirection,
                _animateMoonColorTemperature,
                _moonColorTemperatureKelvin,
                _moonHorizonColorTemperatureKelvin,
                _moonZenithColorTemperatureKelvin);

            if (sun is not null)
                ApplyDirectionalLightSync(sun, sunDirection, _sunColorTemperatureEnabled, sunKelvin, _sunIntensity);

            if (moon is not null)
                ApplyDirectionalLightSync(moon, moonDirection, _moonColorTemperatureEnabled, moonKelvin, _moonIntensity);

            if (_syncGlobalAmbientLighting)
                ApplyGlobalAmbientSync(sun, moon, sunDirection, moonDirection, sunKelvin, moonKelvin);
        }

        /// <summary>
        /// Computes the world-space direction toward the procedural sun for the current <see cref="TimeOfDay"/>.
        /// Matches the formula used by the dynamic sky fragment shader.
        /// </summary>
        public Vector3 GetSunDirection()
        {
            float angle = _timeOfDay * MathF.Tau;
            Vector3 dir = new(MathF.Cos(angle), MathF.Sin(angle), 0.18f);
            float lenSq = dir.LengthSquared();
            return lenSq > 1e-8f ? dir / MathF.Sqrt(lenSq) : new Vector3(0.0f, 1.0f, 0.0f);
        }

        /// <summary>
        /// Computes the world-space direction toward the procedural moon for the current <see cref="TimeOfDay"/>.
        /// Matches the formula used by the dynamic sky fragment shader.
        /// </summary>
        public Vector3 GetMoonDirection()
        {
            float angle = _timeOfDay * MathF.Tau;
            float moonAngle = angle + MathF.PI + 0.25f * MathF.Sin(_timeOfDay * MathF.Tau * 0.3f);
            Vector3 dir = new(MathF.Cos(moonAngle), MathF.Sin(moonAngle), -0.22f);
            float lenSq = dir.LengthSquared();
            return lenSq > 1e-8f ? dir / MathF.Sqrt(lenSq) : new Vector3(0.0f, 1.0f, 0.0f);
        }

        /// <summary>
        /// Resolves the directional light to drive for the sun: the explicit <see cref="SunDirectionalLight"/> if set,
        /// otherwise the scene's first directional light.
        /// </summary>
        private DirectionalLightComponent? ResolveSunLight()
        {
            if (_sunDirectionalLight is not null)
                return _sunDirectionalLight;

            var lights = GetSceneDirectionalLights();
            return lights is { Count: > 0 } ? lights[0] : null;
        }

        /// <summary>
        /// Resolves the directional light to drive for the moon: the explicit <see cref="MoonDirectionalLight"/> if set,
        /// otherwise the scene's second directional light (falling back to the first if it wasn't already used for the sun).
        /// </summary>
        private DirectionalLightComponent? ResolveMoonLight(DirectionalLightComponent? sunLight)
        {
            if (_moonDirectionalLight is not null)
                return _moonDirectionalLight;

            var lights = GetSceneDirectionalLights();
            if (lights is null || lights.Count == 0)
                return null;

            for (int i = 0; i < lights.Count; i++)
            {
                var candidate = lights[i];
                if (candidate != sunLight)
                    return candidate;
            }

            return null;
        }

        private IReadOnlyList<DirectionalLightComponent>? GetSceneDirectionalLights()
            => (SceneNode?.World as XRWorldInstance)?.Lights.DynamicDirectionalLights;

        private void ApplyDirectionalLightSync(DirectionalLightComponent light, Vector3 direction, bool temperatureEnabled, float kelvin, float baseIntensity)
        {
            float fade = _horizonAutoDisable ? ComputeHorizonFade(direction) : 1.0f;

            // Fully below the fade band -> deactivate to skip shadow/lighting work.
            bool shouldBeActive = fade > 0.0f;
            if (light.IsActive != shouldBeActive)
                light.IsActive = shouldBeActive;

            if (!shouldBeActive)
                return;

            // Synced sun/moon are the scene's primary shadow casters; ensure shadows stay enabled.
            if (!light.CastsShadows)
                light.CastsShadows = true;

            light.DiffuseIntensity = baseIntensity * fade;

            if (temperatureEnabled)
                light.Color = KelvinToColorF3(kelvin);

            OrientLightToDirection(light, direction);
        }

        private void ApplyGlobalAmbientSync(
            DirectionalLightComponent? sun,
            DirectionalLightComponent? moon,
            Vector3 sunDirection,
            Vector3 moonDirection,
            float sunKelvin,
            float moonKelvin)
        {
            var settings = WorldAs<XRWorldInstance>()?.TargetWorld?.Settings;
            if (settings is null)
                return;

            float sunFade = ComputeHorizonFade(sunDirection);
            float moonFade = ComputeHorizonFade(moonDirection);

            Vector3 sunColor = sun is not null ? sun.Color : KelvinToColorF3(sunKelvin);
            Vector3 moonColor = moon is not null ? moon.Color : KelvinToColorF3(moonKelvin);

            Vector3 ambient =
                ((Vector3)_minimumGlobalAmbientColor * _minimumGlobalAmbientIntensity) +
                (sunColor * (_sunIntensity * _sunGlobalAmbientScale * sunFade)) +
                (moonColor * (_moonIntensity * _moonGlobalAmbientScale * moonFade));

            ApplyEffectiveAmbient(settings, ambient);
        }

        private float ComputeHorizonFade(Vector3 direction)
        {
            float threshold = _horizonDisableThreshold;
            float range = MathF.Max(_horizonFadeRange, 1e-4f);
            float t = Math.Clamp((direction.Y - threshold) / range, 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        private static void ApplyEffectiveAmbient(XREngine.Scene.WorldSettings settings, Vector3 ambient)
        {
            ambient = new Vector3(
                MathF.Max(0.0f, ambient.X),
                MathF.Max(0.0f, ambient.Y),
                MathF.Max(0.0f, ambient.Z));

            float intensity = MathF.Max(ambient.X, MathF.Max(ambient.Y, ambient.Z));
            if (intensity <= 1e-6f)
            {
                SetAmbientIfChanged(settings, ColorF3.Black, 0.0f);
                return;
            }

            ColorF3 color = new(ambient.X / intensity, ambient.Y / intensity, ambient.Z / intensity);
            SetAmbientIfChanged(settings, color, intensity);
        }

        private static void SetAmbientIfChanged(XREngine.Scene.WorldSettings settings, ColorF3 color, float intensity)
        {
            const float epsilon = 0.00001f;

            ColorF3 current = settings.AmbientLightColor;
            if (MathF.Abs(current.R - color.R) > epsilon ||
                MathF.Abs(current.G - color.G) > epsilon ||
                MathF.Abs(current.B - color.B) > epsilon)
            {
                settings.AmbientLightColor = color;
            }

            if (MathF.Abs(settings.AmbientLightIntensity - intensity) > epsilon)
                settings.AmbientLightIntensity = intensity;
        }

        private static void OrientLightToDirection(DirectionalLightComponent light, Vector3 direction)
        {
            // Light travels from the sun/moon toward the scene, so its forward vector is -direction.
            // Globals.Forward = -Z, so we need the transform rotation such that local -Z maps to -direction,
            // which means local +Z maps to +direction.

            // Build an orthonormal basis (right, up, direction) where local +X->right, +Y->up, +Z->direction.
            Vector3 upSeed = MathF.Abs(Vector3.Dot(direction, Globals.Up)) > 0.99f
                ? Globals.Right
                : Globals.Up;
            Vector3 right = Vector3.Normalize(Vector3.Cross(upSeed, direction));
            Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));

            // System.Numerics.Matrix4x4 is row-major with row-vector multiplication:
            // local axes map to rows (M11..M13 = world image of +X, M21..M23 = +Y, M31..M33 = +Z).
            Matrix4x4 basis = new(
                right.X, right.Y, right.Z, 0.0f,
                up.X, up.Y, up.Z, 0.0f,
                direction.X, direction.Y, direction.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);

            Quaternion worldRotation = Quaternion.CreateFromRotationMatrix(basis);

            if (light.Transform is Transform transform)
            {
                // Convert world rotation to local rotation relative to the light's parent.
                Quaternion parentWorldRotation = transform.Parent?.WorldRotation ?? Quaternion.Identity;
                Quaternion localRotation = Quaternion.Concatenate(worldRotation, Quaternion.Inverse(parentWorldRotation));
                transform.Rotation = localRotation;
            }
        }

        /// <summary>
        /// Converts a black-body color temperature in Kelvin to a linear RGB color using
        /// Tanner Helland's piecewise approximation. Output channels are clamped to [0, 1].
        /// </summary>
        public static ColorF3 KelvinToColorF3(float kelvin)
        {
            // Helland's approximation operates on "temperature / 100".
            float t = Math.Clamp(kelvin, 1000.0f, 40000.0f) / 100.0f;

            float r, g, b;

            if (t <= 66.0f)
            {
                r = 1.0f;
                g = Math.Clamp((99.4708025861f * MathF.Log(t) - 161.1195681661f) / 255.0f, 0.0f, 1.0f);
            }
            else
            {
                r = Math.Clamp((329.698727446f * MathF.Pow(t - 60.0f, -0.1332047592f)) / 255.0f, 0.0f, 1.0f);
                g = Math.Clamp((288.1221695283f * MathF.Pow(t - 60.0f, -0.0755148492f)) / 255.0f, 0.0f, 1.0f);
            }

            if (t >= 66.0f)
                b = 1.0f;
            else if (t <= 19.0f)
                b = 0.0f;
            else
                b = Math.Clamp((138.5177312231f * MathF.Log(t - 10.0f) - 305.0447927307f) / 255.0f, 0.0f, 1.0f);

            return new ColorF3(r, g, b);
        }

        private static float ResolveAnimatedColorTemperatureKelvin(
            Vector3 direction,
            bool animateTemperature,
            float fixedKelvin,
            float horizonKelvin,
            float zenithKelvin)
        {
            if (!animateTemperature)
                return fixedKelvin;

            float t = SmoothStep(-0.05f, 0.35f, direction.Y);
            return Lerp(horizonKelvin, zenithKelvin, t);
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            if (Math.Abs(edge1 - edge0) < 1e-6f)
                return value >= edge1 ? 1.0f : 0.0f;

            float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        private static float Lerp(float start, float end, float amount)
            => start + ((end - start) * amount);

        private void AttachDebugHooks()
        {
            if (_debugHooksAttached)
                return;

            _renderInfo?.CollectedForRenderCallback += OnCollectedForRender;

            _renderCommand?.PreRender += OnPreRender;

            _debugHooksAttached = true;
        }

        private void DetachDebugHooks()
        {
            if (!_debugHooksAttached)
                return;

            _renderInfo?.CollectedForRenderCallback -= OnCollectedForRender;

            _renderCommand?.PreRender -= OnPreRender;

            _debugHooksAttached = false;
        }

        private void OnCollectedForRender(RenderInfo info, RenderCommand command, IRuntimeRenderCamera? camera)
        {
            if (_loggedCollected)
                return;

            _loggedCollected = true;
            Debug.Rendering("[Skybox] CollectedForRender. CmdPass={0} Camera={1}", command.RenderPass, camera?.ToString() ?? "<null>");
        }

        private void OnPreRender()
        {
            if (_loggedRendered)
                return;

            _loggedRendered = true;
            Debug.Rendering("[Skybox] RenderCommand.PreRender fired (draw executing). Pass={0}", _renderCommand?.RenderPass ?? -1);
        }

        private void RebuildAll()
        {
            RebuildMesh();
            RebuildMaterial();
            UpdateRenderCommand();
        }

        /// <summary>
        /// Creates a fullscreen triangle mesh that covers the entire screen when rendered.
        /// Uses a single oversized triangle to avoid the diagonal seam that would occur with a quad.
        /// </summary>
        private void RebuildMesh()
        {
            _mesh?.Destroy();

            // Create a fullscreen triangle that overdraws past the screen bounds
            // This is more efficient than a quad (one triangle vs two) and avoids seams
            VertexTriangle triangle = new(
                new Vertex(new Vector3(-1, -1, 0)),
                new Vertex(new Vector3(3, -1, 0)),
                new Vertex(new Vector3(-1, 3, 0)));

            _mesh = XRMesh.Create(triangle);

            _meshRenderer?.Mesh = _mesh;
        }

        private void RebuildMaterial()
        {
            _material?.SettingUniforms -= SetUniforms;

            XRShader? vertexShader = GetVertexShader();
            XRShader? fragmentShader = _mode switch
            {
                ESkyboxMode.Texture => GetFragmentShaderForProjection(_projection),
                ESkyboxMode.DynamicProcedural => GetDynamicShader(),
                _ => GetGradientShader()
            };
            
            if (vertexShader is null || fragmentShader is null)
            {
                Debug.RenderingWarning($"SkyboxComponent: Failed to load shaders for projection {_projection}");
                return;
            }

            XRTexture? tex = _mode == ESkyboxMode.Texture ? (_texture ?? CreateDefaultTexture(_projection)) : null;

            RenderingParameters renderParams = new()
            {
                CullMode = ECullMode.None, // Fullscreen triangle has no meaningful face culling
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    UpdateDepth = false,
                    Function = EComparison.Lequal,
                },
                // Skybox shaders reconstruct and rotate view rays in the vertex stage, so they require camera matrices.
                RequiredEngineUniforms = EUniformRequirements.Camera,
                // Skybox uses a specialized vertex shader that outputs clip-space positions directly.
                // GPU indirect dispatch would replace it with a model-matrix-based shader, breaking rendering.
                ExcludeFromGpuIndirect = true,
            };

            _material = new XRMaterial(tex is not null ? [tex] : [], vertexShader, fragmentShader)
            {
                RenderPass = (int)EDefaultRenderPass.Background,
                RenderOptions = renderParams,
            };

            // RenderCommand.RenderPass is what the pipeline uses to bucket this draw.
            // Some pipelines may not execute the Background pass; debug mode forces a widely-used pass.
            _renderCommand?.RenderPass = _material.RenderPass;

            _material.SettingUniforms += SetUniforms;

            if (_meshRenderer is null)
            {
                _meshRenderer = new XRMeshRenderer(_mesh, _material);
            }
            else
            {
                _meshRenderer.Material = _material;
            }

            UpdateRenderCommand();
        }

        private void SetUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            program.Uniform("SkyboxIntensity", _intensity);
            program.Uniform("SkyboxRotation", _rotation * MathF.PI / 180.0f);

            if (_mode != ESkyboxMode.Texture)
            {
                Vector3 top = _topColor;
                Vector3 bottom = _mode == ESkyboxMode.SolidColor ? _topColor : _bottomColor;
                program.Uniform("SkyboxTopColor", top);
                program.Uniform("SkyboxBottomColor", bottom);
            }

            if (_mode == ESkyboxMode.DynamicProcedural)
            {
                program.Uniform("SkyTimeOfDay", _timeOfDay);
                program.Uniform("SkyCloudCoverage", _cloudCoverage);
                program.Uniform("SkyCloudScale", _cloudScale);
                program.Uniform("SkyCloudSpeed", _cloudSpeed);
                program.Uniform("SkyCloudSharpness", _cloudSharpness);
                program.Uniform("SkyStarIntensity", _starIntensity);
                program.Uniform("SkyHorizonHaze", _horizonHaze);
                program.Uniform("SkySunDiscSize", _sunDiscSize);
                program.Uniform("SkyMoonDiscSize", _moonDiscSize);
            }
            
            if (_projection == ESkyboxProjection.CubemapArray)
                program.Uniform("CubemapLayer", _cubemapArrayLayer);
        }

        private void UpdateRenderCommand()
        {
            if (_renderCommand is null || _meshRenderer is null)
                return;

            _renderCommand.Mesh = _meshRenderer;
            _renderCommand.WorldMatrix = Matrix4x4.Identity;
        }

        private void CleanupResources()
        {
            DetachDebugHooks();

            _material?.SettingUniforms -= SetUniforms;

            _mesh?.Destroy();
            _mesh = null;
            _material = null;
            _meshRenderer = null;
        }

        private static XRShader? GetFragmentShaderForProjection(ESkyboxProjection projection)
        {
            return projection switch
            {
                ESkyboxProjection.Equirectangular => GetEquirectShader(),
                ESkyboxProjection.Octahedral => GetOctahedralShader(),
                ESkyboxProjection.Cubemap => GetCubemapShader(),
                ESkyboxProjection.CubemapArray => GetCubemapArrayShader(),
                _ => null
            };
        }

        private static XRShader GetVertexShader()
        {
            if (s_vertexShader is null)
            {
                s_vertexShader = Engine.Assets.LoadEngineAsset<XRShader>(
                    JobPriority.Highest,
                    "Shaders", "Scene3D", "Skybox.vs");

                // Inline fallback shader
                s_vertexShader ??= new XRShader(EShaderType.Vertex, VertexShaderSource);
            }
            return s_vertexShader;
        }

        private static XRShader GetGradientShader()
        {
            if (s_gradientShader is not null)
                return s_gradientShader;

            s_gradientShader = Engine.Assets.LoadEngineAsset<XRShader>(
                JobPriority.Highest,
                "Shaders", "Scene3D", "SkyboxGradient.fs");

            // Inline fallback shader
            return s_gradientShader ??= new XRShader(EShaderType.Fragment, GradientShaderSource);
        }

        private static XRShader GetDynamicShader()
        {
            if (s_dynamicShader is not null)
                return s_dynamicShader;

            s_dynamicShader = Engine.Assets.LoadEngineAsset<XRShader>(
                JobPriority.Highest,
                "Shaders", "Scene3D", "SkyboxDynamic.fs");

            return s_dynamicShader ??= new XRShader(EShaderType.Fragment, DynamicShaderSource);
        }

        private static XRShader GetEquirectShader()
        {
            if (s_equirectShader is null)
            {
                s_equirectShader = Engine.Assets.LoadEngineAsset<XRShader>(
                    JobPriority.Highest,
                    "Shaders", "Scene3D", "SkyboxEquirect.fs");

                // Inline fallback shader
                s_equirectShader ??= new XRShader(EShaderType.Fragment, EquirectShaderSource);
            }
            return s_equirectShader;
        }

        private static XRShader GetOctahedralShader()
        {
            if (s_octahedralShader is null)
            {
                s_octahedralShader = Engine.Assets.LoadEngineAsset<XRShader>(
                    JobPriority.Highest,
                    "Shaders", "Scene3D", "SkyboxOctahedral.fs");

                // Inline fallback shader
                s_octahedralShader ??= new XRShader(EShaderType.Fragment, OctahedralShaderSource);
            }
            return s_octahedralShader;
        }

        private static XRShader GetCubemapShader()
        {
            if (s_cubemapShader is null)
            {
                s_cubemapShader = Engine.Assets.LoadEngineAsset<XRShader>(
                    JobPriority.Highest,
                    "Shaders", "Scene3D", "SkyboxCubemap.fs");

                // Inline fallback shader
                s_cubemapShader ??= new XRShader(EShaderType.Fragment, CubemapShaderSource);
            }
            return s_cubemapShader;
        }

        private static XRShader GetCubemapArrayShader()
        {
            if (s_cubemapArrayShader is null)
            {
                s_cubemapArrayShader = Engine.Assets.LoadEngineAsset<XRShader>(
                    JobPriority.Highest,
                    "Shaders", "Scene3D", "SkyboxCubemapArray.fs");

                // Inline fallback shader
                s_cubemapArrayShader ??= new XRShader(EShaderType.Fragment, CubemapArrayShaderSource);
            }
            return s_cubemapArrayShader;
        }

        private static XRTexture? CreateDefaultTexture(ESkyboxProjection projection)
        {
            // Return null; the user should provide a texture
            return null;
        }

        #region Inline Shader Sources

        /// <summary>
        /// Vertex shader that outputs the fullscreen triangle and precomputes world-space sky rays.
        /// </summary>
        private const string VertexShaderSource = @"
#version 450

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragClipPos;
layout(location = 1) out vec3 FragWorldDir;

uniform mat4 InverseViewMatrix;
uniform mat4 InverseProjMatrix;
uniform float SkyboxRotation = 0.0;

vec3 GetWorldRay(vec2 clipXY)
{
    vec4 viewPos = InverseProjMatrix * vec4(clipXY, 1.0, 1.0);
    float invW = abs(viewPos.w) > 1e-6 ? 1.0 / viewPos.w : 1.0;
    vec3 viewRay = viewPos.xyz * invW;
    return mat3(InverseViewMatrix) * viewRay;
}

vec3 RotateSkyDirection(vec3 dir)
{
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    return vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot);
}

void main()
{
    vec2 clipXY = Position.xy;
    FragClipPos = Position;
    FragWorldDir = RotateSkyDirection(GetWorldRay(clipXY));
    // Output at maximum depth (z=1) so skybox is behind everything
    gl_Position = vec4(clipXY, 1.0, 1.0);
}
";

        private const string EquirectShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;

const float PI = 3.14159265359;

void main()
{
    vec3 dir = normalize(FragWorldDir);

    // Convert direction to spherical coordinates
    float phi = atan(dir.z, dir.x);
    float theta = asin(clamp(dir.y, -1.0, 1.0));
    
    // Map to UV coordinates
    vec2 uv = vec2((phi / (2.0 * PI)) + 0.5, 1.0 - ((theta / PI) + 0.5));

    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
";

        private const string OctahedralShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;

vec2 EncodeOcta(vec3 dir)
{
    // Swizzle: world Y (up) -> octahedral Z
    vec3 octDir = vec3(dir.x, dir.z, dir.y);
    octDir /= max(abs(octDir.x) + abs(octDir.y) + abs(octDir.z), 1e-5);

    vec2 uv = octDir.xy;
    if (octDir.z < 0.0)
    {
        vec2 signDir = vec2(octDir.x >= 0.0 ? 1.0 : -1.0, octDir.y >= 0.0 ? 1.0 : -1.0);
        uv = (1.0 - abs(uv.yx)) * signDir;
    }

    return uv * 0.5 + 0.5;
}

void main()
{
    vec3 dir = normalize(FragWorldDir);

    vec2 uv = EncodeOcta(dir);
    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
";

        private const string CubemapShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform samplerCube Texture0;
uniform float SkyboxIntensity = 1.0;

void main()
{
    vec3 dir = normalize(FragWorldDir);

    OutColor = texture(Texture0, dir).rgb * SkyboxIntensity;
}
";

        private const string CubemapArrayShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform samplerCubeArray Texture0;
uniform float SkyboxIntensity = 1.0;
uniform int CubemapLayer = 0;

void main()
{
    vec3 dir = normalize(FragWorldDir);

    OutColor = texture(Texture0, vec4(dir, float(CubemapLayer))).rgb * SkyboxIntensity;
}
";

        private const string GradientShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform float SkyboxIntensity = 1.0;
uniform vec3 SkyboxTopColor = vec3(0.52, 0.74, 1.0);
uniform vec3 SkyboxBottomColor = vec3(0.05, 0.06, 0.08);

void main()
{
    vec3 dir = normalize(FragWorldDir);
    float t = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 col = mix(SkyboxBottomColor, SkyboxTopColor, t);
    OutColor = col * SkyboxIntensity;
}
";

        private const string DynamicShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 1) in vec3 FragWorldDir;

uniform float SkyboxIntensity = 1.0;
uniform float SkyTimeOfDay = 0.25;
uniform float SkyCloudCoverage = 0.45;
uniform float SkyCloudScale = 1.4;
uniform float SkyCloudSpeed = 0.02;
uniform float SkyCloudSharpness = 1.75;
uniform float SkyStarIntensity = 1.0;
uniform float SkyHorizonHaze = 1.0;
uniform float SkySunDiscSize = 0.9994;
uniform float SkyMoonDiscSize = 0.99965;

const float PI = 3.14159265359;
const float TAU = 6.28318530718;

vec2 SafeNormalize2(vec2 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * inversesqrt(lenSq) : vec2(0.0, 1.0);
}

vec3 SafeNormalize3(vec3 v)
{
    float lenSq = dot(v, v);
    return lenSq > 1e-8 ? v * inversesqrt(lenSq) : vec3(0.0, 1.0, 0.0);
}

vec2 DirectionToOctahedralPlane(vec3 dir)
{
    vec3 n = SafeNormalize3(dir);
    float invL1 = 1.0 / max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6);
    vec2 oct = n.xz * invL1;

    if (n.y < 0.0)
    {
        vec2 octSign = vec2(oct.x >= 0.0 ? 1.0 : -1.0, oct.y >= 0.0 ? 1.0 : -1.0);
        oct = (1.0 - abs(oct.yx)) * octSign;
    }

    return oct;
}

float Hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float Hash3(vec3 p)
{
    p = fract(p * vec3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.yzx + 19.19);
    return fract((p.x + p.y) * p.z);
}

float Noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(Hash(i + vec2(0.0, 0.0)), Hash(i + vec2(1.0, 0.0)), u.x),
               mix(Hash(i + vec2(0.0, 1.0)), Hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float Noise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = Hash3(i);
    float n100 = Hash3(i + vec3(1.0, 0.0, 0.0));
    float n010 = Hash3(i + vec3(0.0, 1.0, 0.0));
    float n110 = Hash3(i + vec3(1.0, 1.0, 0.0));
    float n001 = Hash3(i + vec3(0.0, 0.0, 1.0));
    float n101 = Hash3(i + vec3(1.0, 0.0, 1.0));
    float n011 = Hash3(i + vec3(0.0, 1.0, 1.0));
    float n111 = Hash3(i + vec3(1.0, 1.0, 1.0));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
               mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

float Fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise(p);
        p = p * 2.03 + vec2(17.0, 11.0);
        a *= 0.5;
    }
    return v;
}

float Fbm6(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise(p);
        p = p * 2.17 + vec2(4.3, 9.1);
        a *= 0.55;
    }
    return v;
}

float Fbm3(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.03 + vec3(17.0, 11.0, 5.3);
        a *= 0.5;
    }
    return v;
}

float Fbm3_6(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 6; ++i)
    {
        v += a * Noise3(p);
        p = p * 2.17 + vec3(4.3, 9.1, 2.7);
        a *= 0.55;
    }
    return v;
}

vec3 Atmosphere(vec3 viewDir, vec3 sunDir)
{
    float cosZen = max(viewDir.y, -0.1);
    float cosSunZen = max(sunDir.y, -0.1);
    float mu = dot(viewDir, sunDir);

    float viewPath = 1.0 / (cosZen + 0.15);
    float sunPath = 1.0 / (cosSunZen + 0.15);

    float rayleighPhase = 0.75 * (1.0 + mu * mu);
    float g = 0.76;
    float g2 = g * g;
    float miePhase = (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5);
    miePhase *= 0.0597;

    vec3 rayleighCoeff = vec3(0.58, 1.35, 3.31);
    vec3 mieCoeff = vec3(2.1);

    vec3 sunExt = exp(-rayleighCoeff * sunPath * 0.22 - mieCoeff * sunPath * 0.08);
    vec3 viewExt = exp(-rayleighCoeff * viewPath * 0.10 - mieCoeff * viewPath * 0.06);

    float sunStrength = smoothstep(-0.14, 0.25, sunDir.y);

    vec3 rayleighIn = rayleighCoeff * rayleighPhase * sunExt * (1.0 - viewExt);
    vec3 mieIn = mieCoeff * miePhase * sunExt * (1.0 - viewExt) * 0.35;

    return (rayleighIn + mieIn) * sunStrength;
}

vec3 ShadeMoon(vec3 dir, vec3 moonDir, vec3 sunDir, float nightFactor)
{
    vec3 up = abs(moonDir.y) < 0.95 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = SafeNormalize3(cross(up, moonDir));
    vec3 bitangent = cross(moonDir, tangent);

    float d = dot(dir, moonDir);
    if (d <= SkyMoonDiscSize)
    {
        float corona = pow(max(d, 0.0), 96.0) * 0.35 + pow(max(d, 0.0), 24.0) * 0.05;
        return vec3(0.74, 0.79, 0.94) * corona * nightFactor;
    }

    float t = dot(dir, tangent) / max(d, 1e-3);
    float b = dot(dir, bitangent) / max(d, 1e-3);
    float discRadius = sqrt(max(1.0 - SkyMoonDiscSize * SkyMoonDiscSize, 1e-6)) * 1.15;
    vec2 local = vec2(t, b);
    float rn = clamp(dot(local, local) / (discRadius * discRadius), 0.0, 1.0);

    float z = sqrt(max(1.0 - rn, 0.0));
    vec3 surfaceN = SafeNormalize3(tangent * (local.x / discRadius)
                                 + bitangent * (local.y / discRadius)
                                 + moonDir * z);

    float phase = clamp(dot(surfaceN, sunDir), 0.0, 1.0);

    vec2 surfUv = local / discRadius;
    float maria = smoothstep(0.40, 0.58, Fbm(surfUv * 2.1 + vec2(7.3, 1.9)));
    float craters = mix(0.72, 1.0, Fbm6(surfUv * 5.5) * 0.5 + 0.5);

    vec3 moonAlbedo = mix(vec3(0.82, 0.83, 0.86), vec3(0.45, 0.49, 0.58), maria);
    vec3 moonLit = moonAlbedo * craters * (0.06 + phase * 1.15);

    float edgeSoft = smoothstep(1.0, 0.85, rn);
    vec3 moonColor = moonLit * edgeSoft;

    float coronaFalloff = max(d, 0.0);
    float corona = pow(coronaFalloff, 96.0) * 0.40 + pow(coronaFalloff, 24.0) * 0.06;
    moonColor += vec3(0.74, 0.79, 0.94) * corona;

    return moonColor * nightFactor;
}

void main()
{
    vec3 dir = SafeNormalize3(FragWorldDir);

    float angle = SkyTimeOfDay * TAU;
    vec3 sunDir = SafeNormalize3(vec3(cos(angle), sin(angle), 0.18));
    float moonAngle = angle + PI + 0.25 * sin(SkyTimeOfDay * TAU * 0.3);
    vec3 moonDir = SafeNormalize3(vec3(cos(moonAngle), sin(moonAngle), -0.22));

    float dayFactor = smoothstep(-0.18, 0.12, sunDir.y);
    float nightFactor = 1.0 - dayFactor;
    float duskFactor = clamp(exp(-sunDir.y * sunDir.y * 38.0), 0.0, 1.0);

    vec3 skyBase = Atmosphere(dir, sunDir);

    float horizonT = 1.0 - clamp(abs(dir.y), 0.0, 1.0);
    vec3 horizonGlow = vec3(1.15, 0.55, 0.22) * pow(horizonT, 3.0) * duskFactor * SkyHorizonHaze;

    vec2 flatSun = SafeNormalize2(vec2(sunDir.x, sunDir.z));
    vec2 flatDir = SafeNormalize2(vec2(dir.x, dir.z));
    float antiSun = max(-dot(flatDir, flatSun), 0.0);
    vec3 beltOfVenus = vec3(0.78, 0.55, 0.68) * pow(horizonT, 2.2) * antiSun * duskFactor * 0.45;

    vec3 color = skyBase + horizonGlow + beltOfVenus;
    color *= mix(vec3(1.0), vec3(1.20, 0.88, 0.78), duskFactor * 0.55);

    float starDensity = 256.0;
    vec3 starP = dir * starDensity;
    vec3 starCell = floor(starP);
    float starHash = Hash3(starCell);
    float twinkle = 0.65 + 0.35 * sin(TAU * (SkyTimeOfDay * 360.0 + starHash * 53.0));
    float smallStar = step(0.9975, starHash) * (0.55 + 0.45 * fract(starHash * 17.3)) * twinkle;

    float bigDensity = 64.0;
    vec3 bigP = dir * bigDensity;
    vec3 bigCell = floor(bigP);
    float bigHash = Hash3(bigCell);
    vec3 bigOffset = fract(bigP) - 0.5;
    float bigStar = step(0.997, bigHash) * exp(-dot(bigOffset, bigOffset) * 60.0);
    bigStar *= 0.6 + 0.4 * sin(TAU * (SkyTimeOfDay * 180.0 + bigHash * 91.0));
    float hueSeed = Hash3(starCell + vec3(11.0, 23.0, 7.0));
    vec3 starColor = mix(vec3(0.85, 0.90, 1.10), vec3(1.10, 0.95, 0.78), hueSeed);

    vec3 stars = (smallStar + bigStar * 2.4) * starColor;

    vec3 galacticUp = SafeNormalize3(vec3(0.35, 0.22, 0.91));
    float bandCoord = dot(dir, galacticUp);
    float band = exp(-bandCoord * bandCoord * 38.0);
    float mwDetail = smoothstep(0.35, 0.95, Fbm3_6(dir * 6.2));
    vec3 milkyWay = mix(vec3(0.06, 0.08, 0.15), vec3(0.22, 0.18, 0.28), mwDetail) * band * mwDetail * 0.35;

    float starHorizonFade = smoothstep(-0.05, 0.22, dir.y);
    vec3 starField = (stars + milkyWay) * SkyStarIntensity * starHorizonFade;

    vec3 nightBase = vec3(0.006, 0.009, 0.022) * (0.4 + 0.6 * smoothstep(-0.12, 0.4, dir.y));

    vec3 night = nightBase + starField * nightFactor;
    color = mix(night, color + night * 0.15, clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0));

    float timeAdvect = SkyCloudSpeed * SkyTimeOfDay * 240.0;
    float flowAngle = SkyTimeOfDay * 0.35;
    vec3 flow = vec3(cos(flowAngle), 0.0, sin(flowAngle)) * timeAdvect * 0.25;
    vec3 cloudP = dir * SkyCloudScale * 3.0 + flow;

    vec3 warp = vec3(Fbm3(cloudP * 1.3 + vec3(3.2, 7.1, 0.5)),
                     Fbm3(cloudP * 1.3 + vec3(1.7, 9.3, 4.2)),
                     Fbm3(cloudP * 1.3 + vec3(6.1, 2.8, 8.4))) - 0.5;
    vec3 warped = cloudP + warp * 1.2;
    float cloudBase = Fbm3_6(warped);
    float cloudDetail = Fbm3(warped * 3.7 - flow * 0.3);
    float cloudShape = cloudBase * 0.75 + cloudDetail * 0.25;

    float cloudMask = smoothstep(-0.08, 0.12, dir.y);
    float cloud = smoothstep(1.0 - SkyCloudCoverage, 1.0, pow(cloudShape, SkyCloudSharpness)) * cloudMask;

    float muSun = dot(dir, sunDir);
    float silver = pow(max(muSun, 0.0), 8.0);
    float powder = 1.0 - exp(-cloud * 2.5);
    vec3 cloudLit = mix(vec3(0.45, 0.48, 0.55), vec3(1.00, 0.98, 0.93), dayFactor);
    vec3 cloudShadow = mix(vec3(0.04, 0.05, 0.09), vec3(0.58, 0.63, 0.75), dayFactor);
    vec3 cloudColor = mix(cloudShadow, cloudLit, powder);
    cloudColor += vec3(1.30, 0.55, 0.22) * silver * duskFactor * 1.2;
    cloudColor = mix(cloudColor, color, smoothstep(0.5, 0.03, dir.y) * 0.55);

    color = mix(color, cloudColor, cloud);

    float cosSun = dot(dir, sunDir);
    float sunDisc = smoothstep(SkySunDiscSize, 1.0, cosSun);
    float limbT = clamp((cosSun - SkySunDiscSize) / max(1.0 - SkySunDiscSize, 1e-4), 0.0, 1.0);
    float limbDark = mix(0.55, 1.0, pow(limbT, 0.5));
    vec3 sunColor = mix(vec3(1.4, 0.55, 0.18), vec3(1.0, 0.96, 0.88), smoothstep(-0.05, 0.35, sunDir.y));
    float sunAureole = pow(max(cosSun, 0.0), 96.0) * 0.80 + pow(max(cosSun, 0.0), 32.0) * 0.12;
    float sunOcclusion = 1.0 - cloud * 0.85;
    color += sunColor * (sunDisc * 18.0 * limbDark + sunAureole) * dayFactor * sunOcclusion;

    vec3 moonContribution = ShadeMoon(dir, moonDir, sunDir, nightFactor);
    color += moonContribution * (1.0 - cloud * 0.9);

    if (any(isnan(color)) || any(isinf(color)))
    {
        float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
        color = mix(vec3(0.05, 0.08, 0.15), vec3(0.30, 0.50, 0.85), h);
    }

    OutColor = max(color, vec3(0.0)) * SkyboxIntensity;
}
";

        #endregion

        #region Static Factory Methods

        /// <summary>
        /// Creates a skybox component with an equirectangular texture.
        /// </summary>
        public static SkyboxComponent CreateEquirectangular(XRTexture2D texture, float intensity = 1.0f)
        {
            return new SkyboxComponent
            {
                Projection = ESkyboxProjection.Equirectangular,
                Texture = texture,
                Intensity = intensity
            };
        }

        /// <summary>
        /// Creates a skybox component with an octahedral texture.
        /// </summary>
        public static SkyboxComponent CreateOctahedral(XRTexture2D texture, float intensity = 1.0f)
        {
            return new SkyboxComponent
            {
                Projection = ESkyboxProjection.Octahedral,
                Texture = texture,
                Intensity = intensity
            };
        }

        /// <summary>
        /// Creates a skybox component with a cubemap texture.
        /// </summary>
        public static SkyboxComponent CreateCubemap(XRTextureCube texture, float intensity = 1.0f)
        {
            return new SkyboxComponent
            {
                Projection = ESkyboxProjection.Cubemap,
                Texture = texture,
                Intensity = intensity
            };
        }

        /// <summary>
        /// Creates a skybox component with a cubemap array texture.
        /// </summary>
        public static SkyboxComponent CreateCubemapArray(XRTextureCubeArray texture, int layer = 0, float intensity = 1.0f)
        {
            return new SkyboxComponent
            {
                Projection = ESkyboxProjection.CubemapArray,
                Texture = texture,
                CubemapArrayLayer = layer,
                Intensity = intensity
            };
        }

        #endregion
    }
}
