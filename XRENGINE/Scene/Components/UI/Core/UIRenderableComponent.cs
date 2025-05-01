using System.Numerics;
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
        public const string UIWidthUniformName = "UIWidth";
        public const string UIHeightUniformName = "UIHeight";

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

        protected virtual bool ShouldRender3D(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var tfm = BoundableTransform;
            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false) || !tfm.IsVisibleInHierarchy)
                return false;
            var canvas = tfm.ParentCanvas;
            return canvas is not null && canvas.DrawSpace != ECanvasDrawSpace.Screen;
        }

        protected virtual bool ShouldRender2D(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            var tfm = BoundableTransform;
            if (!(tfm.ParentCanvas?.SceneNode?.IsActiveInHierarchy ?? false) || !tfm.IsVisibleInHierarchy)
                return false;
            var canvas = tfm.ParentCanvas;
            return canvas is not null && canvas.DrawSpace == ECanvasDrawSpace.Screen;
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            base.OnTransformRenderWorldMatrixChanged(transform);

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
                        if (Material is not null)
                            Material.SettingUniforms -= OnMaterialSettingUniforms;
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
                    if (m is not null)
                        m.Material = Material;
                    if (Material is not null)
                        Material.SettingUniforms += OnMaterialSettingUniforms;
                    break;
            }
        }

        protected virtual void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            var m = Material;
            if (m is null)
                return;

            var tfm = BoundableTransform;
            var w = tfm.ActualWidth;
            var h = tfm.ActualHeight;

            program.Uniform(UIWidthUniformName, w);
            program.Uniform(UIHeightUniformName, h);
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
