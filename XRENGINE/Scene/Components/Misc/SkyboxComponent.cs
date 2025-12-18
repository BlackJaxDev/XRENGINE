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

        private ESkyboxProjection _projection = ESkyboxProjection.Equirectangular;
        private XRTexture? _texture;
        private float _intensity = 1.0f;
        private float _rotation = 0.0f;
        private int _cubemapArrayLayer = 0;
        private bool _debugSolidColor = false;

        // Cached shaders
        private static XRShader? s_vertexShader;
        private static XRShader? s_equirectShader;
        private static XRShader? s_octahedralShader;
        private static XRShader? s_cubemapShader;
        private static XRShader? s_cubemapArrayShader;
        private static XRShader? s_debugShader;

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

        /// <summary>
        /// Debug mode: renders a simple gradient (no texture sampling) to verify the fullscreen triangle is drawing.
        /// </summary>
        [Category("Skybox")]
        [DisplayName("Debug Solid Color")]
        [Description("When enabled, the skybox draws a debug gradient instead of sampling the texture.")]
        public bool DebugSolidColor
        {
            get => _debugSolidColor;
            set => SetField(ref _debugSolidColor, value);
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
                case nameof(Projection):
                case nameof(Texture):
                case nameof(DebugSolidColor):
                    RebuildMaterial();
                    break;
                case nameof(Intensity):
                case nameof(Rotation):
                case nameof(CubemapArrayLayer):
                    // These are set via uniforms, no rebuild needed
                    break;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            AttachDebugHooks();

            if (!_loggedActivated)
            {
                _loggedActivated = true;
                Debug.Out(EOutputVerbosity.Normal, "[Skybox] Activated. DebugSolidColor={0} Pass={1}", _debugSolidColor, _renderCommand?.RenderPass ?? -1);
            }

            RebuildAll();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            CleanupResources();
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

        private void OnCollectedForRender(RenderInfo info, RenderCommand command, XRCamera? camera)
        {
            if (_loggedCollected)
                return;

            _loggedCollected = true;
            Debug.Out(EOutputVerbosity.Normal, "[Skybox] CollectedForRender. CmdPass={0} Camera={1}", command.RenderPass, camera?.ToString() ?? "<null>");
        }

        private void OnPreRender()
        {
            if (_loggedRendered)
                return;

            _loggedRendered = true;
            Debug.Out(EOutputVerbosity.Normal, "[Skybox] RenderCommand.PreRender fired (draw executing). Pass={0}", _renderCommand?.RenderPass ?? -1);
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
            XRShader? vertexShader = GetVertexShader();
            XRShader? fragmentShader = _debugSolidColor ? GetDebugShader() : GetFragmentShaderForProjection(_projection);
            
            if (vertexShader is null || fragmentShader is null)
            {
                Debug.LogWarning($"SkyboxComponent: Failed to load shaders for projection {_projection}");
                return;
            }

            XRTexture? tex = _debugSolidColor ? null : (_texture ?? CreateDefaultTexture(_projection));

            RenderingParameters renderParams = new()
            {
                CullMode = ECullMode.None, // Fullscreen triangle has no meaningful face culling
                DepthTest = new DepthTest()
                {
                    Enabled = ERenderParamUsage.Enabled,
                    UpdateDepth = false,
                    // In normal mode we want the sky behind geometry; in debug mode we want a definitive signal.
                    Function = _debugSolidColor ? EComparison.Always : EComparison.Lequal,
                },
                // Skybox fragment shaders reconstruct view rays, so they require camera matrices.
                RequiredEngineUniforms = EUniformRequirements.Camera,
            };

            _material = new XRMaterial(tex is not null ? [tex] : [], vertexShader, fragmentShader)
            {
                RenderPass = _debugSolidColor ? (int)EDefaultRenderPass.OpaqueForward : (int)EDefaultRenderPass.Background,
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

        private static XRShader GetDebugShader()
        {
            if (s_debugShader is not null)
                return s_debugShader;
            
            s_debugShader = Engine.Assets.LoadEngineAsset<XRShader>(
                JobPriority.Highest,
                "Shaders", "Scene3D", "SkyboxDebug.fs");

            // Inline fallback shader
            return s_debugShader ??= new XRShader(EShaderType.Fragment, DebugShaderSource);
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
        /// Vertex shader that outputs clip-space position and passes through for view ray reconstruction.
        /// </summary>
        private const string VertexShaderSource = @"
#version 450

layout(location = 0) in vec3 Position;
layout(location = 0) out vec3 FragClipPos;

void main()
{
    FragClipPos = Position;
    // Output at maximum depth (z=1) so skybox is behind everything
    gl_Position = vec4(Position.xy, 1.0, 1.0);
}
";

        private const string EquirectShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;

// Camera matrices - InverseViewMatrix is the camera's world transform
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

const float PI = 3.14159265359;

vec3 GetWorldDirection(vec3 clipPos)
{
    // Reconstruct view-space ray direction from clip coordinates
    // Use inverse projection to go from clip space to view space
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    
    // Transform view direction to world space using camera's world transform
    // InverseViewMatrix is the camera's world matrix (position + orientation)
    mat3 camRotation = mat3(InverseViewMatrix);
    return normalize(camRotation * viewDir);
}

void main()
{
    vec3 dir = GetWorldDirection(FragClipPos);
    
    // Apply rotation around Y axis
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    dir = vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot
    );

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
layout (location = 0) in vec3 FragClipPos;

uniform sampler2D Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;

// Camera matrices
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 GetWorldDirection(vec3 clipPos)
{
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    mat3 camRotation = mat3(InverseViewMatrix);
    return normalize(camRotation * viewDir);
}

vec2 EncodeOcta(vec3 dir)
{
    dir = normalize(dir);
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
    vec3 dir = GetWorldDirection(FragClipPos);
    
    // Apply rotation around Y axis
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    dir = vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot
    );

    vec2 uv = EncodeOcta(dir);
    OutColor = texture(Texture0, uv).rgb * SkyboxIntensity;
}
";

        private const string CubemapShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform samplerCube Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;

// Camera matrices
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 GetWorldDirection(vec3 clipPos)
{
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    mat3 camRotation = mat3(InverseViewMatrix);
    return normalize(camRotation * viewDir);
}

void main()
{
    vec3 dir = GetWorldDirection(FragClipPos);
    
    // Apply rotation around Y axis
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    dir = vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot
    );

    OutColor = texture(Texture0, dir).rgb * SkyboxIntensity;
}
";

        private const string CubemapArrayShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

uniform samplerCubeArray Texture0;
uniform float SkyboxIntensity = 1.0;
uniform float SkyboxRotation = 0.0;
uniform int CubemapLayer = 0;

// Camera matrices
uniform mat4 InverseViewMatrix;
uniform mat4 ProjMatrix;

vec3 GetWorldDirection(vec3 clipPos)
{
    mat4 invProj = inverse(ProjMatrix);
    vec4 viewPos = invProj * vec4(clipPos.xy, 1.0, 1.0);
    vec3 viewDir = normalize(viewPos.xyz / viewPos.w);
    mat3 camRotation = mat3(InverseViewMatrix);
    return normalize(camRotation * viewDir);
}

void main()
{
    vec3 dir = GetWorldDirection(FragClipPos);
    
    // Apply rotation around Y axis
    float cosRot = cos(SkyboxRotation);
    float sinRot = sin(SkyboxRotation);
    dir = vec3(
        dir.x * cosRot - dir.z * sinRot,
        dir.y,
        dir.x * sinRot + dir.z * cosRot
    );

    OutColor = texture(Texture0, vec4(dir, float(CubemapLayer))).rgb * SkyboxIntensity;
}
";

        private const string DebugShaderSource = @"
#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

void main()
{
    float x = clamp(FragClipPos.x * 0.25 + 0.5, 0.0, 1.0);
    float y = clamp(FragClipPos.y * 0.25 + 0.5, 0.0, 1.0);
    OutColor = vec3(x, y, 1.0);
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
