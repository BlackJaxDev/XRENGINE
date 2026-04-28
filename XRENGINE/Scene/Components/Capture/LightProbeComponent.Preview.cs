using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
        #region Preview Methods

        private static XRMesh SharedPreviewSphereMesh
            => s_previewSphereMesh ??= XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 20u);

        public XRTexture? GetPreviewTexture()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance => IrradianceTexture,
                ERenderPreview.Prefilter => PrefilterTexture,
                _ => EnvironmentTextureOctahedral
                    ?? (XRTexture?)EnvironmentTextureCubemap
                    ?? PrefilterTexture
                    ?? IrradianceTexture
                    ?? _environmentTextureEquirect,
            };

        public string GetPreviewShaderPath()
            => GetPreviewShaderPath(GetPreviewTexture());

        private string GetPreviewShaderPath(XRTexture? previewTexture)
            => previewTexture switch
            {
                XRTextureCube => "Scene3D\\Cubemap.fs",
                _ when ReferenceEquals(previewTexture, _environmentTextureEquirect) => "Scene3D\\Equirect.fs",
                null => "Scene3D\\Equirect.fs",
                _ => "Scene3D\\OctahedralEnv.fs",
            };

        private bool OnPreCollectRenderInfo(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (camera != null && !camera.RendersLayer(DefaultLayers.GizmosIndex))
                return false;
            if (AutoShowPreviewOnSelect)
                PreviewEnabled = IsSceneNodeSelected();
            if (PreviewEnabled)
                CachePreviewSphere();
            _debugInfluenceCommand.Enabled = RenderInfluenceOnSelection && IsSceneNodeSelected();
            return true;
        }

        private void UpdatePreviewRenderMatrix(Matrix4x4 renderMatrix)
        {
            _previewRenderMatrix = renderMatrix;
            VisualRenderInfo.CullingOffsetMatrix = renderMatrix;

            if (World is not null)
                _visualRC.WorldMatrix = renderMatrix;
        }

        private bool TryGetAttachedPreviewRenderMatrix(out Matrix4x4 renderMatrix)
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull)
            {
                renderMatrix = Transform.RenderMatrix;
                return true;
            }

            renderMatrix = _previewRenderMatrix;
            return false;
        }

        private void SyncPreviewRenderCommandTransform()
        {
            if (World is not null)
                _visualRC.WorldMatrix = _previewRenderMatrix;
        }

        private void CachePreviewSphere()
        {
            bool shouldMaterialize = PreviewEnabled || (AutoShowPreviewOnSelect && IsSceneNodeSelected());
            if (!shouldMaterialize)
            {
                _previewSphereDirty = true;
                return;
            }

            XRTexture? previewTexture = GetPreviewTexture();
            string previewShaderPath = GetPreviewShaderPath(previewTexture);

            if (!_previewSphereDirty && PreviewSphere is not null &&
                ReferenceEquals(_previewSphereTexture, previewTexture) &&
                string.Equals(_previewSphereShaderPath, previewShaderPath, StringComparison.Ordinal))
            {
                return;
            }

            if (!TryGetAttachedPreviewRenderMatrix(out Matrix4x4 renderMatrix))
            {
                _previewSphereDirty = true;
                return;
            }

            int pass = (int)EDefaultRenderPass.OpaqueForward;
            XRShader previewShader = XRShader.EngineShader(previewShaderPath, EShaderType.Fragment);

            if (PreviewSphere is null)
            {
                XRMaterial material = new([previewTexture], previewShader) { RenderPass = pass };
                PreviewSphere = new XRMeshRenderer(SharedPreviewSphereMesh, material);
            }
            else
            {
                PreviewSphere.Mesh = SharedPreviewSphereMesh;

                XRMaterial material = PreviewSphere.Material ?? new XRMaterial();
                material.Textures = [previewTexture];
                material.Shaders = [previewShader];
                material.RenderPass = pass;
                PreviewSphere.Material = material;
            }

            _visualRC.Mesh = PreviewSphere;
            _visualRC.RenderPass = pass;

            VisualRenderInfo.LocalCullingVolume = SharedPreviewSphereMesh.Bounds;
            UpdatePreviewRenderMatrix(renderMatrix);
            _previewSphereTexture = previewTexture;
            _previewSphereShaderPath = previewShaderPath;
            _previewSphereDirty = false;
        }

        private bool IsSceneNodeSelected()
        {
            SceneNode? sceneNode = SceneNode;
            return sceneNode is not null && (EditorSelectionAccessor.Instance.Value?.IsNodeSelected(sceneNode) ?? false);
        }

        #endregion
    }
}
