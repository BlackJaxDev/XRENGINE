using System.Numerics;
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
            RenderInfo3D.PreCollectCommandsCallback = ShouldRender3D;
            RenderInfo2D.PreCollectCommandsCallback = ShouldRender2D;
        }

        //TODO: register callback on canvas to set RenderInfo3D/2D Visible property so no quadtree/octree culling is done if the canvas is not visible

        private static int _shouldRender3DDiagCount = 0;
        protected virtual bool ShouldRender3D(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var tfm = BoundableTransform;
            bool diagLog = _shouldRender3DDiagCount < 20;
            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false) || !tfm.IsVisibleInHierarchy)
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': parentCanvasActive={tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy} visibleInHierarchy={tfm.IsVisibleInHierarchy} parentCanvas={tfm.ParentCanvas?.SceneNode?.Name}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            var canvas = tfm.ParentCanvas;
            if (canvas is null || canvas.DrawSpace == ECanvasDrawSpace.Screen)
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': canvasNull={canvas is null} drawSpace={canvas?.DrawSpace}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            var canvasComponent = canvas.SceneNode?.GetComponent<UICanvasComponent>();
            if (canvasComponent?.UseOffscreenRenderingForNonScreenSpaces() ?? true)
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender3D] REJECTED {GetType().Name} on '{SceneNode?.Name}': effectiveOffscreen={canvasComponent?.UseOffscreenRenderingForNonScreenSpaces()} preferOffscreen={canvasComponent?.PreferOffscreenRenderingForNonScreenSpaces} canvasComp={canvasComponent is not null}");
                    _shouldRender3DDiagCount++;
                }
                return false;
            }

            if (diagLog)
            {
                Debug.UI($"[ShouldRender3D] ACCEPTED {GetType().Name} on '{SceneNode?.Name}': drawSpace={canvas.DrawSpace} mesh={Mesh is not null} renderPass={RenderCommand3D.RenderPass}");
                _shouldRender3DDiagCount++;
            }

            return true;
        }

        private static int _shouldRender2DDiagCount = 0;
        protected virtual bool ShouldRender2D(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var tfm = BoundableTransform;
            bool diagLog = _shouldRender2DDiagCount < 20;

            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false) || !tfm.IsVisibleInHierarchy)
            {
                if (diagLog)
                {
                    Debug.UI($"[ShouldRender2D] REJECTED {GetType().Name} on '{SceneNode?.Name}': parentCanvasActive={tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy} visibleInHierarchy={tfm.IsVisibleInHierarchy} parentCanvas={tfm.ParentCanvas?.SceneNode?.Name}");
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
            if (SupportsBatchedRendering)
            {
                if (canvasComp?.BatchCollector is { Enabled: true } collector)
                {
                    if (RegisterWithBatchCollector(collector))
                        return false; // Successfully batched — skip individual command
                    // Registration failed (e.g. font not loaded yet); fall through to individual render
                }
            }

            if (diagLog)
            {
                Debug.UI($"[ShouldRender2D] ACCEPTED {GetType().Name} on '{SceneNode?.Name}': drawSpace={canvas.DrawSpace} mesh={Mesh is not null} renderPass={RenderCommand2D.RenderPass}");
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
        /// <returns>True if the component was successfully registered for batching; false to fall back to individual rendering.</returns>
        protected virtual bool RegisterWithBatchCollector(UIBatchCollector collector) => false;

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

            if (transform is not UIBoundableTransform tfm)
                return;

            tfm.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
            var mtx = GetRenderWorldMatrix(tfm);
            RenderCommand3D.WorldMatrix = mtx;
            RenderCommand2D.WorldMatrix = mtx;
        }

        protected virtual Matrix4x4 GetRenderWorldMatrix(UIBoundableTransform tfm)
            => tfm.RenderMatrix;

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
                RenderCommand3D.Mesh = value;
                RenderCommand2D.Mesh = value;

                if (_material != value?.Material)
                    Material = value?.Material;
            }
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
            var tfm = BoundableTransform;
            var w = tfm.ActualWidth;
            var h = tfm.ActualHeight;
            var bottomLeft = tfm.ActualLocalBottomLeftTranslation;
            var x = bottomLeft.X;
            var y = bottomLeft.Y;

            if (x == _lastBounds.X && 
                y == _lastBounds.Y && 
                w == _lastBounds.Z && 
                h == _lastBounds.W)
                return; //No change, no need to update uniforms

            var bounds = new Vector4(x, y, w, h);

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
                case nameof(ClipToBounds):
                    //Toggle setting the region here
                    RenderCommand2D.WorldCropRegion = ClipToBounds ? BoundableTransform.AxisAlignedRegion.AsBoundingRectangle() : null;
                    break;
                case nameof(UIBoundableTransform.AxisAlignedRegion):
                    //But only update the crop region if we're clipping to bounds
                    if (ClipToBounds)
                        RenderCommand2D.WorldCropRegion = BoundableTransform.AxisAlignedRegion.AsBoundingRectangle();
                    break;
            }
        }

        private bool _clipToBounds = false;
        /// <summary>
        /// If true, this UI component will be scissor-tested (cropped) to its bounds.
        /// Any pixels outside of the bounds will not be rendered, which is useful for things like text or scrolling regions.
        /// </summary>
        public bool ClipToBounds
        {
            get => _clipToBounds;
            set => SetField(ref _clipToBounds, value);
        }
    }
}
