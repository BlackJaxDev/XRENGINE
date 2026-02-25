using Extensions;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// Enables and manages input for a UI canvas component.
    /// </summary>
    [RequireComponents(typeof(UICanvasComponent))]
    public class UICanvasInputComponent : XRComponent
    {
        /// <summary>
        /// Returns the canvas component this input component is controlling.
        /// </summary>
        public UICanvasComponent? GetCameraCanvas() => Canvas ?? GetSiblingComponent<UICanvasComponent>(true);

        /// <summary>
        /// The canvas component this input component is controlling.
        /// If null, the component will attempt to find a sibling canvas component.
        /// </summary>
        private UICanvasComponent? _canvas;
        public UICanvasComponent? Canvas
        {
            get => _canvas;
            set => SetField(ref _canvas, value);
        }

        private UIInteractableComponent? _focusedComponent;
        /// <summary>
        /// The UI component focused on by the gamepad or last interacted with by the mouse.
        /// </summary>
        public UIInteractableComponent? FocusedComponent
        {
            get => _focusedComponent;
            set => SetField(ref _focusedComponent, value);
        }

        /// <summary>
        /// True when a Ctrl key (left or right) is currently held.
        /// </summary>
        public bool IsCtrlHeld { get; private set; }

        /// <summary>
        /// True when a Shift key (left or right) is currently held.
        /// </summary>
        public bool IsShiftHeld { get; private set; }

        /// <summary>
        /// True when an Alt key (left or right) is currently held.
        /// </summary>
        public bool IsAltHeld { get; private set; }

        /// <summary>
        /// Fired when the left mouse button is pressed.
        /// The interactable under the cursor is passed (may be null if clicking empty space).
        /// </summary>
        public event Action<UIInteractableComponent?>? LeftClickDown;

        /// <summary>
        /// Fired when the right mouse button is pressed while the cursor is over an interactable component.
        /// The interactable under the cursor is passed as the argument.
        /// </summary>
        public event Action<UIInteractableComponent>? RightClick;

        /// <summary>
        /// Fired when the Escape key is pressed.
        /// </summary>
        public event Action? EscapePressed;

        private PawnComponent? _owningPawn;
        /// <summary>
        /// The pawn that has this HUD linked for screen or camera space use.
        /// World space HUDs do not require a pawn - call SetCursorPosition to set the cursor position.
        /// </summary>
        public PawnComponent? OwningPawn
        {
            get => _owningPawn;
            set => SetField(ref _owningPawn, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(OwningPawn):
                        UnlinkOwningPawn();
                        break;
                    case nameof(FocusedComponent):
                        if (_focusedComponent is not null)
                        {
                            //_focusedComponent.IsFocused = false;
                            //_focusedComponent.PropertyChanged -= FocusedComponentPropertyChanged;
                            //var input = _owningPawn!.LocalPlayerController!.Input;
                            //if (_focusedComponent.RegisterInputsOnFocus)
                            //{
                            //    input.Unregister = true;
                            //    _focusedComponent.RegisterInput(input);
                            //    input.Unregister = false;
                            //}
                        }
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
                case nameof(OwningPawn):
                    LinkOwningPawn();
                    break;
                case nameof(FocusedComponent):
                    if (_focusedComponent is not null)
                    {
                        _focusedComponent.IsFocused = true;
                        _focusedComponent.PropertyChanged += FocusedComponentPropertyChanged;
                        var input = _owningPawn?.LocalPlayerController?.Input;
                        if (input is not null && _focusedComponent.RegisterInputsOnFocus)
                            _focusedComponent.RegisterInput(input);
                    }
                    if (prev is UIInteractableComponent prevInteractable)
                    {
                        prevInteractable.IsFocused = false;
                        prevInteractable.PropertyChanged -= FocusedComponentPropertyChanged;
                        var input = _owningPawn?.LocalPlayerController?.Input;
                        if (input is not null && prevInteractable.RegisterInputsOnFocus)
                        {
                            input.Unregister = true;
                            prevInteractable.RegisterInput(input);
                            input.Unregister = false;
                        }
                    }
                    var localPlayerController = _owningPawn?.LocalPlayerController;
                    if (localPlayerController is not null)
                        localPlayerController.FocusedUIComponent = _focusedComponent;
                    break;
            }
        }

        private void FocusedComponentPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(UIInteractableComponent.IsFocused):
                    if (!(_focusedComponent?.IsFocused ?? false))
                        FocusedComponent = null;
                    break;
                case nameof(UIInteractableComponent.RegisterInputsOnFocus):
                    if (_focusedComponent is null)
                        break;
                    
                    var input = _owningPawn!.LocalPlayerController!.Input;
                    if (_focusedComponent.RegisterInputsOnFocus)
                        _focusedComponent.RegisterInput(input);
                    else
                    {
                        input.Unregister = true;
                        _focusedComponent.RegisterInput(input);
                        input.Unregister = false;
                    }
                    break;
            }
        }

        /// <summary>
        /// Unlinks input from the pawn connected to this input component.
        /// </summary>
        private void UnlinkOwningPawn()
        {
            if (_owningPawn is null || _owningPawn.LocalPlayerController == null)
                return;

            _owningPawn.PropertyChanging -= OwningPawnPropertyChanging;
            _owningPawn.PropertyChanged -= OwningPawnPropertyChanged;
            _owningPawn.LinkedUICanvasInputs.Remove(this);

            UnlinkInput();
        }

        /// <summary>
        /// Links input from the pawn connected to this input component.
        /// </summary>
        private void LinkOwningPawn()
        {
            if (_owningPawn is null)
                return;

            _owningPawn.PropertyChanging += OwningPawnPropertyChanging;
            _owningPawn.PropertyChanged += OwningPawnPropertyChanged;
            _owningPawn.LinkedUICanvasInputs.Add(this);

            LinkInput();
        }

        private void OwningPawnPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            if (e.PropertyName != nameof(PawnComponent.LocalPlayerController))
                return;
            
            UnlinkInput();
        }

        private void OwningPawnPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PawnComponent.LocalPlayerController))
                return;
            
            LinkInput();
        }

        private void LinkInput()
        {
            //Link input commands from the owning controller to this hud
            var input = _owningPawn!.LocalPlayerController!.Input;
            input.TryUnregisterInput();
            input.InputRegistration += RegisterInput;
            if (FocusedComponent is not null && FocusedComponent.RegisterInputsOnFocus)
                input.InputRegistration += FocusedComponent.RegisterInput;
            input.TryRegisterInput();
        }

        private void UnlinkInput()
        {
            //Unlink input commands from the owning controller to this hud
            var input = _owningPawn!.LocalPlayerController!.Input;
            input.TryUnregisterInput();
            input.InputRegistration -= RegisterInput;
            if (FocusedComponent is not null && FocusedComponent.RegisterInputsOnFocus)
                input.InputRegistration -= FocusedComponent.RegisterInput;
            input.TryRegisterInput();
        }

        public void RegisterInput(InputInterface input)
        {
            input.RegisterMouseMove(MouseMove, EMouseMoveType.Absolute);
            input.RegisterMouseScroll(OnMouseScroll);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, OnMouseInteractButtonDown);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released, OnMouseInteractButtonUp);
            input.RegisterMouseButtonEvent(EMouseButton.RightClick, EButtonInputType.Pressed, OnMouseRightButtonDown);

            input.RegisterKeyStateChange(EKey.ControlLeft, OnCtrlLeftStateChanged);
            input.RegisterKeyStateChange(EKey.ControlRight, OnCtrlRightStateChanged);
            input.RegisterKeyStateChange(EKey.ShiftLeft, OnShiftLeftStateChanged);
            input.RegisterKeyStateChange(EKey.ShiftRight, OnShiftRightStateChanged);
            input.RegisterKeyStateChange(EKey.AltLeft, OnAltLeftStateChanged);
            input.RegisterKeyStateChange(EKey.AltRight, OnAltRightStateChanged);
            input.RegisterKeyStateChange(EKey.Escape, OnEscapeStateChanged);

            input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Pressed, OnGamepadInteractButtonDown);
            input.RegisterButtonEvent(EGamePadButton.FaceDown, EButtonInputType.Released, OnGamepadInteractButtonUp);

            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Pressed, OnGamepadBackButtonDown);
            input.RegisterButtonEvent(EGamePadButton.FaceRight, EButtonInputType.Released, OnGamepadBackButtonUp);

            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickX, OnLeftStickX, false);
            input.RegisterAxisUpdate(EGamePadAxis.LeftThumbstickY, OnLeftStickY, false);

            input.RegisterButtonEvent(EGamePadButton.DPadUp, EButtonInputType.Pressed, OnDPadUp);
            input.RegisterButtonEvent(EGamePadButton.DPadDown, EButtonInputType.Pressed, OnDPadDown);
            input.RegisterButtonEvent(EGamePadButton.DPadLeft, EButtonInputType.Pressed, OnDPadLeft);
            input.RegisterButtonEvent(EGamePadButton.DPadRight, EButtonInputType.Pressed, OnDPadRight);
        }

        private void OnMouseScroll(float diff)
        {
            if (MathF.Abs(diff) <= float.Epsilon)
                return;

            // Prefer the top-most element (including non-interactables), then fall back to interactables.
            var node = TopMostElement?.SceneNode ?? TopMostInteractable?.SceneNode;
            while (node is not null)
            {
                foreach (var comp in node.Components)
                {
                    if (comp is IUIScrollReceiver scrollReceiver && scrollReceiver.HandleMouseScroll(diff))
                        return;
                }

                node = node.Parent;
            }
        }

        /// <summary>
        /// The location of the mouse cursor in world space.
        /// </summary>
        public Vector2 CursorPositionWorld2D { get; private set; }
        /// <summary>
        /// The location of the mouse cursor in world space on the last update.
        /// </summary>
        public Vector2 LastCursorPositionWorld2D { get; private set; }
        
        /// <summary>
        /// 
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        private void MouseMove(float x, float y)
        {
            var vp = OwningPawn?.Viewport;
            if (vp is null)
                return;

            var canvas = GetCameraCanvas();
            if (canvas is null)
                return;

            var vpCoord = vp.ScreenToViewportCoordinate(new Vector2(x, y));
            var normCoord = vp.NormalizeViewportCoordinate(vpCoord);
            normCoord.Y = 1.0f - normCoord.Y;

            var canvasTransform = canvas.CanvasTransform;
            var space = canvasTransform.DrawSpace;

            XRCamera? pointerCamera = space switch
            {
                ECanvasDrawSpace.Screen => canvas.Camera2D,
                ECanvasDrawSpace.Camera => canvasTransform.CameraSpaceCamera ?? vp.ActiveCamera,
                _ => null
            };

            Vector2? uiCoord = GetUICoordinate(vp, pointerCamera, normCoord, canvasTransform, space);

            if (uiCoord is not null)
                CursorPositionWorld2D = uiCoord.Value;
        }

        private void CollectVisible()
        {
            var tree = GetCameraCanvas()?.VisualScene2D?.RenderTree;
            tree?.FindAllIntersectingSorted(CursorPositionWorld2D, UIElementIntersections, UIElementPredicate);
        }

        private void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("UICanvasInputComponent.SwapBuffers");

            // If any intersected element has BlocksInputBehind, remove items not in its subtree.
            FilterBlockedInput();

            // Select the deepest (most nested) element under the cursor.
            // Children are visually on top of parents, so with matching LayerIndex
            // the deepest transform in the UI hierarchy should receive input.
            TopMostElement = UIElementIntersections
                .Where(x => x.Owner is UIComponent)
                .OrderByDescending(x => (x.Owner as UIComponent)?.Transform?.Depth ?? int.MinValue)
                .ThenByDescending(x => x.LayerIndex)
                .ThenByDescending(x => x.IndexWithinLayer)
                .FirstOrDefault()?.Owner as UIComponent;

            TopMostInteractable = UIElementIntersections
                .Where(x => x.Owner is UIInteractableComponent)
                .OrderByDescending(x => (x.Owner as UIInteractableComponent)?.Transform?.Depth ?? int.MinValue)
                .ThenByDescending(x => x.LayerIndex)
                .ThenByDescending(x => x.IndexWithinLayer)
                .FirstOrDefault()?.Owner as UIInteractableComponent;

            ValidateAndSwapIntersections();
            LastCursorPositionWorld2D = CursorPositionWorld2D;
        }

        private static Vector2? GetUICoordinate(XRViewport worldVP, XRCamera? inputCamera, Vector2 normCoord, UICanvasTransform canvasTransform, ECanvasDrawSpace space)
        {
            Vector2? uiCoord;
            //Convert to ui coord depending on the draw space
            switch (space)
            {
                case ECanvasDrawSpace.Screen:
                    {
                        //depth = 0 because we're in 2D, z coord is checked later
                        if (inputCamera is null)
                            return null;
                        uiCoord = inputCamera.NormalizedViewportToWorldCoordinate(normCoord, 0.0f).XY();
                        break;
                    }
                case ECanvasDrawSpace.Camera:
                    {
                        var camera = inputCamera ?? canvasTransform.CameraSpaceCamera ?? worldVP.ActiveCamera;
                        if (camera is null)
                            return null;
                        //Convert the normalized coord to world space using the draw distance
                        Vector3 worldCoord = camera.NormalizedViewportToWorldCoordinate(
                            normCoord,
                            XRMath.DistanceToDepth(
                                canvasTransform.CameraDrawSpaceDistance,
                                camera.NearZ,
                                camera.FarZ,
                                camera.IsReversedDepth));

                        //Transform the world coord to the canvas' local space
                        Matrix4x4 worldToLocal = canvasTransform.InverseWorldMatrix;
                        uiCoord = Vector3.Transform(worldCoord, worldToLocal).XY();
                        break;
                    }
                case ECanvasDrawSpace.World:
                    {
                        // Get the world segment from the normalized coord
                        // Transform the world segment to the canvas' local space
                        Segment localSegment = worldVP.GetWorldSegment(normCoord).TransformedBy(canvasTransform.InverseWorldMatrix);

                        // Check if the segment intersects the canvas' plane
                        if (GeoUtil.SegmentIntersectsPlane(
                            localSegment.Start,
                            localSegment.End,
                            XRMath.GetPlaneDistance(Vector3.Zero, Globals.Backward),
                            Globals.Backward,
                            out Vector3 localIntersectionPoint))
                        {
                            // Check if the point is within the canvas' bounds
                            var bounds = canvasTransform.GetActualBounds();
                            Vector2 point = localIntersectionPoint.XY();
                            uiCoord = bounds.Contains(point) ? point : null;
                        }
                        else
                            uiCoord = null;
                    }
                    break;
                default:
                    uiCoord = null;
                    break;
            }
            return uiCoord;
        }

        /// <summary>
        /// If any intersected element has BlocksInputBehind set, filters out elements
        /// that are not descendants of (or the same as) that blocking element.
        /// This prevents hover/input from reaching elements underneath dropdowns, popups, etc.
        /// </summary>
        private void FilterBlockedInput()
        {
            // Find the frontmost (deepest) blocker
            TransformBase? blockerTransform = null;
            int blockerDepth = -1;

            foreach (var item in UIElementIntersections)
            {
                if (item.Owner is not UIComponent ui)
                    continue;

                if (ui.Transform is UIBoundableTransform bt && bt.BlocksInputBehind)
                {
                    int depth = bt.Depth;
                    if (depth > blockerDepth)
                    {
                        blockerDepth = depth;
                        blockerTransform = bt;
                    }
                }
            }

            if (blockerTransform is null)
                return;

            // Remove items whose transform is not the blocker and not a descendant of the blocker
            var toRemove = new List<RenderInfo2D>();
            foreach (var item in UIElementIntersections)
                if (item.Owner is UIComponent ui && !IsDescendantOfOrSelf(ui.Transform, blockerTransform))
                    toRemove.Add(item);
            
            foreach (var item in toRemove)
                UIElementIntersections.Remove(item);
        }

        /// <summary>
        /// Returns true if <paramref name="candidate"/> is the same as <paramref name="ancestor"/>
        /// or is a descendant (child, grandchild, etc.) of <paramref name="ancestor"/>.
        /// </summary>
        private static bool IsDescendantOfOrSelf(TransformBase? candidate, TransformBase ancestor)
        {
            for (var t = candidate; t is not null; t = t.Parent)
            {
                if (ReferenceEquals(t, ancestor))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// This verifies the mouseover state of previous and current mouse intersections and swaps them.
        /// </summary>
        private void ValidateAndSwapIntersections()
        {
            LastUIElementIntersections.Union(UIElementIntersections).ForEach(ValidateIntersection);
            (LastUIElementIntersections, UIElementIntersections) = (UIElementIntersections, LastUIElementIntersections);
            UIElementIntersections.Clear();
        }

        private void OnGamepadInteractButtonDown()
        {
            FocusedComponent?.OnInteract();
        }
        private void OnGamepadInteractButtonUp()
        {

        }

        private void OnMouseInteractButtonDown()
        {
            FocusedComponent = TopMostInteractable;
            LeftClickDown?.Invoke(TopMostInteractable);

            if (FocusedComponent is not null && FocusedComponent.InteractOnButtonDown)
                FocusedComponent.OnInteract();
        }
        private void OnMouseInteractButtonUp()
        {
            if (FocusedComponent is not null && TopMostInteractable == FocusedComponent && !FocusedComponent.InteractOnButtonDown)
                OnInteract();
        }

        private void OnMouseRightButtonDown()
        {
            var target = TopMostInteractable;
            if (target is not null)
                RightClick?.Invoke(target);
        }

        // --- Modifier key tracking ---
        private bool _ctrlLeft, _ctrlRight;
        private bool _shiftLeft, _shiftRight;
        private bool _altLeft, _altRight;

        private void OnCtrlLeftStateChanged(bool pressed)  { _ctrlLeft = pressed;  IsCtrlHeld = _ctrlLeft || _ctrlRight; }
        private void OnCtrlRightStateChanged(bool pressed) { _ctrlRight = pressed; IsCtrlHeld = _ctrlLeft || _ctrlRight; }
        private void OnShiftLeftStateChanged(bool pressed)  { _shiftLeft = pressed;  IsShiftHeld = _shiftLeft || _shiftRight; }
        private void OnShiftRightStateChanged(bool pressed) { _shiftRight = pressed; IsShiftHeld = _shiftLeft || _shiftRight; }
        private void OnAltLeftStateChanged(bool pressed)  { _altLeft = pressed;  IsAltHeld = _altLeft || _altRight; }
        private void OnAltRightStateChanged(bool pressed) { _altRight = pressed; IsAltHeld = _altLeft || _altRight; }
        private void OnEscapeStateChanged(bool pressed)
        {
            if (pressed)
                EscapePressed?.Invoke();
        }
        protected virtual void OnGamepadBackButtonDown()
        {
            FocusedComponent?.OnBack();
        }
        protected virtual void OnGamepadBackButtonUp()
        {

        }

        protected virtual void OnLeftStickX(float value) { }
        protected virtual void OnLeftStickY(float value) { }

        /// <summary>
        /// Called on either left click or A button.
        /// Default behavior will OnClick the currently focused/highlighted UI component, if anything.
        /// </summary>
        protected virtual void OnInteract()
        {
            FocusedComponent?.OnInteract();
        }
        protected virtual void OnDPadUp()
        {

        }
        protected virtual void OnDPadDown()
        {

        }
        protected virtual void OnDPadLeft()
        {

        }
        protected virtual void OnDPadRight()
        {

        }

        private bool _subscribedToTimer;
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            GetCameraCanvas()?.CanvasTransform.InvalidateLayout();
            if (!_subscribedToTimer)
            {
                Engine.Time.Timer.CollectVisible += CollectVisible;
                Engine.Time.Timer.SwapBuffers += SwapBuffers;
                _subscribedToTimer = true;
            }
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            if (_subscribedToTimer)
            {
                Engine.Time.Timer.CollectVisible -= CollectVisible;
                Engine.Time.Timer.SwapBuffers -= SwapBuffers;
                _subscribedToTimer = false;
            }
        }

        protected void OnChildAdded(UIComponent child)
        {
            //child.OwningActor = this;
        }

        //public void Render()
        //{
        //    _scene.DoRender(AbstractRenderer.CurrentCamera, null);
        //}

        [Browsable(false)]
        public bool IsLayoutInvalidated { get; private set; }
        public void InvalidateLayout() => IsLayoutInvalidated = true;

        /// <summary>
        /// This is the topmost UI component that the mouse is directly over.
        /// Typically, this will be the component that will receive input, like a button.
        /// Backgrounds and other non-interactive components will still be intersected with, but they will not be considered the topmost interactable.
        /// </summary>
        public UIInteractableComponent? TopMostInteractable { get; private set; }
        public UIComponent? TopMostElement { get; private set; }

        private SortedSet<RenderInfo2D> LastUIElementIntersections = new(new Comparer());
        private SortedSet<RenderInfo2D> UIElementIntersections = new(new Comparer());

        protected static bool UIElementPredicate(RenderInfo2D item)
            => item.Owner is UIComponent ui && ui.UITransform.IsVisibleInHierarchy;
        private void ValidateIntersection(RenderInfo2D item)
        {
            if (item.Owner is not UIInteractableComponent inter)
                return;

            //Quick fix: sortedset uses a comparer to sort, but the comparer doesn't check for equality, resulting in invalid contains checks.
            var last = LastUIElementIntersections.ToArray();
            var curr = UIElementIntersections.ToArray();

            if (last.Contains(item))
            {
                //Mouse was over this renderable last update
                if (!curr.Contains(item))
                {
                    //Lost mouse over
                    inter.IsMouseOver = false;
                    inter.IsMouseDirectlyOver = false;
                }
                else
                {
                    //Had mouse over and still does now — re-evaluate direct-over in case TopMost changed
                    bool shouldBeDirectlyOver = inter == TopMostInteractable;
                    if (inter.IsMouseDirectlyOver != shouldBeDirectlyOver)
                        inter.IsMouseDirectlyOver = shouldBeDirectlyOver;

                    var uiTransform = inter.UITransform;
                    if (inter.NeedsMouseMove)
                        inter.MouseMoved(
                            uiTransform.CanvasToLocal(LastCursorPositionWorld2D),
                            uiTransform.CanvasToLocal(CursorPositionWorld2D));
                }
            }
            else if (curr.Contains(item)) //Mouse was not over this renderable last update
            {
                //Got mouse over
                inter.IsMouseOver = true;
                inter.IsMouseDirectlyOver = inter == TopMostInteractable;
            }
            else
            {
                //Not over this renderable
                inter.IsMouseOver = false;
                inter.IsMouseDirectlyOver = false;
            }
        }
        private class Comparer : IComparer<RenderInfo2D>, IComparer
        {
            public int Compare(RenderInfo2D? x, RenderInfo2D? y)
            {
                if (x is not RenderInfo2D left ||
                    y is not RenderInfo2D right)
                    return 0;

                if (ReferenceEquals(left, right))
                    return 0;

                if (left.LayerIndex > right.LayerIndex)
                    return -1;

                if (right.LayerIndex > left.LayerIndex)
                    return 1;

                if (left.IndexWithinLayer > right.IndexWithinLayer)
                    return -1;

                if (right.IndexWithinLayer > left.IndexWithinLayer)
                    return 1;

                // Tiebreaker: distinct RenderInfo2D instances must never compare as 0
                // because SortedSet treats 0 as "same item" and silently drops duplicates.
                return RuntimeHelpers.GetHashCode(left).CompareTo(RuntimeHelpers.GetHashCode(right));
            }
            public int Compare(object? x, object? y)
                => Compare(x as RenderInfo2D, y as RenderInfo2D);
        }
    }
}
