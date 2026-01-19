using Extensions;
using System.Collections;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.UI;

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
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, OnMouseInteractButtonDown);
            input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Released, OnMouseInteractButtonUp);

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
            => GetCameraCanvas()?.VisualScene2D?.RenderTree?.FindAllIntersectingSorted(CursorPositionWorld2D, UIElementIntersections, UIElementPredicate);

        private void SwapBuffers()
        {
            using var sample = Engine.Profiler.Start("UICanvasInputComponent.SwapBuffers");
            TopMostElement = UIElementIntersections.FirstOrDefault(x => x.Owner is UIComponent)?.Owner as UIComponent;
            TopMostInteractable = UIElementIntersections.FirstOrDefault(x => x.Owner is UIInteractableComponent)?.Owner as UIInteractableComponent;
            //if (TopMostInteractable is not null)
            //    Debug.Out($"Topmost interactable: {TopMostInteractable.Name}");
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
                            if (bounds.Contains(point))
                                uiCoord = point;
                            else
                                uiCoord = null;
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

            if (FocusedComponent is not null && FocusedComponent.InteractOnButtonDown)
                FocusedComponent.OnInteract();
        }
        private void OnMouseInteractButtonUp()
        {
            if (FocusedComponent is not null && TopMostInteractable == FocusedComponent && !FocusedComponent.InteractOnButtonDown)
                OnInteract();
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

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            GetCameraCanvas()?.CanvasTransform.InvalidateLayout();
            Engine.Time.Timer.CollectVisible += CollectVisible;
            Engine.Time.Timer.SwapBuffers += SwapBuffers;
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.Time.Timer.CollectVisible -= CollectVisible;
            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
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
                    //Had mouse over and still does now
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

                if (left.LayerIndex > right.LayerIndex)
                    return -1;

                if (right.LayerIndex > left.LayerIndex)
                    return 1;

                if (left.IndexWithinLayer > right.IndexWithinLayer)
                    return -1;

                if (right.IndexWithinLayer > left.IndexWithinLayer)
                    return 1;

                return 0;
            }
            public int Compare(object? x, object? y)
                => Compare(x as RenderInfo2D, y as RenderInfo2D);
        }
    }
}
