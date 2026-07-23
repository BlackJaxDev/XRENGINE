using XREngine.Components.Lights;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture.Lights.Types
{
    /// <summary>
    /// Base class for lights that render shadows from one camera, such as spot and primary directional shadows.
    /// </summary>
    public abstract class OneViewLightComponent : LightComponent
    {
        private const uint DefaultResolution = 4096u;

        // Shadow resources are created lazily so light components can be imported
        // or deserialized in headless contexts without pulling in render pipelines.
        private XRViewport? _primaryShadowViewport;

        /// <summary>
        /// Lazily-created viewport that owns the shadow camera and shadow render pipeline.
        /// </summary>
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

        /// <summary>
        /// Camera used by the primary one-view shadow pass, when the component is active.
        /// Runtime-only shadow state is restored lazily because editor snapshots do not
        /// serialize viewport or camera instances.
        /// </summary>
        public XRCamera? ShadowCamera
        {
            get
            {
                EnsurePrimaryShadowViewportReady();
                return _primaryShadowViewport?.Camera;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            EnsurePrimaryShadowViewportReady();

            if (Type == ELightType.Dynamic && CastsShadows && ShadowMap is null)
                SetShadowMapResolution(DefaultResolution, DefaultResolution);
        }

        private void EnsurePrimaryShadowViewportReady()
        {
            if (!IsActiveInHierarchy)
                return;

            XRViewport viewport = _primaryShadowViewport ??= CreateShadowViewport();
            viewport.WorldInstanceOverride = WorldAs<XREngine.Rendering.IRuntimeRenderWorld>();
            if (viewport.Camera is not null)
                return;

            XRCamera cam = new(GetShadowCameraParentTransform(), GetCameraParameters())
            {
                CullingMask = DefaultLayers.EverythingExceptGizmos
            };
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
        }

        /// <summary>
        /// Resolves a live primary shadow viewport whose runtime camera has been
        /// reconstructed. Snapshot restoration does not serialize either object, so
        /// derived light implementations must use this guard instead of directly
        /// submitting the lazily-created viewport.
        /// </summary>
        protected bool TryGetPrimaryShadowViewportForProcessing(out XRViewport viewport)
        {
            viewport = null!;
            if (!IsActiveInHierarchy)
                return false;

            EnsurePrimaryShadowViewportReady();
            XRViewport? candidate = _primaryShadowViewport;
            if (candidate?.ActiveCamera is null)
                return false;

            viewport = candidate;
            return true;
        }

        protected virtual TransformBase GetShadowCameraParentTransform()
            => Transform;

        protected override void OnComponentDeactivated()
        {
            // Publish the detached state before touching the viewport so queued shadow work
            // cannot discover and reuse it while snapshot restoration is deactivating this light.
            XRViewport? viewport = _primaryShadowViewport;
            _primaryShadowViewport = null;
            viewport?.Destroy();

            base.OnComponentDeactivated();
        }

        protected virtual bool UsesAtlasShadowViewport => false;

        protected virtual bool PrimaryShadowViewportRelevant => true;

        private bool TryGetProcessableShadowViewport(out XRViewport viewport)
        {
            viewport = null!;
            if (!IsActiveInHierarchy ||
                !CastsShadows ||
                !PrimaryShadowViewportRelevant ||
                (ShadowMap is null && !UsesAtlasShadowViewport))
            {
                return false;
            }

            return TryGetPrimaryShadowViewportForProcessing(out viewport);
        }

        /// <summary>
        /// Swaps the one-view shadow viewport buffers after visible shadow casters have been collected.
        /// </summary>
        public override void SwapBuffers(Rendering.Lightmapping.LightmapBakeManager? lightmapBaker = null)
        {
            if (!TryGetProcessableShadowViewport(out XRViewport viewport))
                return;

            viewport.SwapBuffers();
            lightmapBaker?.ProcessDynamicCachedAutoBake(this);
        }

        /// <summary>
        /// Collects visible shadow casters for the one-view shadow camera.
        /// </summary>
        public override void CollectVisibleItems()
        {
            if (!TryGetProcessableShadowViewport(out XRViewport viewport))
                return;

            viewport.CollectVisible(false);
        }

        private static bool _loggedShadowRenderOnce = false;

        /// <summary>
        /// Renders the primary shadow camera into this light's standalone shadow map.
        /// </summary>
        public override void RenderShadowMap(bool collectVisibleNow = false)
        {
            if (!IsActiveInHierarchy ||
                !CastsShadows ||
                ShadowMap is null ||
                !PrimaryShadowViewportRelevant)
                return;

            if (!TryGetPrimaryShadowViewportForProcessing(out XRViewport viewport))
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

            if (viewport.RenderPipeline is ShadowRenderPipeline shadowPipeline)
                shadowPipeline.ClearColor = GetShadowMapClearColor();

            viewport.Render(ShadowMap, null, null, true, ShadowMap.Material);
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
