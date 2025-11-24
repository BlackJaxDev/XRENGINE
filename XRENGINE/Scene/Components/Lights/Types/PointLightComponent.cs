using Extensions;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights.Types
{
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
        public override void CollectVisibleItems()
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            foreach (var vp in _viewports)
                vp.CollectVisible(false);
        }
        public override void SwapBuffers()
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            foreach (var vp in _viewports)
                vp.SwapBuffers();
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

            foreach (var vp in _viewports)
                vp.Render(ShadowMap, null, null, true, ShadowMap.Material);
        }

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
            base.SetShadowMapResolution(max, max);
            foreach (var vp in _viewports)
                vp.Resize(max, max);
        }

        private float _brightness = 1.0f;
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
                _viewports[i].Camera = cam;
                cam.PostProcessing = new PostProcessingSettings();
                cam.PostProcessing.ColorGrading.AutoExposure = false;
                cam.PostProcessing.ColorGrading.Exposure = 1.0f;
            }

            ShadowExponentBase = 1.0f;
            ShadowExponent = 2.5f;
            ShadowMinBias = 0.05f;
            ShadowMaxBias = 10.0f;

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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicPointLights.Add(this);

            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = World;
        }
        protected internal override void OnComponentDeactivated()
        {
            if (Type == ELightType.Dynamic)
                World?.Lights.DynamicPointLights.Remove(this);

            for (int i = 0; i < _viewports.Length; i++)
                _viewports[i].WorldInstanceOverride = null;

            base.OnComponentDeactivated();
        }

        /// <summary>
        /// This is to set uniforms in the GBuffer lighting shader 
        /// or in a forward shader that requests lighting uniforms.
        /// </summary>
        public override void SetUniforms(XRRenderProgram program, string? targetStructName = null)
        {
            base.SetUniforms(program, targetStructName);

            targetStructName = $"{targetStructName ?? Engine.Rendering.Constants.LightsStructName}.";

            program.Uniform($"{targetStructName}Color", _color);
            program.Uniform($"{targetStructName}DiffuseIntensity", _diffuseIntensity);
            program.Uniform($"{targetStructName}Position", _influenceVolume.Center);
            program.Uniform($"{targetStructName}Radius", _influenceVolume.Radius);
            program.Uniform($"{targetStructName}Brightness", _brightness);

            var mat = ShadowMap?.Material;
            if (mat is null || mat.Textures.Count < 2)
                return;
            
            var tex = mat.Textures[1];
            if (tex is not null)
                program.Sampler("ShadowMap", tex, 4);
        }

        /// <summary>
        /// This is to set special uniforms each time any mesh is rendered 
        /// with the shadow depth shader during the shadow pass.
        /// </summary>
        protected override void SetShadowMapUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            program.Uniform("FarPlaneDist", _influenceVolume.Radius);
            program.Uniform("LightPos", _influenceVolume.Center);
            for (int i = 0; i < ShadowCameras.Length; ++i)
            {
                var cam = ShadowCameras[i];
                program.Uniform($"InverseViewMatrices[{i}]", cam.Transform.RenderMatrix);
                program.Uniform($"ProjectionMatrices[{i}]", cam.ProjectionMatrix);
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
                },
                new XRTextureCube(cubeExtent, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, false)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
                    FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
                    SamplerName = "ShadowMap"
                },
            ];

            //This material is used for rendering to the framebuffer.
            XRShader fragShader = XRShader.EngineShader("PointLightShadowDepth.fs", EShaderType.Fragment);
            XRShader geomShader = XRShader.EngineShader("PointLightShadowDepth.gs", EShaderType.Geometry);
            XRMaterial mat = new(refs, fragShader, geomShader);

            //No culling so if a light exists inside of a mesh it will shadow everything.
            mat.RenderOptions.CullMode = ECullMode.None;

            return mat;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Type):
                        if (Type == ELightType.Dynamic)
                            World?.Lights.DynamicPointLights.Add(this);
                        else
                            World?.Lights.DynamicPointLights.Remove(this);
                        break;
                }
            }
            return change;
        }
    }
}
