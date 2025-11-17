using System.ComponentModel;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;

namespace XREngine.Rendering.UI
{
    [RequiresTransform(typeof(UITransform))]
    public class UIComponent : XRComponent
    {
        [Browsable(false)]
        public UITransform UITransform => TransformAs<UITransform>(true)!;

        [Browsable(false)]
        public UICanvasComponent? UserInterfaceCanvas => UITransform.GetCanvasComponent();

        internal override void VerifyInterfacesOnStart()
        {
            base.VerifyInterfacesOnStart();

            var canvas = UserInterfaceCanvas;
            var tfm = UITransform;

            //if (tfm is IRenderable r)
            //    foreach (var obj in r.RenderedObjects)
            //        obj.UserInterfaceCanvas = canvas;
            
            if (this is not IRenderable rend)
                return;

            tfm.PropertyChanging += UITransformPropertyChanging;
            tfm.PropertyChanged += UITransformPropertyChanged;

            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = canvas;
        }
        internal override void VerifyInterfacesOnStop()
        {
            base.VerifyInterfacesOnStop();

            var tfm = UITransform;

            //if (tfm is IRenderable r)
            //    foreach (var obj in r.RenderedObjects)
            //        obj.UserInterfaceCanvas = null;

            if (this is not IRenderable rend)
                return;

            tfm.PropertyChanging -= UITransformPropertyChanging;
            tfm.PropertyChanged -= UITransformPropertyChanged;

            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = null;
        }

        protected override void OnTransformChanging()
        {
            base.OnTransformChanging();

            if (this is not IRenderable rend || SceneNode.IsTransformNull || Transform is not UITransform uiTfm)
                return;

            //if (uiTfm is IRenderable r)
            //    foreach (var obj in r.RenderedObjects)
            //        obj.UserInterfaceCanvas = null;

            uiTfm.PropertyChanging -= UITransformPropertyChanging;
            uiTfm.PropertyChanged -= UITransformPropertyChanged;

            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = null;
        }
        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();

            if (this is not IRenderable rend || SceneNode.IsTransformNull || Transform is not UITransform uiTfm)
                return;

            //if (uiTfm is IRenderable r)
            //    foreach (var obj in r.RenderedObjects)
            //        obj.UserInterfaceCanvas = UserInterfaceCanvas;

            uiTfm.PropertyChanging += UITransformPropertyChanging;
            uiTfm.PropertyChanged += UITransformPropertyChanged;

            var canvas = uiTfm.GetCanvasComponent();
            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = canvas;
        }
        /// <summary>
        /// Called when one of the transform's properties about to change.
        /// Linking this callback to the current UITransform is handled automatically for you.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void UITransformPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName != nameof(UITransform.ParentCanvas) || this is not IRenderable rend)
                return;
            
            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = null;
        }
        /// <summary>
        /// Called when one of the transform's properties has changed.
        /// Linking this callback to the current UITransform is handled automatically for you.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(UITransform.ParentCanvas) || this is not IRenderable rend)
                return;

            var canvas = UserInterfaceCanvas;
            foreach (var obj in rend.RenderedObjects)
                obj.UserInterfaceCanvas = canvas;
        }
    }
}
