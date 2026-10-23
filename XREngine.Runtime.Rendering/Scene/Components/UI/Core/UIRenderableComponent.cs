using System.Numerics;
using XREngine.Rendering.UI.State;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Base helper class for all UI components that can be rendered.
    /// Automatically handles rendering and culling of UI components.
    /// </summary>
    [RequiresTransform(typeof(UIBoundableTransform))]
    public abstract class UIRenderableComponent : UIComponent, IRenderable
    {
        public UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;
        public UIRenderableComponent()
        {
            RenderInfo3D = RenderInfo3D.New(this, RenderCommand3D);
            RenderInfo2D = RenderInfo2D.New(this, RenderCommand2D);
            RenderedObjects = [RenderInfo3D, RenderInfo2D];
            // The callback does not pass the camera or the pass collection to ShouldRender3D. When it rejects a main view,
            // it rejects the shadow cameras too, so the declaration keeps a hidden or screen-space item out of the
            // shadow-only caster census.
            RenderInfo3D.SetCameraIndependentPreCollectCommandsCallback(PreCollectRenderInfo3D);
            RenderInfo2D.PreCollectCommandsCallback = ShouldRender2D;
            _publicationCommandObserver = new UIDrawCommandObserver(RenderCommand2D, PublicationDrawChanged);
        }

        /// <summary>Call after direct writes to draw arrays, index lists, or other storage without change events.</summary>
        public void InvalidatePublicationDraw()
        {
            _publicationPayloadBuilder?.InvalidateRawStorage();
            PublicationDrawChanged();
        }

        private void PublicationDrawChanged()
        {
            if (UITransform is { } transform)
                transform.RecordUIChange(UIChangeDomain.Visual);
        }

        private readonly UIDrawCommandObserver _publicationCommandObserver;
        private UIDrawPayloadBuilder? _publicationPayloadBuilder;
        private RenderCommandMesh2D? _publicationFrozenCommand;

        protected override void OnComponentDeactivated()
        {
            _publicationPayloadBuilder?.Dispose();
            _publicationFrozenCommand = null;
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            _publicationCommandObserver.Dispose();
            try
            {
                base.OnDestroying();
            }
            finally
            {
                _publicationPayloadBuilder?.Dispose();
                try { RenderInfo3D.Dispose(); }
                finally { RenderInfo2D.Dispose(); }
            }
        }

        //TODO: register callback on canvas to set RenderInfo3D/2D Visible property so no quadtree/octree culling is done if the canvas is not visible

        /// <summary>
        /// The pre-collection callback of <see cref="RenderInfo3D"/>. The render info declares it camera independent,
        /// because it ignores its camera and pass collection arguments.
        /// </summary>
        private bool PreCollectRenderInfo3D(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
            => ShouldRender3D();

        private static int _shouldRender3DDiagCount = 0;
        /// <summary>
        /// Returns whether the 3D render info collects its commands: only for an active, visible item on a world-space or
        /// camera-space canvas that renders directly, without an offscreen target.
        /// </summary>
        /// <remarks>
        /// The result applies to every camera and every pass collection, because the 3D render info declares its
        /// pre-collection callback camera independent. An override must read only component, transform and canvas
        /// state.
        /// </remarks>
        protected virtual bool ShouldRender3D()
        {
            var tfm = BoundableTransform;
            bool diagLog = _shouldRender3DDiagCount < 20;
            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false))
            {
                if (diagLog)
                {
                    //Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': parentCanvasActive={tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy} visibleInHierarchy={tfm.IsVisibleInHierarchy} parentCanvas={tfm.ParentCanvas?.SceneNode?.Name}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            var canvas = tfm.ParentCanvas;
            if (canvas is null || canvas.DrawSpace == ECanvasDrawSpace.Screen)
            {
                if (diagLog)
                {
                    //Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': canvasNull={canvas is null} drawSpace={canvas?.DrawSpace}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            var canvasComponent = canvas.SceneNode?.GetComponent<UICanvasComponent>();
            if (canvasComponent?.UseOffscreenRenderingForNonScreenSpaces() ?? true)
            {
                if (diagLog)
                {
                    //Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': effectiveOffscreen={canvasComponent?.UseOffscreenRenderingForNonScreenSpaces()} preferOffscreen={canvasComponent?.PreferOffscreenRenderingForNonScreenSpaces} canvasComp={canvasComponent is not null}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            if (diagLog)
            {
                //Debug.UI($"[ShouldRender3D] ACCEPTED {GetType().Name} on '{SceneNode?.Name}': drawSpace={canvas.DrawSpace} mesh={Mesh is not null} renderPass={RenderCommand3D.RenderPass}");
                _shouldRender3DDiagCount++;
            }

            return true;
        }

        private static int _shouldRender2DDiagCount = 0;
        protected virtual bool ShouldRender2D(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            var tfm = BoundableTransform;
            bool diagLog = _shouldRender2DDiagCount < 20;

            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false))
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender2D] REJECTED {GetType().Name} on '{SceneNode?.Name}': parentCanvasActive={tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy} parentCanvas={tfm.ParentCanvas?.SceneNode?.Name}");
                    _shouldRender2DDiagCount++;
                }
                return false;
            }

            var canvas = tfm.ParentCanvas;
            if (canvas is null)
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender2D] REJECTED {GetType().Name} on '{SceneNode?.Name}': canvas is null");
                    _shouldRender2DDiagCount++;
                }
                return false;
            }

            var canvasComp = canvas.SceneNode?.GetComponent<UICanvasComponent>();

            if (canvasComp is null ||
                !canvasComp.TryGetCollectingRenderRecord(RenderInfo2D.MembershipId, out UIRenderRecord published) ||
                !published.Visible || published.Identity != tfm.ElementIdentity)
                return false;

            RenderCommand2D.WorldCropRegion = published.Crop;
            RenderCommand2D.WorldMatrix = published.WorldMatrix;

            // Determine if this item should use the 2D render path:
            // - Screen-space: always yes
            // - Non-screen with offscreen FBO: yes (rendering to canvas's internal FBO)
            // - Non-screen without offscreen: no (handled by ShouldRender3D as direct 3D objects)
            if (canvas.DrawSpace != ECanvasDrawSpace.Screen)
            {
                if (!(canvasComp?.UseOffscreenRenderingForNonScreenSpaces() ?? false))
                {
                    if (diagLog)
                    {
                        Debug.UI($"[ShouldRender2D] REJECTED {GetType().Name} on '{SceneNode?.Name}': drawSpace={canvas.DrawSpace} effectiveOffscreen={canvasComp?.UseOffscreenRenderingForNonScreenSpaces()} preferOffscreen={canvasComp?.PreferOffscreenRenderingForNonScreenSpaces} canvasComp={canvasComp is not null}");
                        _shouldRender2DDiagCount++;
                    }
                    return false;
                }
            }

            // Attempt batched rendering path: register with the canvas's batch collector
            // and skip the individual render command for this component.
            if (published.SupportsBatch)
            {
                if (canvasComp?.BatchCollector is { Enabled: true } collector)
                {
                    if (RegisterWithBatchCollector(collector, passes))
                        return false; // Successfully batched — skip individual command
                    // Registration failed (e.g. font not loaded yet); fall through to individual render
                }
            }

            canvasComp?.BatchCollector?.BreakBatchRun(published.RenderPass);

            if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked ||
                AbstractRenderer.Current?.BackendId == RendererBackendId.WebGPU)
                throw new NotSupportedException($"WebGPU.UI.UnbatchedUnsupported: '{GetType().Name}' requires an admitted screen-space batch profile; individual UI draws are unsupported.");

            if (diagLog)
            {
                Debug.UI($"[ShouldRender2D] ACCEPTED {GetType().Name} on '{SceneNode?.Name}': drawSpace={canvas.DrawSpace} mesh={Mesh is not null} renderPass={RenderCommand2D.RenderPass} instances={RenderCommand2D.Instances}");
                _shouldRender2DDiagCount++;
            }

            return true;
        }

        /// <summary>
        /// When true, this component participates in batched instanced rendering.
        /// Components with clip-to-bounds or special materials should return false.
        /// </summary>
        public virtual bool SupportsBatchedRendering => false;

        /// <summary>
        /// Called during collect-visible when batching is enabled.
        /// Override to register per-instance data with the batch collector.
        /// </summary>
        /// <param name="collector">The canvas's batch collector to register with.</param>
        /// <param name="passes">The UI pass collection so batching can insert ordered dispatch markers.</param>
        /// <returns>True if the component was successfully registered for batching; false to fall back to individual rendering.</returns>
        protected virtual bool RegisterWithBatchCollector(UIBatchCollector collector, RenderCommandCollection passes) => false;

        /// <summary>Captures draw values on the canvas owner for one publication.</summary>
        public virtual UIRenderRecord CapturePublicationRenderRecord(in UILayoutNodeOutput output)
        {
            UIBoundableTransform bounds = BoundableTransform;
            Vector2 bottomLeft = bounds.ActualLocalBottomLeftTranslation;
            Matrix4x4 worldMatrix = bounds.WorldMatrix *
                (bounds.ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
            worldMatrix = CapturePublicationModelMatrix(in output, in worldMatrix);
            bool supportsBatch = SupportsBatchedRendering &&
                bounds.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>()?.BatchCollector.Enabled == true;
            bool visible = output.Visible && IsActiveInHierarchy && RenderInfo2D.IsVisible &&
                (supportsBatch || RenderCommand2D.Enabled && RenderCommand2D.Instances > 0);
            if (supportsBatch || !visible)
            {
                _publicationPayloadBuilder?.Dispose();
                _publicationFrozenCommand = null;
            }
            RenderCommandMesh2D? frozenCommand = null;
            UIDrawPayload? payload = null;
            string? captureFailure = null;
            Vector4 frozenBounds = new(bottomLeft.X, bottomLeft.Y, output.ActualSize.X, output.ActualSize.Y);
            if (!supportsBatch && visible)
            {
                if (_publicationPayloadBuilder is null)
                {
                    _publicationPayloadBuilder = new UIDrawPayloadBuilder(OnMaterialSettingUniforms);
                    _publicationPayloadBuilder.SourceChanged += PublicationDrawChanged;
                }
                _publicationPayloadBuilder.Bounds = frozenBounds;
                if (TryCapturePublicationPayload(in output, _publicationPayloadBuilder, out payload, out captureFailure) && payload is not null)
                {
                    RenderCommandMesh2D source = RenderCommand2D;
                    var crop = UIClipRegion.ResolveCrop(bounds, ClipToBounds);
                    var cropSpace = bounds.ParentCanvas?.GetActualBounds();
                    RenderCommandMesh2D? cached = _publicationFrozenCommand;
                    if (cached is not null && ReferenceEquals(cached.Mesh, payload.Renderer) &&
                        cached.WorldMatrix == worldMatrix && cached.ZIndex == source.ZIndex && cached.RenderPass == source.RenderPass &&
                        cached.Instances == source.Instances && cached.WorldCropRegion == crop && cached.UICanvasCropSpace == cropSpace &&
                        cached.ForceCpuRendering == source.ForceCpuRendering && cached.WorldMatrixIsModelMatrix == source.WorldMatrixIsModelMatrix &&
                        cached.GpuProfilingLabel == source.GpuProfilingLabel && cached.Enabled == source.Enabled)
                        frozenCommand = cached;
                    else
                        frozenCommand = _publicationFrozenCommand = new RenderCommandMesh2D(source.RenderPass)
                        {
                            Mesh = payload.Renderer,
                            WorldMatrix = worldMatrix,
                            ZIndex = source.ZIndex,
                            MaterialOverride = payload.Material,
                            RenderOptionsOverride = payload.RenderOptions,
                            Instances = source.Instances,
                            WorldCropRegion = crop,
                            UICanvasCropSpace = cropSpace,
                            ForceCpuRendering = source.ForceCpuRendering,
                            WorldMatrixIsModelMatrix = source.WorldMatrixIsModelMatrix,
                            GpuProfilingLabel = source.GpuProfilingLabel,
                            Enabled = source.Enabled,
                        };
                }
            }
            return new UIRenderRecord
            {
                RenderInfo = RenderInfo2D,
                MembershipId = RenderInfo2D.MembershipId,
                Identity = bounds.ElementIdentity,
                WorldMatrix = worldMatrix,
                Bounds = new Vector4(bottomLeft.X, bottomLeft.Y, output.ActualSize.X, output.ActualSize.Y),
                Crop = UIClipRegion.ResolveCrop(bounds, ClipToBounds),
                RenderPass = RenderPass,
                ZIndex = RenderCommand2D.ZIndex,
                Material = payload?.Material ?? Material,
                Visible = visible,
                SupportsBatch = supportsBatch,
                FrozenCommand = frozenCommand,
                Payload = payload,
                CaptureFailure = captureFailure,
            };
        }

        /// <summary>Declares that the component captures all values emitted by its material callback.</summary>
        protected virtual bool CanCapturePublicationUniforms => false;

        /// <summary>Captures callback values and owned resources before a publication can become visible.</summary>
        protected virtual bool TryCapturePublicationPayload(in UILayoutNodeOutput output,
            UIDrawPayloadBuilder builder, out UIDrawPayload? payload, out string? failure)
        {
            // Overrides with render side effects must supply their own producer data adapter.
            if (!CanCapturePublicationUniforms)
            {
                payload = null;
                failure = "UI.DrawCapture.MaterialAdapterRequired: capture the component uniform callback in TryCapturePublicationPayload.";
                return false;
            }
            return builder.TryCapture(RenderCommand2D, false, out payload, out failure);
        }

        /// <summary>Applies component mesh sizing to the owner-captured canvas matrix.</summary>
        protected virtual Matrix4x4 CapturePublicationModelMatrix(
            in UILayoutNodeOutput output, in Matrix4x4 canvasMatrix)
            => canvasMatrix;

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

            if (transform is not UIBoundableTransform tfm)
                return;

            RefreshRenderCommandFromTransform(tfm);
        }

        private void RefreshRenderCommandFromTransform(UIBoundableTransform tfm)
        {
            tfm.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
            var mtx = GetRenderWorldMatrix(tfm);
            RenderCommand3D.WorldMatrix = mtx;
            RenderCommand2D.WorldMatrix = GetRenderCanvasMatrix(tfm);
        }

        protected virtual Matrix4x4 GetRenderWorldMatrix(UIBoundableTransform tfm)
            => tfm.RenderMatrix;

        /// <summary>
        /// Maps the same authored mesh into the local 2D target before the canvas is placed in the scene.
        /// </summary>
        protected Matrix4x4 GetRenderCanvasMatrix(UIBoundableTransform tfm)
            => GetRenderWorldMatrix(tfm) * (tfm.ParentCanvas?.InverseRenderMatrix ?? Matrix4x4.Identity);

        /// <summary>
        /// The material used to render this UI component.
        /// </summary>
        private XRMaterial? _material;
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        [YamlIgnore]
        public int RenderPass
        {
            get => RenderCommand3D.RenderPass;
            set
            {
                if (RenderCommand3D.RenderPass == value && RenderCommand2D.RenderPass == value)
                    return;
                RecordRenderTopologyChange();
                RenderCommand3D.RenderPass = value;
                RenderCommand2D.RenderPass = value;
            }
        }
        public RenderInfo3D RenderInfo3D { get; }
        public RenderInfo2D RenderInfo2D { get; }
        public RenderCommandMesh3D RenderCommand3D { get; } = new RenderCommandMesh3D(EDefaultRenderPass.OpaqueForward);
        public RenderCommandMesh2D RenderCommand2D { get; } = new RenderCommandMesh2D((int)EDefaultRenderPass.OpaqueForward);
        public RenderInfo[] RenderedObjects { get; }
        [YamlIgnore]
        public XRMeshRenderer? Mesh
        {
            get => RenderCommand3D.Mesh;
            set
            {
                if (ReferenceEquals(RenderCommand3D.Mesh, value) && ReferenceEquals(RenderCommand2D.Mesh, value))
                    return;
                RecordRenderTopologyChange();
                RenderCommand3D.Mesh = value;
                RenderCommand2D.Mesh = value;

                if (_material != value?.Material)
                    Material = value?.Material;
            }
        }

        private void RecordRenderTopologyChange()
        {
            if (SceneNode is not null && !SceneNode.IsTransformNull && Transform is UITransform transform)
                transform.RecordUIChange(UIChangeDomain.Topology | UIChangeDomain.Visual | UIChangeDomain.HitTest);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Material):
                        _material?.SettingUniforms -= OnMaterialSettingUniforms;
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Material):
                    var m = Mesh;
                    m?.Material = _material;
                    _material?.SettingUniforms += OnMaterialSettingUniforms;
                    break;
            }
        }

        private Vector4 _lastBounds = Vector4.Zero;
        protected virtual void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            UIRenderRecord record = default;
            bool hasPublication = UICanvasComponent.RenderingCanvasForCurrentThread?.BatchCollector
                .TryGetRenderingRecord(RenderInfo2D.MembershipId, out record) == true;
            Vector4 bounds;
            if (hasPublication)
                bounds = record.Bounds;
            else
            {
                UIBoundableTransform tfm = BoundableTransform;
                Vector2 bottomLeft = tfm.ActualLocalBottomLeftTranslation;
                bounds = new Vector4(bottomLeft.X, bottomLeft.Y, tfm.ActualWidth, tfm.ActualHeight);
            }
            float x = bounds.X;
            float y = bounds.Y;
            float w = bounds.Z;
            float h = bounds.W;

            if (!hasPublication && x == _lastBounds.X &&
                y == _lastBounds.Y &&
                w == _lastBounds.Z &&
                h == _lastBounds.W)
                return; //No change, no need to update uniforms

            _lastBounds = bounds;
            program.Uniform(EEngineUniform.UIWidth.ToStringFast(), w);
            program.Uniform(EEngineUniform.UIHeight.ToStringFast(), h);
            program.Uniform(EEngineUniform.UIX.ToStringFast(), x);
            program.Uniform(EEngineUniform.UIY.ToStringFast(), y);
            program.Uniform(EEngineUniform.UIXYWH.ToStringFast(), bounds);
        }

        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(UIBoundableTransform.ActualSize):
                    if (sender is UIBoundableTransform tfm)
                    {
                        tfm.RefreshAxisAlignedRegion();
                        RefreshRenderCommandFromTransform(tfm);
                    }
                    break;
                case nameof(ClipToBounds):
                    RenderCommand2D.WorldCropRegion = UIClipRegion.ResolveCrop(BoundableTransform, ClipToBounds);
                    break;
                case nameof(UIBoundableTransform.AxisAlignedRegion):
                    RenderCommand2D.WorldCropRegion = UIClipRegion.ResolveCrop(BoundableTransform, ClipToBounds);
                    break;
            }
        }

        private bool _clipToBounds = false;
        /// <summary>
        /// If true, this UI component and descendants will be scissor-tested to its bounds.
        /// The effective rectangular crop intersects all clipping ancestors.
        /// </summary>
        public bool ClipToBounds
        {
            get => _clipToBounds;
            set => SetField(ref _clipToBounds, value);
        }
    }
}
