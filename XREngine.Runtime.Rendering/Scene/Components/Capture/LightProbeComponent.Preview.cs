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

        private const uint PreviewSpherePrecision = 48u;
        private bool _previewSphereFallbackLogged;

        private static XRMesh SharedPreviewSphereMesh
            => s_previewSphereMesh ??= XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, PreviewSpherePrecision);

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

        private void BindPreviewTextureUniform(XRMaterialBase _, XRRenderProgram program)
        {
            XRTexture? previewTexture = _previewSphereTexture;
            if (previewTexture is null)
            {
                previewTexture = XRTexture2D.GetRoleAwareFallbackTexture("Texture0");
                if (!_previewSphereFallbackLogged)
                {
                    _previewSphereFallbackLogged = true;
                    TextureRuntimeDiagnostics.LogFallbackTextureBound(
                        RuntimeRenderingHostServices.Current.LastRenderTimestampTicks,
                        nameof(LightProbeComponent),
                        "Texture0",
                        "light probe preview has no materialized environment texture");
                }
            }

            program.Sampler("Texture0", previewTexture, 0);
        }

        private void ConfigurePreviewMaterial(XRMaterial material, XRTexture? previewTexture, XRShader previewShader, int pass)
        {
            material.SettingUniforms -= BindPreviewTextureUniform;
            material.SettingUniforms += BindPreviewTextureUniform;
            material.Textures = [previewTexture ?? XRTexture2D.GetRoleAwareFallbackTexture("Texture0")];
            material.Shaders = [previewShader];
            material.RenderPass = pass;
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
            _previewSphereTexture = previewTexture;
            if (previewTexture is not null)
                _previewSphereFallbackLogged = false;

            if (PreviewSphere is null)
            {
                XRMaterial material = new([previewTexture], previewShader);
                ConfigurePreviewMaterial(material, previewTexture, previewShader, pass);
                PreviewSphere = new XRMeshRenderer(SharedPreviewSphereMesh, material);
            }
            else
            {
                PreviewSphere.Mesh = SharedPreviewSphereMesh;

                XRMaterial material = PreviewSphere.Material ?? new XRMaterial();
                ConfigurePreviewMaterial(material, previewTexture, previewShader, pass);
                PreviewSphere.Material = material;
            }

            _visualRC.Mesh = PreviewSphere;
            _visualRC.RenderPass = pass;

            VisualRenderInfo.LocalCullingVolume = SharedPreviewSphereMesh.Bounds;
            UpdatePreviewRenderMatrix(renderMatrix);
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
