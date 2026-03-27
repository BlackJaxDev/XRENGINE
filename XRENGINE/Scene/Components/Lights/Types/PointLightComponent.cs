using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights.Types
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.PointLightComponentEditor")]
    [Category("Lighting")]
    [DisplayName("Point Light")]
    [Description("Emits omnidirectional light with optional shadow maps for local illumination.")]
    public class PointLightComponent : LightComponent
    {
        protected readonly XRViewport[] _viewports = new XRViewport[6].Fill(x => new(null, 1024, 1024)
        {
            RenderPipeline = new ShadowRenderPipeline(),
            SetRenderPipelineFromCamera = false,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
        });

        private bool _useGeometryShader = true;
        private XRFrameBuffer? _perFaceFbo;

        /// <summary>
        /// When enabled, renders all 6 cubemap shadow faces in a single draw call
        /// using a geometry shader. When disabled, renders each face separately in
        /// 6 passes (compatible with GPUs/drivers that lack geometry shader support).
        /// </summary>
        [Category("Shadows")]
        [DisplayName("Use Geometry Shader")]
        [Description("Render all 6 cubemap faces in one draw call via geometry shader. Disable for 6-pass fallback.")]
        public bool UseGeometryShader
        {
            get => _useGeometryShader;
            set
            {
                if (SetField(ref _useGeometryShader, value))
                    RecreateShadowMapMaterial();
            }
        }

        private void RecreateShadowMapMaterial()
        {
            if (ShadowMap is null)
                return;

            uint res = (uint)_viewports[0].Width;
            ShadowMap = new XRMaterialFrameBuffer(GetShadowMapMaterial(res, res));
        }

        public override void CollectVisibleItems()
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            if (_useGeometryShader)
            {
                // GS renders all 6 cubemap faces per draw call — one viewport
                // with the influence sphere captures objects visible from any face.
                _viewports[0].CollectVisible(
                    collectMirrors: false,
                    collectionVolumeOverride: _influenceVolume);
            }
            else
            {
                // Without GS each viewport renders one face with its own camera frustum.
                for (int i = 0; i < 6; i++)
                    _viewports[i].CollectVisible(collectMirrors: false);
            }
        }
        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            if (_useGeometryShader)
            {
                _viewports[0].SwapBuffers();
            }
            else
            {
                for (int i = 0; i < 6; i++)
                    _viewports[i].SwapBuffers();
            }
            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }
        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            if (_useGeometryShader)
            {
                // Single draw: the GS writes to all 6 cubemap layers via gl_Layer.
                int cmdCount = _viewports[0].RenderPipelineInstance.MeshRenderCommands.GetRenderingCommandCount();

                Debug.RenderingEvery(
                    $"PointLight.GSShadow.{GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[ShadowDiag] GS shadow pass. RenderingCmds={0} VP0.Camera={1} VP0.World={2} ShadowMapNull={3} InfluenceRadius={4:F1} InfluenceCenter={5}",
                    cmdCount,
                    _viewports[0].ActiveCamera is not null,
                    _viewports[0].World is not null,
                    ShadowMap is null,
                    _influenceVolume.Radius,
                    _influenceVolume.Center);

                _viewports[0].Render(ShadowMap, null, null, true, ShadowMap!.Material);
            }
            else
            {
                // 6-pass fallback: render each face individually.
                _perFaceFbo ??= new XRFrameBuffer();
                var mat = ShadowMap.Material!;
                var depthCube = (IFrameBufferAttachement)mat.Textures[0]!;
                var shadowCube = (IFrameBufferAttachement)mat.Textures[1]!;
                for (int i = 0; i < 6; i++)
                {
                    _perFaceFbo.SetRenderTargets(
                        (depthCube, EFrameBufferAttachment.DepthAttachment, 0, i),
                        (shadowCube, EFrameBufferAttachment.ColorAttachment0, 0, i));
                    _viewports[i].Render(_perFaceFbo, null, null, true, mat);
                }
            }
        }

        /// <summary>
        /// The distance beyond which this light has no visible effect.
        /// </summary>
        [Category("Attenuation")]
        [DisplayName("Radius")]
        [Description("Distance beyond which the light has no effect.")]
        public float Radius
        {
            get => _influenceVolume.Radius;
            set
            {
                _influenceVolume.Radius = value;
                foreach (var cam in ShadowCameras)
                    cam.FarZ = value;
                MeshCenterAdjustMatrix = Matrix4x4.CreateScale(Radius);
            }
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            uint max = Math.Max(width, height);

            // Cubemap textures use immutable storage (Resizable=false) and cannot
            // be resized in place. Destroy the old FBO so the base recreates it
            // with fresh textures of the new size.
            ShadowMap?.Destroy();
            ShadowMap = null;

            base.SetShadowMapResolution(max, max);

            foreach (var vp in _viewports)
                vp.Resize(max, max);
        }

        private float _brightness = 1.0f;
        /// <summary>
        /// Intensity multiplier for this light.
        /// </summary>
        [Category("Attenuation")]
        [DisplayName("Brightness")]
        [Description("Intensity multiplier applied to the light output.")]
        public float Brightness
        {
            get => _brightness;
            set => SetField(ref _brightness, value);
        }

        public static XRMesh GetVolumeMesh()
            => XRMesh.Shapes.SolidSphere(Vector3.Zero, 1.0f, 32);
        protected override XRMesh GetWireframeMesh()
            => XRMesh.Shapes.WireframeSphere(Vector3.Zero, Radius, 32);

        public XRCamera[] ShadowCameras { get; private set; }

        private Sphere _influenceVolume;

        public PointLightComponent()
            : this(100.0f, 1.0f) { }
        public PointLightComponent(float radius, float brightness)
            : base()
        {
            _influenceVolume = new Sphere(Vector3.Zero, radius);
            Brightness = brightness;

            PositionOnlyTransform positionTransform = new(Transform);
            ShadowCameras = XRCubeFrameBuffer.GetCamerasPerFace(0.1f, radius, true, positionTransform);
            for (int i = 0; i < ShadowCameras.Length; i++)
            {
                var cam = ShadowCameras[i];
                cam.CullingMask = DefaultLayers.EverythingExceptGizmos;
                _viewports[i].Camera = cam;
                var colorStage = cam.GetPostProcessStageState<ColorGradingSettings>();
                if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true && grading is not null)
                {
                    grading.AutoExposure = false;
                    grading.Exposure = 1.0f;
                }
                else
                {
                    colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
                    colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
                }
            }

            MeshCenterAdjustMatrix = Matrix4x4.CreateScale(radius);
        }

        protected override void OnTransformChanged()
        {
            PositionOnlyTransform positionTransform = new(Transform);
            foreach (var cam in ShadowCameras)
                cam.Transform.Parent = positionTransform;
            base.OnTransformChanged();
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _influenceVolume.Center = renderMatrix.Translation;
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = WorldAs<XREngine.Rendering.XRWorldInstance>();
        }
        protected override void OnComponentDeactivated()
        {
            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = null;

            base.OnComponentDeactivated();
        }

        protected override void RegisterDynamicLight(XRWorldInstance world)
            => world.Lights.DynamicPointLights.Add(this);

        protected override void UnregisterDynamicLight(XRWorldInstance world)
            => world.Lights.DynamicPointLights.Remove(this);

        /// <summary>
        /// This is to set uniforms in the GBuffer lighting shader 
        /// or in a forward shader that requests lighting uniforms.
        /// </summary>
        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            string prefix = targetStructName ?? Engine.Rendering.Constants.LightsStructName;
            string flatPrefix = $"{prefix}.";
            string basePrefix = $"{prefix}.Base.";

            // Legacy flat uniforms.
            program.Uniform($"{flatPrefix}Color", _color);
            program.Uniform($"{flatPrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{flatPrefix}Position", _influenceVolume.Center);
            program.Uniform($"{flatPrefix}Radius", _influenceVolume.Radius);
            program.Uniform($"{flatPrefix}Brightness", _brightness);

            // Structured Base.* uniforms for ForwardLighting snippet compatibility.
            program.Uniform($"{basePrefix}Color", _color);
            program.Uniform($"{basePrefix}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{basePrefix}AmbientIntensity", 0.0f);
            program.Uniform($"{prefix}.Position", _influenceVolume.Center);
            program.Uniform($"{prefix}.Radius", _influenceVolume.Radius);
            program.Uniform($"{prefix}.Brightness", _brightness);
            // Note: Shadow map sampler and LightHasShadowMap are bound by the caller (deferred pass)
            // to avoid overwriting material texture units.
        }

        /// <summary>
        /// This is to set special uniforms each time any mesh is rendered 
        /// with the shadow depth shader during the shadow pass.
        /// </summary>
        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            program.Uniform("FarPlaneDist", _influenceVolume.Radius);
            program.Uniform("LightPos", _influenceVolume.Center);
            if (_useGeometryShader)
            {
                for (int i = 0; i < ShadowCameras.Length; ++i)
                {
                    var cam = ShadowCameras[i];
                    // Precompute VP on CPU — avoids per-vertex inverse() in the geometry shader.
                    Matrix4x4.Invert(cam.Transform.RenderMatrix, out Matrix4x4 viewMatrix);
                    Matrix4x4 vp = viewMatrix * cam.ProjectionMatrix;
                    program.Uniform($"ViewProjectionMatrices[{i}]", vp);
                }
            }
        }
        public override XRMaterial GetShadowMapMaterial(uint width, uint height, EDepthPrecision precision = EDepthPrecision.Int24)
        {
            uint cubeExtent = Math.Max(width, height);
            XRTexture[] refs =
            [
                new XRTextureCube(cubeExtent, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedByte, false)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                    Resizable = false,
                },
                new XRTextureCube(cubeExtent, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, false)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap",
                    Resizable = false,
                },
            ];

            //This material is used for rendering to the framebuffer.
            // No custom vertex shader — the engine auto-generates one that outputs
            // FragPos (location 0) in world-space and sets gl_Position to clip-space,
            // which is what both the GS and FS paths expect.
            XRShader fragShader = XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment);
            XRMaterial mat;
            if (_useGeometryShader)
            {
                XRShader geomShader = XRShader.EngineShader("PointLightShadowDepth.gs", EShaderType.Geometry);
                mat = new(refs, fragShader, geomShader);
            }
            else
            {
                mat = new(refs, fragShader);
            }

            //No culling so if a light exists inside of a mesh it will shadow everything.
            mat.RenderOptions.CullMode = ECullMode.None;

            return mat;
        }

        internal override void BuildShadowFrusta(List<PreparedFrustum> output)
        {
            output.Clear();

            if (ShadowCameras is null || ShadowCameras.Length == 0)
                return;

            for (int i = 0; i < ShadowCameras.Length; i++)
            {
                var cam = ShadowCameras[i];
                output.Add(cam.WorldFrustum().Prepare());
            }
        }

    }
}
