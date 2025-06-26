using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// A component that renders a decal onto the scene using deferred rendering.
    /// Default material projects a single texture onto surfaces with optional transparency,
    /// but also supports custom materials in case you want to use a custom normal map or other effects.
    /// The first 4 textures should be left null for deferred rendering: 
    /// 1: Viewport's Albedo/Opacity texture; 
    /// 2: Viewport's Normal texture; 
    /// 3: Viewport's RMSI texture; 
    /// 4: Viewport's Depth texture; 
    /// </summary>
    public class DeferredDecalComponent : XRComponent, IRenderable
    {
        private XRMaterial? _material;
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        /// <summary>
        /// Creates a new <see cref="DeferredDecalComponent"/>.
        /// </summary>
        public DeferredDecalComponent()
        {
            RenderInfo = RenderInfo3D.New(this, RenderCommandDecal);
            DebugRenderInfo = RenderInfo3D.New(this, DebugRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, RenderDebug));
            RenderedObjects = [RenderInfo, DebugRenderInfo];
        }

        /// <summary>
        /// Creates a new <see cref="DeferredDecalComponent"/> with the specified projection depth and a texture to project.
        /// The projection box's width and height will be set to the texture's width and height, respectively.
        /// </summary>
        /// <param name="depth"></param>
        /// <param name="texture"></param>
        public void SetTexture(float depth, XRTexture2D texture)
        {
            if (texture is null)
                return;
            
            HalfExtents = new Vector3(texture.Width * 0.5f, depth, texture.Height * 0.5f);
            Material = CreateDefaulMaterial(texture);
        }
        public void SetTexture(XRTexture2D texture)
        {
            if (texture is null)
                return;

            HalfExtents = new Vector3(texture.Width * 0.5f, Math.Max(texture.Width, texture.Height) * 0.5f, texture.Height * 0.5f);
            Material = CreateDefaulMaterial(texture);
        }

        private Vector3 _halfExtents = Vector3.One;
        /// <summary>
        /// The half-extents of the projection box.
        /// </summary>
        public Vector3 HalfExtents
        {
            get => _halfExtents;
            set => SetField(ref _halfExtents, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(HalfExtents):
                    UpdateRenderCommandMatrix();
                    break;
            }
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            UpdateRenderCommandMatrix();
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        private void UpdateRenderCommandMatrix()
        {
            RenderCommandDecal.WorldMatrix = Matrix4x4.CreateScale(HalfExtents) * Transform.RenderMatrix;
            RenderInfo.CullingOffsetMatrix = Transform.RenderMatrix;
            RenderInfo.LocalCullingVolume = new AABB(-HalfExtents, HalfExtents);
        }

        /// <summary>
        /// Gets the engine's default shader for deferred decals.
        /// </summary>
        /// <returns></returns>
        public static XRShader GetDefaultShader()
            => XRShader.EngineShader(Path.Combine("Scene3D", "DeferredDecal.fs"), EShaderType.Fragment);

        /// <summary>
        /// Generates a basic decal material that projects a single texture onto surfaces. Texture may use transparency.
        /// </summary>
        /// <param name="albedo">The texture to project as a decal.</param>
        /// <returns>The <see cref="XRMaterial"/> to be used with a <see cref="DeferredDecalComponent"/>.</returns>
        public static XRMaterial CreateDefaulMaterial(XRTexture2D albedo)
        {
            XRTexture?[] decalRefs =
            [
                null, //Viewport's Albedo/Opacity texture
                null, //Viewport's Normal texture
                null, //Viewport's RMSI texture
                null, //Viewport's Depth texture
                albedo
            ];
            ShaderVar[] decalVars = [];
            RenderingParameters decalRenderParams = new()
            {
                CullMode = ECullMode.Front,
                RequiredEngineUniforms = EUniformRequirements.Camera,
                DepthTest = new DepthTest() { Enabled = ERenderParamUsage.Disabled }
            };
            return new XRMaterial(decalVars, decalRefs, GetDefaultShader())
            {
                Name = "MAT_DeferredDecal",
                RenderOptions = decalRenderParams,
                RenderPass = (int)EDefaultRenderPass.DeferredDecals
            };
        }

        protected internal override void OnComponentActivated()
        {
            if (Material is null)
                return;

            RenderCommandDecal.Mesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(-Vector3.One, Vector3.One), Material);
            RenderCommandDecal.Mesh.SettingUniforms += DecalManager_SettingUniforms;

            base.OnComponentActivated();
        }

        protected virtual void DecalManager_SettingUniforms(XRRenderProgram vtxProg, XRRenderProgram matProg)
        {
            if (matProg is null)
                return;

            var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is null)
                return;

            var albedoOpacityTexture = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.AlbedoOpacityTextureName);
            var normalTexture = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.NormalTextureName);
            var rmseTexture = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.RMSETextureName);
            var depthViewTexture = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DepthViewTextureName);

            if (albedoOpacityTexture is null || normalTexture is null || rmseTexture is null || depthViewTexture is null)
                return;

            matProg.Sampler("Texture0", albedoOpacityTexture, 0);
            matProg.Sampler("Texture1", normalTexture, 1);
            matProg.Sampler("Texture2", rmseTexture, 2);
            matProg.Sampler("Texture3", depthViewTexture, 3);
            matProg.Uniform("BoxWorldMatrix", Transform.RenderMatrix);
            matProg.Uniform("BoxHalfScale", HalfExtents);
        }

        public RenderCommandMesh3D RenderCommandDecal { get; }
            = new RenderCommandMesh3D(EDefaultRenderPass.DeferredDecals);
        public RenderCommandMethod3D DebugRenderCommand { get; }

        private void RenderDebug()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            Engine.Rendering.Debug.RenderBox(HalfExtents, Vector3.Zero, Transform.RenderMatrix, false, ColorF4.Red);
        }

        public RenderInfo3D RenderInfo { get; }
        public RenderInfo3D DebugRenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }
    }
}
