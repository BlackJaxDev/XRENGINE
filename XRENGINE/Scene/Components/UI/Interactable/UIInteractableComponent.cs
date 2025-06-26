using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Input.Devices;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    public delegate void DelMouseMove(float x, float y, UIInteractableComponent comp);
    /// <summary>
    /// Bounded UI component that can be interacted with by the player.
    /// </summary>
    [RequiresTransform(typeof(UIBoundableTransform))]
    public abstract class UIInteractableComponent : UIComponent, IRenderable
    {
        public UIInteractableComponent()
        {
            //Use render tree purely for culling volume testing, not for rendering.
            RenderInfo3D = RenderInfo3D.New(this);
            RenderInfo2D = RenderInfo2D.New(this);
            RenderedObjects = [RenderInfo2D, RenderInfo3D];
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

            if (transform is not UIBoundableTransform tfm)
                return;

            tfm.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
        }
        protected override void UITransformPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.UITransformPropertyChanged(sender, e);
            switch (e.PropertyName)
            {
                case nameof(UIBoundableTransform.ActualSize):
                    BoundableTransform.UpdateRenderInfoBounds(RenderInfo2D, RenderInfo3D);
                    break;
            }
        }

        public RenderInfo3D RenderInfo3D { get; }
        public RenderInfo2D RenderInfo2D { get; }

        public UIBoundableTransform BoundableTransform => TransformAs<UIBoundableTransform>(true)!;

        public event Action<UIInteractableComponent>? GotFocus;
        public event Action<UIInteractableComponent>? LostFocus;
        public event DelMouseMove? MouseMove;
        public event Action<UIInteractableComponent>? MouseOverlapEnter;
        public event Action<UIInteractableComponent>? MouseOverlapLeave;
        public event Action<UIInteractableComponent>? MouseDirectOverlapEnter;
        public event Action<UIInteractableComponent>? MouseDirectOverlapLeave;
        public event Action<UIInteractableComponent>? GamepadNavigateEnter;
        public event Action<UIInteractableComponent>? GamepadNavigateLeave;

        private bool _registerInputsOnFocus = true;
        /// <summary>
        /// When true, this component will register inputs when it gains focus and unregister inputs when it loses focus.
        /// </summary>
        public bool RegisterInputsOnFocus
        {
            get => _registerInputsOnFocus;
            set => SetField(ref _registerInputsOnFocus, value);
        }

        private bool _isFocused = false;
        /// <summary>
        /// Set when this component has focus.
        /// Focus is set after the mouse clicks on this component or when gamepad navigates to it.
        /// </summary>
        public bool IsFocused
        {
            get => _isFocused;
            set => SetField(ref _isFocused, value);
        }

        private bool _isMouseOver = false;
        /// <summary>
        /// Set when the mouse is over this component.
        /// </summary>
        public bool IsMouseOver
        {
            get => _isMouseOver;
            set => SetField(ref _isMouseOver, value);
        }

        private bool _isMouseDirectlyOver = false;
        /// <summary>
        /// Set when the mouse is directly over this component.
        /// "Directly over" means this component is the top-most component under the mouse.
        /// </summary>
        public bool IsMouseDirectlyOver
        {
            get => _isMouseDirectlyOver;
            set => SetField(ref _isMouseDirectlyOver, value);
        }

        public UIInteractableComponent? GamepadUpComponent { get; set; }
        public UIInteractableComponent? GamepadDownComponent { get; set; }
        public UIInteractableComponent? GamepadLeftComponent { get; set; }
        public UIInteractableComponent? GamepadRightComponent { get; set; }
        public RenderInfo[] RenderedObjects { get; }

        private bool _needsMouseMove = false;
        /// <summary>
        /// Set to true if this component needs mouse move events.
        /// </summary>
        public bool NeedsMouseMove
        {
            get => _needsMouseMove;
            set => SetField(ref _needsMouseMove, value);
        }

        private bool _interactOnButtonDown = false;
        public bool InteractOnButtonDown
        {
            get => _interactOnButtonDown;
            set => SetField(ref _interactOnButtonDown, value);
        }

        protected virtual void OnMouseMove(float x, float y)
            => MouseMove?.Invoke(x, y, this);
        protected virtual void OnMouseOverlapEnter()
            => MouseOverlapEnter?.Invoke(this);
        protected virtual void OnMouseOverlapLeave()
            => MouseOverlapLeave?.Invoke(this);
        protected virtual void OnMouseDirectOverlapEnter()
            => MouseDirectOverlapEnter?.Invoke(this);
        protected virtual void OnMouseDirectOverlapLeave()
            => MouseDirectOverlapLeave?.Invoke(this);
        protected virtual void OnGamepadNavigateEnter()
            => GamepadNavigateEnter?.Invoke(this);
        protected virtual void OnGamepadNavigateLeave()
            => GamepadNavigateLeave?.Invoke(this);

        /// <summary>
        /// Called when this component gains focus.
        /// Focus is gained after the mouse clicks on this component or the gamepad navigates to it.
        /// </summary>
        protected virtual void OnGotFocus()
        {
            //if (RegisterInputsOnFocus)
            //{
            //    var input = OwningUserInterface?.LocalPlayerController?.Input;
            //    if (input != null)
            //    {
            //        input.Unregister = false;
            //        RegisterInputs(input);
            //    }
            //}

            GotFocus?.Invoke(this);
        }

        /// <summary>
        /// Called when this component loses focus.
        /// Focus is lost when the mouse clicks off this component or the gamepad navigates away from it.
        /// </summary>
        protected virtual void OnLostFocus()
        {
            if (RegisterInputsOnFocus)
            {
                //var input = OwningUserInterface?.LocalPlayerController?.Input;
                //if (input != null)
                //{
                //    input.Unregister = true;
                //    RegisterInputs(input);
                //    input.Unregister = false;
                //}
            }

            LostFocus?.Invoke(this);
        }

        public event Action<UIInteractableComponent>? BackAction;

        /// <summary>
        /// Typically mapped to the "B" button on a gamepad to go back or cancel an action, etc.
        /// </summary>
        public void OnBack()
        {
            BackAction?.Invoke(this);
        }

        public event Action<UIInteractableComponent>? InteractAction;
        
        /// <summary>
        /// Typically mapped to the "A" button on a gamepad to click buttons, etc.
        /// </summary>
        public void OnInteract()
        {
            InteractAction?.Invoke(this);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(IsMouseOver):
                    if (IsMouseOver)
                        OnMouseOverlapEnter();
                    break;
                case nameof(IsMouseDirectlyOver):
                    if (IsMouseDirectlyOver)
                        OnMouseDirectOverlapEnter();
                    break;
                case nameof(IsFocused):
                    if (IsFocused)
                        OnGotFocus();
                    break;
            }
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(IsMouseOver):
                        if (IsMouseOver)
                            OnMouseOverlapLeave();
                        break;
                    case nameof(IsMouseDirectlyOver):
                        if (IsMouseDirectlyOver)
                            OnMouseDirectOverlapLeave();
                        break;
                    case nameof(IsFocused):
                        if (IsFocused)
                            OnLostFocus();
                        break;
                }
            }
            return change;
        }

        public virtual void MouseMoved(Vector2 lastPosLocal, Vector2 posLocal)
        {
            OnMouseMove(posLocal.X, posLocal.Y);
        }

        public virtual void RegisterInput(InputInterface input)
        {

        }
    }
}
