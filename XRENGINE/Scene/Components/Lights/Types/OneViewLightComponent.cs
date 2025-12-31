using XREngine.Components.Lights;
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

        protected readonly XRViewport _viewport = new(null, DefaultResolution, DefaultResolution)
        {
            RenderPipeline = new ShadowRenderPipeline(),
            SetRenderPipelineFromCamera = false,
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            CullWithFrustum = true,
        };

        public override void SetShadowMapResolution(uint width, uint height)
        {
            base.SetShadowMapResolution(width, height);
            _viewport.Resize(width, height);
        }

        protected abstract XRCameraParameters GetCameraParameters();

        public XRCamera? ShadowCamera => _viewport.Camera;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            _viewport.WorldInstanceOverride = World;
            XRCamera cam = new(GetShadowCameraParentTransform(), GetCameraParameters());
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
            _viewport.Camera = cam;

            if (Type == ELightType.Dynamic && CastsShadows && ShadowMap is null)
                SetShadowMapResolution(DefaultResolution, DefaultResolution);
        }

        protected virtual TransformBase GetShadowCameraParentTransform()
            => Transform;

        protected internal override void OnComponentDeactivated()
        {
            _viewport.WorldInstanceOverride = null;
            _viewport.Camera = null;

            base.OnComponentDeactivated();
        }

        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            _viewport.SwapBuffers();
            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }
        public override void CollectVisibleItems()
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            _viewport.CollectVisible(false);
        }

        private static bool _loggedShadowRenderOnce = false;

        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!CastsShadows || ShadowMap is null)
                return;

            if (!_loggedShadowRenderOnce)
            {
                _loggedShadowRenderOnce = true;
                Debug.Out($"[ShadowRender] RenderShadowMap called. ShadowMap FBO exists, rendering...");
            }

            if (collectVisibleNow)
            {
                CollectVisibleItems();
                SwapBuffers();
            }

            _viewport.Render(ShadowMap, null, null, true, ShadowMap.Material);
        }

        internal override void BuildShadowFrusta(List<PreparedFrustum> output)
        {
            output.Clear();

            if (ShadowCamera is null)
                return;

            output.Add(ShadowCamera.WorldFrustum().Prepare());
        }
    }
}
