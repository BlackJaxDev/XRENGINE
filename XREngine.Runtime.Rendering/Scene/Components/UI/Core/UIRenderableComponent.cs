using System.Numerics;
using System.Runtime.ExceptionServices;
using XREngine.Rendering.UI.State;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene;
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
            RenderInfo3D.SetCollectionSuppressed(true);
            _worldCollectionSuppressed = true;
            RenderInfo2D = RenderInfo2D.New(this, RenderCommand2D);
            RenderedObjects = [RenderInfo3D, RenderInfo2D];
            // The callback does not pass the camera or the pass collection to ShouldRender3D. When it rejects a main view,
            // it rejects the shadow cameras too, so the declaration keeps a hidden or screen-space item out of the
            // shadow-only caster census.
            _canonicalWorldPreCollect = PreCollectRenderInfo3D;
            RenderInfo3D.SetCameraIndependentPreCollectCommandsCallback(_canonicalWorldPreCollect);
            RenderInfo2D.PreCollectCommandsCallback = ShouldRender2D;
            RenderInfo3D.PropertyChanged += WorldCollectionDrawPropertyChanged;
            RenderCommand3D.PropertyChanged += WorldCollectionDrawPropertyChanged;
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
        private bool _worldCollectionSuppressed;
        private bool _worldCollectionTerminal;
        private UICanvasTransform? _worldCollectionCanvasTransform;
        private UICanvasComponent? _worldCollectionCanvasComponent;
        private UICanvasComponent? _registeredDirectWorldCanvas;
        private RenderInfo.DelAddRenderCommandsCallback? _canonicalWorldPreCollect;
        private SceneNode? _worldCollectionCanvasNode;
        private readonly List<UITransform> _worldCollectionAncestors = [];

        private void SyncCanvasWorldCollection()
        {
            if (_worldCollectionTerminal || !IsActiveInHierarchy)
            {
                ReleaseCanvasWorldCollection();
                return;
            }
            UICanvasTransform? canvasTransform = SceneNode is not null && !SceneNode.IsTransformNull
                ? (Transform as UITransform)?.ParentCanvas
                : null;
            SceneNode? canvasNode = canvasTransform?.SceneNode;
            UICanvasComponent? canvasComponent = canvasNode?.GetComponent<UICanvasComponent>();

            if (!ReferenceEquals(_worldCollectionCanvasTransform, canvasTransform))
            {
                if (_worldCollectionCanvasTransform is not null)
                    _worldCollectionCanvasTransform.PropertyChanged -= CanvasWorldTransformPropertyChanged;
                _worldCollectionCanvasTransform = canvasTransform;
                if (canvasTransform is not null)
                    canvasTransform.PropertyChanged += CanvasWorldTransformPropertyChanged;
            }
            if (!ReferenceEquals(_worldCollectionCanvasNode, canvasNode))
            {
                if (_worldCollectionCanvasNode is not null)
                {
                    _worldCollectionCanvasNode.ComponentAdded -= CanvasWorldComponentChanged;
                    _worldCollectionCanvasNode.ComponentRemoved -= CanvasWorldComponentChanged;
                    _worldCollectionCanvasNode.Activated -= CanvasWorldNodeActivationChanged;
                    _worldCollectionCanvasNode.Deactivated -= CanvasWorldNodeActivationChanged;
                }
                _worldCollectionCanvasNode = canvasNode;
                if (canvasNode is not null)
                {
                    canvasNode.ComponentAdded += CanvasWorldComponentChanged;
                    canvasNode.ComponentRemoved += CanvasWorldComponentChanged;
                    canvasNode.Activated += CanvasWorldNodeActivationChanged;
                    canvasNode.Deactivated += CanvasWorldNodeActivationChanged;
                }
            }
            if (!ReferenceEquals(_worldCollectionCanvasComponent, canvasComponent))
            {
                if (_worldCollectionCanvasComponent is not null)
                    _worldCollectionCanvasComponent.WorldCollectionModeChanged -= SyncCanvasWorldCollection;
                _worldCollectionCanvasComponent = canvasComponent;
                if (canvasComponent is not null)
                    canvasComponent.WorldCollectionModeChanged += SyncCanvasWorldCollection;
            }

            RenderInfo3D.UserInterfaceCanvas = canvasComponent;
            RenderInfo2D.UserInterfaceCanvas = canvasComponent;

            bool directWorld = canvasNode?.IsActiveInHierarchy == true &&
                canvasComponent is { IsActiveInHierarchy: true, WorldCollectionUnavailable: false } &&
                canvasTransform is { DrawSpace: not ECanvasDrawSpace.Screen } &&
                !canvasComponent.UseOffscreenRenderingForNonScreenSpaces() &&
                (Transform as UITransform)?.IsVisibleInHierarchy == true;
            SetCanvasWorldCollectionSuppressed(!directWorld);
            ReconcileDirectWorldCanvas(directWorld ? canvasComponent : null);
        }

        private void ReconcileDirectWorldCanvas(UICanvasComponent? canvas)
        {
            if (ReferenceEquals(_registeredDirectWorldCanvas, canvas))
                return;
            UICanvasComponent? old = _registeredDirectWorldCanvas;
            _registeredDirectWorldCanvas = null;
            old?.UnregisterDirectWorldChild(RenderCommand3D);
            if (canvas is not null)
            {
                canvas.RegisterDirectWorldChild(RenderCommand3D);
                _registeredDirectWorldCanvas = canvas;
            }
        }

        private void WorldCollectionDrawPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if ((sender == RenderInfo3D && e.PropertyName is
                    nameof(RenderInfo3D.IsVisible) or nameof(RenderInfo3D.WorldInstance) or
                    nameof(RenderInfo3D.Layer) or nameof(RenderInfo3D.CastsShadows) or
                    nameof(RenderInfo3D.LocalCullingVolume) or nameof(RenderInfo3D.CullingOffsetMatrix)) ||
                (sender == RenderCommand3D && e.PropertyName is
                    nameof(RenderCommand3D.Enabled) or nameof(RenderCommand3D.RenderPass) or
                    nameof(RenderCommandMesh3D.Mesh) or nameof(RenderCommandMesh3D.MaterialOverride) or
                    nameof(RenderCommandMesh3D.RenderOptionsOverride) or
                    nameof(RenderCommandMesh3D.WorldMatrix) or nameof(RenderCommandMesh3D.Instances) or
                    nameof(RenderCommandMesh3D.WorldCullingVolumeOverride)))
                _registeredDirectWorldCanvas?.NotifyDirectWorldChildChanged();
        }

        internal bool HasCanonicalWorldPreCollect(RenderInfo3D info)
            => ReferenceEquals(info, RenderInfo3D) &&
                ReferenceEquals(info.PreCollectCommandsCallback, _canonicalWorldPreCollect);

        private void ReleaseCanvasWorldCollection()
        {
            ReconcileDirectWorldCanvas(null);
            if (_worldCollectionCanvasTransform is not null)
                _worldCollectionCanvasTransform.PropertyChanged -= CanvasWorldTransformPropertyChanged;
            _worldCollectionCanvasTransform = null;
            if (_worldCollectionCanvasNode is not null)
            {
                _worldCollectionCanvasNode.ComponentAdded -= CanvasWorldComponentChanged;
                _worldCollectionCanvasNode.ComponentRemoved -= CanvasWorldComponentChanged;
                _worldCollectionCanvasNode.Activated -= CanvasWorldNodeActivationChanged;
                _worldCollectionCanvasNode.Deactivated -= CanvasWorldNodeActivationChanged;
            }
            _worldCollectionCanvasNode = null;
            if (_worldCollectionCanvasComponent is not null)
                _worldCollectionCanvasComponent.WorldCollectionModeChanged -= SyncCanvasWorldCollection;
            _worldCollectionCanvasComponent = null;
            foreach (UITransform ancestor in _worldCollectionAncestors)
                ancestor.PropertyChanged -= WorldCollectionAncestorPropertyChanged;
            _worldCollectionAncestors.Clear();
            RenderInfo3D.UserInterfaceCanvas = null;
            RenderInfo2D.UserInterfaceCanvas = null;
            SetCanvasWorldCollectionSuppressed(true);
        }

        private void RebindWorldCollectionAncestors()
        {
            foreach (UITransform ancestor in _worldCollectionAncestors)
                ancestor.PropertyChanged -= WorldCollectionAncestorPropertyChanged;
            _worldCollectionAncestors.Clear();
            TransformBase? parent = (Transform as UITransform)?.Parent;
            while (parent is UITransform ancestor)
            {
                ancestor.PropertyChanged += WorldCollectionAncestorPropertyChanged;
                _worldCollectionAncestors.Add(ancestor);
                parent = ancestor.Parent;
            }
        }

        private void CanvasWorldTransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(UICanvasTransform.DrawSpace) or nameof(TransformBase.SceneNode))
                SyncCanvasWorldCollection();
            if (e.PropertyName == nameof(TransformBase.SceneNode))
                SyncBatchPrimitiveRegistration();
        }

        private void CanvasWorldComponentChanged((SceneNode node, XRComponent comp) change)
        {
            if (change.comp is UICanvasComponent)
            {
                SyncCanvasWorldCollection();
                SyncBatchPrimitiveRegistration();
            }
        }

        private void CanvasWorldNodeActivationChanged(SceneNode _)
            => SyncCanvasWorldCollection();

        private void WorldCollectionAncestorPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (_worldCollectionTerminal || !IsActiveInHierarchy)
            {
                ReleaseCanvasWorldCollection();
                return;
            }
            if (e.PropertyName == nameof(UITransform.Parent))
                RebindWorldCollectionAncestors();
            if (e.PropertyName is nameof(UITransform.Parent) or nameof(UITransform.Visibility))
                SyncCanvasWorldCollection();
        }

        /// <summary>Keeps authored visibility while canvas mode controls 3D collection membership.</summary>
        internal void SetCanvasWorldCollectionSuppressed(bool suppressed)
        {
            if (_worldCollectionSuppressed == suppressed)
                return;
            _worldCollectionSuppressed = suppressed;
            RenderInfo3D.SetCollectionSuppressed(suppressed);
        }
        private UIDrawPayloadBuilder? _publicationPayloadBuilder;
        private RenderCommandMesh2D? _publicationFrozenCommand;
        private RenderCommandMesh3D? _publicationFrozenWorldCommand;
        private UIBatchCollector? _registeredBatchCollector;
        private ulong _registeredPrimitiveHandle;
        private UIElementIdentity _registeredPrimitiveIdentity;

        /// <summary>Synchronizes the stable batch slot with this component's active canvas membership.</summary>
        internal void SyncBatchPrimitiveRegistration()
        {
            UITransform? transform = SceneNode is not null && !SceneNode.IsTransformNull
                ? Transform as UITransform
                : null;
            UIBatchCollector? collector = IsActiveInHierarchy && transform is not null
                ? transform?.GetCanvasComponent()?.BatchCollector
                : null;
            UIElementIdentity identity = transform?.ElementIdentity ?? default;
            if (collector?.Enabled != true || !identity.IsValid)
                collector = null;

            long membershipId = RenderInfo2D.MembershipId;
            if (ReferenceEquals(collector, _registeredBatchCollector) &&
                collector is not null &&
                identity == _registeredPrimitiveIdentity &&
                collector.GetRetainedPrimitiveHandle(membershipId, identity) == _registeredPrimitiveHandle)
                return;

            ReleaseBatchPrimitiveRegistration();
            if (collector is not null)
            {
                _registeredPrimitiveHandle = collector.RegisterPrimitiveMembership(membershipId, identity);
                _registeredBatchCollector = collector;
                _registeredPrimitiveIdentity = identity;
            }
        }

        private void ReleaseBatchPrimitiveRegistration()
        {
            _registeredBatchCollector?.UnregisterPrimitiveMembership(
                RenderInfo2D.MembershipId, _registeredPrimitiveHandle);
            _registeredBatchCollector = null;
            _registeredPrimitiveHandle = 0;
            _registeredPrimitiveIdentity = default;
        }

        protected override void OnComponentActivated()
        {
            if (_worldCollectionTerminal) return;
            if (_materialDeactivating)
            {
                _materialActivationPending = true;
                return;
            }
            _materialOwnerActive = false;
            ulong lifecycleRevision = ++_materialLifecycleRevision;
            _materialBindingRevision++;
            RebindWorldCollectionAncestors();
            SyncCanvasWorldCollection();
            base.OnComponentActivated();
            SyncBatchPrimitiveRegistration();
            if (_materialLifecycleRevision == lifecycleRevision && !_worldCollectionTerminal &&
                !IsDestroyed && !IsDestroyQueued && IsActiveInHierarchy)
            {
                _materialOwnerActive = true;
                AttachSubscribedMaterial(_material);
            }
        }

        protected override void OnComponentDeactivated()
        {
            if (_materialDeactivating) return;
            _materialDeactivating = true;
            _materialOwnerActive = false;
            _materialLifecycleRevision++;
            _materialBindingRevision++;
            bool completed = false;
            try
            {
                DetachSubscribedMaterial();
                ReleaseCanvasWorldCollection();
                ReleaseBatchPrimitiveRegistration();
                _publicationPayloadBuilder?.Dispose();
                _publicationFrozenCommand = null;
                _publicationFrozenWorldCommand = null;
                base.OnComponentDeactivated();
                completed = true;
            }
            finally
            {
                _materialDeactivating = false;
                bool replay = _materialActivationPending;
                _materialActivationPending = false;
                if (completed && replay && !_worldCollectionTerminal && IsActiveInHierarchy)
                    OnComponentActivated();
            }
        }

        protected override void OnDestroying()
        {
            _worldCollectionTerminal = true;
            _materialOwnerActive = false;
            _materialActivationPending = false;
            _materialLifecycleRevision++;
            _materialBindingRevision++;
            List<Exception>? failures = null;
            TryRelease(() => DetachSubscribedMaterial(), ref failures);
            TryRelease(ReleaseCanvasWorldCollection, ref failures);
            TryRelease(ReleaseBatchPrimitiveRegistration, ref failures);
            TryRelease(_publicationCommandObserver.Dispose, ref failures);
            RenderInfo3D.PropertyChanged -= WorldCollectionDrawPropertyChanged;
            RenderCommand3D.PropertyChanged -= WorldCollectionDrawPropertyChanged;
            try { base.OnDestroying(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            TryRelease(() => _publicationPayloadBuilder?.Dispose(), ref failures);
            TryRelease(RenderInfo3D.Dispose, ref failures);
            TryRelease(RenderInfo2D.Dispose, ref failures);
            if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException("UI renderable cleanup failed.", failures);
        }

        private static void TryRelease(Action release, ref List<Exception>? failures)
        {
            try { release(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
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
            SyncBatchPrimitiveRegistration();
            UIBoundableTransform bounds = BoundableTransform;
            Vector2 bottomLeft = bounds.ActualLocalBottomLeftTranslation;
            Matrix4x4 worldMatrix = bounds.WorldMatrix *
                (bounds.ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
            worldMatrix = CapturePublicationModelMatrix(in output, in worldMatrix);
            UICanvasComponent? canvas = bounds.ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
            bool directWorld = bounds.ParentCanvas is { DrawSpace: not ECanvasDrawSpace.Screen } &&
                canvas is not null && !canvas.UseOffscreenRenderingForNonScreenSpaces();
            bool supportsBatch = !directWorld && SupportsBatchedRendering && canvas?.BatchCollector.Enabled == true;
            bool visible = output.Visible && IsActiveInHierarchy &&
                (directWorld ? RenderInfo3D.IsVisible &&
                    RenderCommand3D.Enabled && RenderCommand3D.Instances > 0 :
                    RenderInfo2D.IsVisible && (supportsBatch || RenderCommand2D.Enabled && RenderCommand2D.Instances > 0));
            if (supportsBatch || !visible)
            {
                _publicationPayloadBuilder?.Dispose();
                _publicationFrozenCommand = null;
                _publicationFrozenWorldCommand = null;
            }
            RenderCommandMesh2D? frozenCommand = null;
            RenderCommandMesh3D? frozenWorldCommand = null;
            UIDrawPayload? payload = null;
            string? captureFailure = null;
            Vector4 frozenBounds = new(bottomLeft.X, bottomLeft.Y, output.ActualSize.X, output.ActualSize.Y);
            BoundingRectangle? effectiveCrop = UIClipRegion.ResolveCrop(bounds, ClipToBounds);
            if (!supportsBatch && visible)
            {
                if (_publicationPayloadBuilder is null)
                {
                    _publicationPayloadBuilder = new UIDrawPayloadBuilder(OnMaterialSettingUniforms);
                    _publicationPayloadBuilder.SourceChanged += PublicationDrawChanged;
                }
                _publicationPayloadBuilder.Bounds = frozenBounds;
                bool cropReady = true;
                UIWorldCropCapture? worldClip = null;
                if (directWorld && effectiveCrop is { } worldCrop)
                {
                    cropReady = UIWorldCropShaderContract.TryCapture(
                        worldCrop, worldMatrix, out UIWorldCropCapture clipped);
                    if (cropReady)
                        worldClip = clipped;
                }
                _publicationPayloadBuilder.WorldCrop = worldClip;
                if (!cropReady)
                    captureFailure = "UI.DrawCapture.WorldCropRange: the captured canvas crop or transform is unsupported.";
                else if (TryCapturePublicationPayload(in output, _publicationPayloadBuilder,
                             out payload, out captureFailure) && payload is not null &&
                         (!directWorld || effectiveCrop == payload.WorldCrop?.Region))
                {
                    RenderCommandMesh2D source = RenderCommand2D;
                    var crop = effectiveCrop;
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
                    if (directWorld)
                    {
                        RenderCommandMesh3D worldSource = RenderCommand3D;
                        if (worldSource.MaterialOverride is not null || worldSource.RenderOptionsOverride is not null)
                            captureFailure = "UI.DrawCapture.WorldOverrideAdapterRequired: direct-world material or raster overrides need an immutable payload adapter.";
                        else
                        {
                            Matrix4x4 frozenWorldMatrix = worldMatrix * bounds.ParentCanvas!.WorldMatrix;
                            RenderCommandMesh3D? cachedWorld = _publicationFrozenWorldCommand;
                            if (cachedWorld is not null && ReferenceEquals(cachedWorld.Mesh, payload.Renderer) &&
                                cachedWorld.WorldMatrix == frozenWorldMatrix && cachedWorld.RenderPass == worldSource.RenderPass &&
                                ReferenceEquals(cachedWorld.MaterialOverride, payload.Material) &&
                                ReferenceEquals(cachedWorld.RenderOptionsOverride, payload.RenderOptions) &&
                                cachedWorld.Instances == worldSource.Instances &&
                                cachedWorld.WorldMatrixIsModelMatrix == worldSource.WorldMatrixIsModelMatrix &&
                                cachedWorld.ForceCpuRendering == worldSource.ForceCpuRendering &&
                                cachedWorld.EditorHighlightBits == worldSource.EditorHighlightBits &&
                                Equals(cachedWorld.WorldCullingVolumeOverride, worldSource.WorldCullingVolumeOverride) &&
                                cachedWorld.GpuProfilingLabel == worldSource.GpuProfilingLabel &&
                                cachedWorld.Enabled == worldSource.Enabled)
                                frozenWorldCommand = cachedWorld;
                            else
                                frozenWorldCommand = _publicationFrozenWorldCommand = new RenderCommandMesh3D(worldSource.RenderPass)
                                {
                                    Mesh = payload.Renderer,
                                    WorldMatrix = frozenWorldMatrix,
                                    MaterialOverride = payload.Material,
                                    RenderOptionsOverride = payload.RenderOptions,
                                    Instances = worldSource.Instances,
                                    WorldMatrixIsModelMatrix = worldSource.WorldMatrixIsModelMatrix,
                                    ForceCpuRendering = worldSource.ForceCpuRendering,
                                    EditorHighlightBits = worldSource.EditorHighlightBits,
                                    WorldCullingVolumeOverride = worldSource.WorldCullingVolumeOverride,
                                    GpuProfilingLabel = worldSource.GpuProfilingLabel,
                                    Enabled = worldSource.Enabled,
                                };
                        }
                    }
                }
                else if (directWorld && payload is not null &&
                         effectiveCrop != payload.WorldCrop?.Region)
                    captureFailure = "UI.DrawCapture.WorldCropChanged: the captured draw crop did not match the frozen record.";
            }
            return new UIRenderRecord
            {
                RenderInfo = RenderInfo2D,
                MembershipId = RenderInfo2D.MembershipId,
                Identity = bounds.ElementIdentity,
                WorldMatrix = worldMatrix,
                Bounds = new Vector4(bottomLeft.X, bottomLeft.Y, output.ActualSize.X, output.ActualSize.Y),
                Crop = effectiveCrop,
                RenderPass = RenderPass,
                ZIndex = RenderCommand2D.ZIndex,
                Material = payload?.Material ?? _material,
                Visible = visible,
                SupportsBatch = supportsBatch,
                FrozenCommand = frozenCommand,
                FrozenWorldCommand = frozenWorldCommand,
                WorldRenderInfo = RenderInfo3D,
                SourceWorldCommand = RenderCommand3D,
                WorldRegistrationIncarnation = RenderInfo3D.WorldRegistrationIncarnation,
                WorldCastsShadows = RenderInfo3D.CastsShadows,
                WorldLayer = RenderInfo3D.Layer,
                WorldCullingVolume = RenderInfo3D.CaptureWorldCullingVolume(),
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
        private XRMaterial? _subscribedMaterial;
        private bool _materialOwnerActive;
        private bool _materialDeactivating;
        private bool _materialActivationPending;
        private ulong _materialLifecycleRevision;
        private ulong _materialBindingRevision;
        private bool CanObserveMaterial => _materialOwnerActive && !_worldCollectionTerminal &&
            !_materialDeactivating && !IsDestroyed && !IsDestroyQueued && IsActiveInHierarchy;
        public virtual XRMaterial? Material
        {
            get => _material;
            set
            {
                if (_worldCollectionTerminal || IsDestroyed || IsDestroyQueued) return;
                _materialBindingRevision++;
                SetField(ref _material, value);
            }
        }

        private void DetachSubscribedMaterial()
        {
            XRMaterial? subscribed = _subscribedMaterial;
            _subscribedMaterial = null;
            if (subscribed is not null)
                subscribed.SettingUniforms -= OnMaterialSettingUniforms;
        }

        private void AttachSubscribedMaterial(XRMaterial? material)
        {
            if (!CanObserveMaterial || !ReferenceEquals(_material, material) ||
                ReferenceEquals(_subscribedMaterial, material)) return;
            DetachSubscribedMaterial();
            if (material is null) return;
            _subscribedMaterial = material;
            material.SettingUniforms += OnMaterialSettingUniforms;
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
            ulong revision = _materialBindingRevision;
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (propName == nameof(Material))
            {
                if (!change || _worldCollectionTerminal || revision != _materialBindingRevision)
                    return false;
                DetachSubscribedMaterial();
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            ulong revision = _materialBindingRevision;
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(Material))
            {
                if (_worldCollectionTerminal || revision != _materialBindingRevision ||
                    !ReferenceEquals(_material, field)) return;
                var m = Mesh;
                m?.Material = _material;
                if (_worldCollectionTerminal || revision != _materialBindingRevision ||
                    !ReferenceEquals(_material, field)) return;
                if (CanObserveMaterial)
                    AttachSubscribedMaterial(_material);
            }
            if (propName is nameof(Material) or nameof(UIMaterialComponent.DisableBatching))
                SyncBatchPrimitiveRegistration();
        }

        protected override void OnTransformChanging()
        {
            ReleaseCanvasWorldCollection();
            ReleaseBatchPrimitiveRegistration();
            base.OnTransformChanging();
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            if (IsActiveInHierarchy && !_worldCollectionTerminal)
            {
                RebindWorldCollectionAncestors();
                SyncCanvasWorldCollection();
            }
            SyncBatchPrimitiveRegistration();
        }

        protected override void UITransformPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName == nameof(UITransform.ParentCanvas))
            {
                ReleaseCanvasWorldCollection();
                ReleaseBatchPrimitiveRegistration();
            }
            base.UITransformPropertyChanging(sender, e);
        }

        private Vector4 _lastBounds = Vector4.Zero;
        protected virtual void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            if (!CanObserveMaterial || !ReferenceEquals(material, _subscribedMaterial)) return;
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
            if (IsActiveInHierarchy && !_worldCollectionTerminal)
            {
                if (e.PropertyName == nameof(UITransform.Parent))
                    RebindWorldCollectionAncestors();
                if (e.PropertyName is nameof(UITransform.ParentCanvas) or nameof(UITransform.Parent) or nameof(UITransform.Visibility))
                    SyncCanvasWorldCollection();
            }
            else
                ReleaseCanvasWorldCollection();
            if (e.PropertyName == nameof(UITransform.ParentCanvas))
                SyncBatchPrimitiveRegistration();
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
