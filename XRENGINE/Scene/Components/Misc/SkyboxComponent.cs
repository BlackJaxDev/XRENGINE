using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;

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
            if (!_autoCycle || _mode != ESkyboxMode.DynamicProcedural)
                return;

            float dt = Math.Max(0.0f, Engine.Delta);
            float dayLength = Math.Max(1.0f, _dayLengthSeconds);
            _timeOfDay = (_timeOfDay + dt / dayLength) % 1.0f;
        }

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

float Noise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(Hash(i + vec2(0.0, 0.0)), Hash(i + vec2(1.0, 0.0)), u.x),
               mix(Hash(i + vec2(0.0, 1.0)), Hash(i + vec2(1.0, 1.0)), u.x), u.y);
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

vec3 NightSky(vec3 dir, float nightFactor)
{
    float horizonFade = smoothstep(-0.1, 0.35, dir.y);
    vec3 nightGradient = mix(vec3(0.01, 0.015, 0.03), vec3(0.005, 0.007, 0.015), horizonFade);
    vec2 starUv = DirectionToOctahedralPlane(dir) * 256.0 + vec2(dir.y * 73.0, dir.x * 41.0);
    float stars = step(0.9975, Hash(floor(starUv))) * nightFactor;
    return nightGradient + stars * vec3(1.0, 0.96, 0.9) * SkyStarIntensity;
}

void main()
{
    vec3 dir = SafeNormalize3(FragWorldDir);

    float angle = SkyTimeOfDay * PI * 2.0;
    vec3 sunDir = normalize(vec3(cos(angle), sin(angle), 0.2));
    vec3 moonDir = -sunDir;
    float sunHeight = sunDir.y;

    float dayFactor = smoothstep(-0.18, 0.1, sunHeight);
    float nightFactor = 1.0 - dayFactor;
    float duskFactor = 1.0 - abs(sunHeight) / 0.22;
    duskFactor = clamp(duskFactor, 0.0, 1.0);

    float h = clamp(dir.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 dayHorizon = vec3(0.73, 0.84, 1.0);
    vec3 dayZenith = vec3(0.15, 0.42, 0.95);
    vec3 duskHorizon = vec3(1.0, 0.45, 0.18);
    vec3 duskZenith = vec3(0.26, 0.08, 0.32);
    vec3 skyDay = mix(dayHorizon, dayZenith, pow(h, 0.45));
    vec3 skyDusk = mix(duskHorizon, duskZenith, pow(h, 0.65));
    vec3 skyBase = mix(skyDusk, skyDay, dayFactor);

    float mie = pow(max(dot(dir, sunDir), 0.0), 18.0);
    float rayleigh = pow(max(dot(dir, sunDir), 0.0), 3.0);
    vec3 sunsetScatter = (vec3(1.0, 0.34, 0.16) * mie + vec3(0.45, 0.56, 1.0) * rayleigh * 0.4) * duskFactor;
    vec3 color = skyBase + sunsetScatter;

    float horizon = 1.0 - clamp(abs(dir.y), 0.0, 1.0);
    color += vec3(0.22, 0.19, 0.15) * pow(horizon, 2.2) * SkyHorizonHaze * duskFactor;

    vec2 cloudUv = DirectionToOctahedralPlane(dir) * SkyCloudScale;
    cloudUv += vec2(SkyCloudSpeed * SkyTimeOfDay * 240.0, SkyCloudSpeed * SkyTimeOfDay * 120.0);
    float cloudMask = smoothstep(-0.08, 0.12, dir.y);
    float cloud = Fbm(cloudUv);
    cloud = smoothstep(1.0 - SkyCloudCoverage, 1.0, pow(cloud, SkyCloudSharpness)) * cloudMask;
    vec3 cloudDay = vec3(1.0, 0.98, 0.95);
    vec3 cloudNight = vec3(0.26, 0.28, 0.35);
    vec3 cloudTint = mix(cloudNight, cloudDay, dayFactor);
    color = mix(color, cloudTint + sunsetScatter * 0.35, cloud * (0.25 + 0.55 * dayFactor));

    float sunDisc = smoothstep(SkySunDiscSize, 1.0, dot(dir, sunDir));
    float sunHalo = pow(max(dot(dir, sunDir), 0.0), 64.0);
    color += vec3(1.0, 0.92, 0.78) * (sunDisc * 12.0 + sunHalo * 0.75) * dayFactor;

    float moonDisc = smoothstep(SkyMoonDiscSize, 1.0, dot(dir, moonDir));
    float moonGlow = pow(max(dot(dir, moonDir), 0.0), 48.0);
    color += vec3(0.74, 0.79, 0.94) * (moonDisc * 2.8 + moonGlow * 0.35) * nightFactor;

    float skyBlend = clamp(dayFactor + duskFactor * 0.35, 0.0, 1.0);
    vec3 fallbackColor = mix(NightSky(dir, nightFactor), skyBase, skyBlend);
    color = mix(NightSky(dir, nightFactor), color, skyBlend);
    if (any(isnan(color)) || any(isinf(color)))
        color = fallbackColor;

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
