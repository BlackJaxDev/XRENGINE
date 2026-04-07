using MIConvexHull;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Timers;

namespace XREngine.Components.Capture.Lights
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.LightProbeComponentEditor")]
    public partial class LightProbeComponent : SceneCaptureComponent, IRenderable, IVertex
    {
        #region Nested Types

        public enum ERenderPreview
        {
            Environment,
            Irradiance,
            Prefilter,
        }

        public enum EInfluenceShape
        {
            Sphere,
            Box,
        }

        /// <summary>
        /// HDR encoding format for baked/static probes. Dynamic captures use Rgb16f.
        /// </summary>
        public enum EHdrEncoding
        {
            /// <summary>Default full-precision half-float RGB.</summary>
            Rgb16f,
            /// <summary>RGBM encoding (RGB * M in alpha, 8-bit per channel).</summary>
            RGBM,
            /// <summary>RGBE shared-exponent encoding.</summary>
            RGBE,
            /// <summary>YCoCg color space encoding.</summary>
            YCoCg,
        }

        private readonly record struct FaceDebugInfo(string Name, ColorF4 Color);

        #endregion

        #region Constants

        private const uint OctahedralResolutionMultiplier = 2u;
        private const string FullscreenCubeVertexShaderSource =
            """
            #version 450

            layout(location = 0) in vec3 Position;
            layout(location = 20) out vec3 FragPosLocal;

            uniform mat4 ModelMatrix;
            uniform mat4 ViewProjectionMatrix_VTX;

            void main()
            {
                FragPosLocal = Position;
                gl_Position = ViewProjectionMatrix_VTX * ModelMatrix * vec4(Position, 1.0);
            }
            """;
        private const string CubemapToOctaShaderSource =
            """
            #version 450

            #pragma snippet "OctahedralMapping"

            layout(location = 0) in vec3 FragPos;
            layout(location = 0) out vec4 OutColor;

            uniform samplerCube Texture0;

            void main()
            {
                vec2 clipXY = FragPos.xy;
                if (clipXY.x < -1.0f || clipXY.x > 1.0f || clipXY.y < -1.0f || clipXY.y > 1.0f)
                {
                    discard;
                }

                vec2 uv = clipXY * 0.5f + 0.5f;
                vec3 dir = XRENGINE_DecodeOcta(uv);
                OutColor = vec4(texture(Texture0, dir).rgb, 1.0f);
            }
            """;
        private const string IrradianceCubemapFragmentShaderSource =
            """
            #version 450

            layout (location = 0) out vec3 OutColor;
            layout (location = 20) in vec3 FragPosLocal;

            uniform samplerCube Texture0;

            const float PI = 3.14159265359f;

            void main()
            {
                vec3 N = normalize(FragPosLocal);

                vec3 irradiance = vec3(0.0f);

                vec3 up    = vec3(0.0f, 1.0f, 0.0f);
                vec3 right = cross(up, N);
                up         = cross(N, right);

                float sampleDelta = 0.025f;
                int numSamples = 0;
                for (float phi = 0.0f; phi < 2.0f * PI; phi += sampleDelta)
                {
                    for (float theta = 0.0f; theta < 0.5f * PI; theta += sampleDelta)
                    {
                        float tanX = sin(theta) * cos(phi);
                        float tanY = sin(theta) * sin(phi);
                        float tanZ = cos(theta);

                        vec3 sampleVec = tanX * right + tanY * up + tanZ * N;

                        irradiance += texture(Texture0, sampleVec).rgb * cos(theta) * sin(theta);
                        ++numSamples;
                    }
                }

                OutColor = irradiance * vec3(PI / float(numSamples));
            }
            """;
        private const string PrefilterCubemapFragmentShaderSource =
            """
            #version 450

            layout (location = 0) out vec3 OutColor;
            layout (location = 20) in vec3 FragPosLocal;

            uniform samplerCube Texture0;
            uniform float Roughness = 0.0f;
            uniform int CubemapDim = 512;

            const float PI = 3.14159265359f;

            float DistributionGGX(vec3 N, vec3 H, float roughness)
            {
                float a = roughness * roughness;
                float a2 = a * a;
                float NdotH = max(dot(N, H), 0.0f);
                float NdotH2 = NdotH * NdotH;

                float nom   = a2;
                float denom = (NdotH2 * (a2 - 1.0f) + 1.0f);
                denom = PI * denom * denom;

                return nom / denom;
            }

            float RadicalInverse_VdC(uint bits)
            {
                 bits = (bits << 16u) | (bits >> 16u);
                 bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
                 bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
                 bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
                 bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
                 return float(bits) * 2.3283064365386963e-10f;
            }

            vec2 Hammersley(uint i, uint N)
            {
                return vec2(float(i) / float(N), RadicalInverse_VdC(i));
            }

            vec3 ImportanceSampleGGX(vec2 Xi, vec3 N, float roughness)
            {
                float a = roughness * roughness;

                float phi = 2.0f * PI * Xi.x;
                float cosTheta = sqrt((1.0f - Xi.y) / (1.0f + (a * a - 1.0f) * Xi.y));
                float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

                vec3 H;
                H.x = cos(phi) * sinTheta;
                H.y = sin(phi) * sinTheta;
                H.z = cosTheta;

                vec3 up        = abs(N.z) < 0.999f ? vec3(0.0f, 0.0f, 1.0f) : vec3(1.0f, 0.0f, 0.0f);
                vec3 tangent   = normalize(cross(up, N));
                vec3 bitangent = cross(N, tangent);

                vec3 sampleVec = tangent * H.x + bitangent * H.y + N * H.z;
                return normalize(sampleVec);
            }

            void main()
            {
                vec3 N = normalize(FragPosLocal);
                vec3 R = N;
                vec3 V = R;

                const uint SAMPLE_COUNT = 1024u;
                vec3 prefilteredColor = vec3(0.0f);
                float totalWeight = 0.0f;

                for (uint i = 0u; i < SAMPLE_COUNT; ++i)
                {
                    vec2 Xi = Hammersley(i, SAMPLE_COUNT);
                    vec3 H  = ImportanceSampleGGX(Xi, N, Roughness);
                    vec3 L  = normalize(2.0f * dot(V, H) * H - V);

                    float NdotL = dot(N, L);
                    if (NdotL > 0.0f)
                    {
                        float D     = DistributionGGX(N, H, Roughness);
                        float NdotH = max(dot(N, H), 0.0f);
                        float HdotV = max(dot(H, V), 0.0f);
                        float pdf   = D * NdotH / (4.0f * HdotV) + 0.0001f;

                        float res      = float(CubemapDim);
                        float saTexel  = 4.0f * PI / (6.0f * res * res);
                        float saSample = 1.0f / (float(SAMPLE_COUNT) * pdf + 0.0001f);

                        float mipLevel = Roughness == 0.0f ? 0.0f : 0.5f * log2(saSample / saTexel);

                        prefilteredColor += textureLod(Texture0, L, mipLevel).rgb * NdotL;
                        totalWeight      += NdotL;
                    }
                }

                OutColor = prefilteredColor / totalWeight;
            }
            """;

        #endregion

        #region Static Fields

        private static XRShader? s_fullscreenTriVertexShader;
        private static XRShader? s_fullscreenCubeVertexShader;
        private static XRShader? s_cubemapToOctaShader;
        private static XRShader? s_irradianceCubemapFragmentShader;
        private static XRShader? s_irradianceCubemapToOctaFragmentShader;
        private static XRShader? s_prefilterCubemapFragmentShader;
        private static XRShader? s_prefilterCubemapToOctaFragmentShader;

        private static readonly FaceDebugInfo[] s_faceDebugInfos =
        [
            new("+X", ColorF4.LightRed),
            new("-X", ColorF4.DarkRed),
            new("+Y", ColorF4.LightGreen),
            new("-Y", ColorF4.DarkGreen),
            new("+Z", ColorF4.LightBlue),
            new("-Z", ColorF4.DarkBlue),
        ];

        #endregion

        #region Instance Fields

        private readonly RenderCommandMesh3D _visualRC;
        private readonly RenderCommandMethod3D _debugAxesCommand;
        private readonly RenderCommandMethod3D _debugInfluenceCommand;
        private readonly GameTimer _realtimeCaptureTimer;
        private readonly GameTimer _startupCaptureTimer;

        private bool _parallaxCorrectionEnabled = false;
        private Vector3 _proxyBoxCenterOffset = Vector3.Zero;
        private Vector3 _proxyBoxHalfExtents = Vector3.One;
        private Quaternion _proxyBoxRotation = Quaternion.Identity;

        private EInfluenceShape _influenceShape = EInfluenceShape.Sphere;
        private Vector3 _influenceOffset = Vector3.Zero;
        private float _influenceSphereInnerRadius = 0.0f;
        private float _influenceSphereOuterRadius = 5.0f;
        private Vector3 _influenceBoxInnerExtents = Vector3.Zero;
        private Vector3 _influenceBoxOuterExtents = new(5.0f, 5.0f, 5.0f);

        // HDR encoding & normalization
        private EHdrEncoding _hdrEncoding = EHdrEncoding.Rgb16f;
        private bool _normalizedCubemap = false;
        private float _normalizationScale = 1.0f;

        // Mip streaming state
        private int _streamedMipLevel = 0;
        private int _targetMipLevel = 0;
        private bool _streamHighMipsOnDemand = false;

        private bool _autoShowPreviewOnSelect = true;
        private bool _renderInfluenceOnSelection = true;
        private bool _autoCaptureOnActivate = true;
        private bool _realtime = false;
        private TimeSpan? _realTimeUpdateInterval = TimeSpan.FromMilliseconds(100.0f);
        private bool _useDirectCubemapIblGeneration = true;
        private bool _releaseTransientEnvironmentTexturesAfterCapture = true;
        private uint _irradianceResolution = 32;
        private ERenderPreview _previewDisplay = ERenderPreview.Environment;

        private XRQuadFrameBuffer? _irradianceFBO;
        private XRQuadFrameBuffer? _prefilterFBO;
        private XRCubeFrameBuffer? _irradianceCubeFBO;
        private XRCubeFrameBuffer? _prefilterCubeFBO;
        private int _prefilterSourceDimension = 1;
        private XRTexture? _irradianceSourceTexture;
        private XRTexture? _prefilterSourceTexture;
        private XRTexture2D? _irradianceTexture;
        private XRTexture2D? _prefilterTexture;
        private XRTextureCube? _irradianceTextureCubemap;
        private XRTextureCube? _prefilterTextureCubemap;
        private XRMeshRenderer? _previewSphere;
        private XRTexture2D? _environmentTextureEquirect;
        private bool _useCubemapConvolution;

        private XRWorldInstance? _registeredWorld;

        #endregion

        #region Constructor

        public LightProbeComponent() : base()
        {
            _realtimeCaptureTimer = new GameTimer(this);
            _startupCaptureTimer = new GameTimer(this);
            _debugAxesCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderCameraOrientationDebug)
            {
                Enabled = false,
            };
            _debugInfluenceCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderVolumesDebug)
            {
                Enabled = false,
            };
            RenderedObjects =
            [
                VisualRenderInfo = RenderInfo3D.New(this, _visualRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)),
            ];
            VisualRenderInfo.Layer = DefaultLayers.GizmosIndex;
            VisualRenderInfo.RenderCommands.Add(_debugAxesCommand);
            VisualRenderInfo.RenderCommands.Add(_debugInfluenceCommand);
            VisualRenderInfo.PreCollectCommandsCallback += OnPreCollectRenderInfo;
        }

        #endregion

        #region IVertex Implementation

        double[] IVertex.Position => [Transform.WorldTranslation.X, Transform.WorldTranslation.Y, Transform.WorldTranslation.Z];

        #endregion

        #region IRenderable Properties

        public RenderInfo3D VisualRenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }

        #endregion
    }
}