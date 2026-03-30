using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
        #region Preview Methods

        public XRTexture? GetPreviewTexture()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance => IrradianceTexture,
                ERenderPreview.Prefilter => PrefilterTexture,
                _ => EnvironmentTextureOctahedral
                    ?? (XRTexture?)EnvironmentTextureCubemap
                    ?? _environmentTextureEquirect,
            };

        public string GetPreviewShaderPath()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance or ERenderPreview.Prefilter => "Scene3D\\OctahedralEnv.fs",
                _ => EnvironmentTextureOctahedral is not null
                    ? "Scene3D\\OctahedralEnv.fs"
                    : EnvironmentTextureCubemap is not null
                        ? "Scene3D\\Cubemap.fs"
                    : "Scene3D\\Equirect.fs",
            };

        private bool OnPreCollectRenderInfo(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (camera != null && !camera.CullingMask.Contains(DefaultLayers.GizmosIndex))
                return false;
            if (AutoShowPreviewOnSelect)
                PreviewEnabled = IsSceneNodeSelected();
            _debugInfluenceCommand.Enabled = RenderInfluenceOnSelection && IsSceneNodeSelected();
            return true;
        }

        private void CachePreviewSphere()
        {
            PreviewSphere?.Destroy();

            int pass = (int)EDefaultRenderPass.OpaqueForward;
            var mesh = XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 20u);
            var mat = new XRMaterial([GetPreviewTexture()], XRShader.EngineShader(GetPreviewShaderPath(), EShaderType.Fragment)) { RenderPass = pass };
            PreviewSphere = new XRMeshRenderer(mesh, mat);

            _visualRC.Mesh = PreviewSphere;
            _visualRC.WorldMatrix = Transform.RenderMatrix;
            _visualRC.RenderPass = pass;

            VisualRenderInfo.LocalCullingVolume = PreviewSphere?.Mesh?.Bounds;
            VisualRenderInfo.CullingOffsetMatrix = Transform.RenderMatrix;
        }

        private bool IsSceneNodeSelected()
            => EditorSelectionAccessor.Instance.Value?.IsNodeSelected(SceneNode) ?? false;

        #endregion
    }
}
