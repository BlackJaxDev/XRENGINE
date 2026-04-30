using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;
using XREngine.Data.Geometry;

namespace XREngine.Components.Capture.Lights.Types
{
    /// <summary>
    /// Base class to handle shadow mapping for a light that only has one view.
    /// </summary>
    public abstract class OneViewLightComponent : LightComponent
    {
        private const uint DefaultResolution = 4096u;

        // Shadow resources are created lazily so light components can be imported
        // or deserialized in headless contexts without pulling in render pipelines.
        private XRViewport? _primaryShadowViewport;

        protected XRViewport PrimaryShadowViewport => _primaryShadowViewport ??= CreateShadowViewport();

        protected XRViewport? PrimaryShadowViewportOrNull => _primaryShadowViewport;

        protected OneViewLightComponent()
        {
        }

        protected OneViewLightComponent(EShadowMapStorageFormat shadowMapStorageFormat)
            : base(shadowMapStorageFormat)
        {
        }

        private XRViewport CreateShadowViewport()
        {
            uint width = ShadowMapResolutionWidth > 0 ? ShadowMapResolutionWidth : DefaultResolution;
            uint height = ShadowMapResolutionHeight > 0 ? ShadowMapResolutionHeight : DefaultResolution;
            return new XRViewport(null, width, height)
            {
                RenderPipeline = new ShadowRenderPipeline(),
                SetRenderPipelineFromCamera = false,
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
                CullWithFrustum = true,
            };
        }

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);
            _primaryShadowViewport?.Resize(width, height);
        }

        protected abstract XRCameraParameters GetCameraParameters();

        public XRCamera? ShadowCamera => _primaryShadowViewport?.Camera;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();

            XRViewport viewport = PrimaryShadowViewport;
            viewport.WorldInstanceOverride = WorldAs<XREngine.Rendering.IRuntimeRenderWorld>();
            XRCamera cam = new(GetShadowCameraParentTransform(), GetCameraParameters());
            cam.CullingMask = DefaultLayers.EverythingExceptGizmos;
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
            viewport.Camera = cam;

            if (Type == ELightType.Dynamic && CastsShadows && ShadowMap is null)
                SetShadowMapResolution(DefaultResolution, DefaultResolution);
        }

        protected virtual TransformBase GetShadowCameraParentTransform()
            => Transform;

        protected override void OnComponentDeactivated()
        {
            if (_primaryShadowViewport is not null)
            {
                _primaryShadowViewport.WorldInstanceOverride = null;
                _primaryShadowViewport.Camera = null;
            }

            base.OnComponentDeactivated();
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            PrimaryShadowViewport.SwapBuffers();
            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }
        public override void CollectVisibleItems()
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            PrimaryShadowViewport.CollectVisible(false);
        }

        private static bool _loggedShadowRenderOnce = false;

        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            if (!_loggedShadowRenderOnce)
            {
                _loggedShadowRenderOnce = true;
                Debug.Rendering($"[ShadowRender] RenderShadowMap called. ShadowMap FBO exists, rendering...");
            }

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            if (PrimaryShadowViewport.RenderPipeline is ShadowRenderPipeline shadowPipeline)
                shadowPipeline.ClearColor = GetShadowMapClearColor();

            PrimaryShadowViewport.Render(ShadowMap, null, null, true, ShadowMap.Material);
        }

        protected virtual ColorF4 GetShadowMapClearColor()
            => ColorF4.White;

        internal override void BuildShadowFrusta(List<PreparedFrustum> output)
        {
            output.Clear();

            if (ShadowCamera is null)
                return;

            output.Add(ShadowCamera.WorldFrustum().Prepare());
        }
    }
}
