using System.Drawing;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Input.Devices;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Represents a UI transform in 2D space.
    /// </summary>
    public class UITransform : TransformBase, IRenderable
    {
        private UICanvasTransform? _parentCanvas;
        public UICanvasTransform? ParentCanvas
        {
            get => _parentCanvas;
            set => SetField(ref _parentCanvas, value);
        }

        public UICanvasTransform? GetCanvasTransform()
            => ParentCanvas ?? this as UICanvasTransform;
        public UICanvasComponent? GetCanvasComponent()
            => GetCanvasTransform()?.SceneNode?.GetComponent<UICanvasComponent>();

        private string _stylingClass = string.Empty;
        /// <summary>
        /// The CSS class that this UI component uses for styling.
        /// </summary>
        public string StylingClass
        {
            get => _stylingClass;
            set => SetField(ref _stylingClass, value);
        }

        private string _stylingID = string.Empty;
        /// <summary>
        /// The CSS ID that this UI component uses for styling.
        /// </summary>
        public string StylingID
        {
            get => _stylingID;
            set => SetField(ref _stylingID, value);
        }

        protected Vector2 _translation = Vector2.Zero;
        public virtual Vector2 Translation
        {
            get => _translation;
            set => SetField(ref _translation, value);
        }

        protected Vector2 _actualLocalBottomLeftTranslation = new();
        /// <summary>
        /// This is the translation after being potentially modified by the parent's placement info.
        /// </summary>
        public Vector2 ActualLocalBottomLeftTranslation
        {
            get => _actualLocalBottomLeftTranslation;
            set => SetField(ref _actualLocalBottomLeftTranslation, value);
        }

        protected float _z = 0.0f;
        public virtual float DepthTranslation
        {
            get => _z;
            set => SetField(ref _z, value);
        }

        protected Vector3 _scale = Vector3.One;
        public virtual Vector3 Scale
        {
            get => _scale;
            set => SetField(ref _scale, value);
        }

        protected float _rotationRadians = 0.0f;
        public float RotationRadians
        {
            get => _rotationRadians;
            set => SetField(ref _rotationRadians, value);
        }
        public float RotationDegrees
        {
            get => XRMath.RadToDeg(RotationRadians);
            set => RotationRadians = XRMath.DegToRad(value);
        }

        public RenderInfo2D DebugRenderInfo2D { get; private set; }

        public UITransform() : this(null) { }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        public UITransform(TransformBase? parent) : base(parent)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
        {
            Children.PostAnythingAdded += OnChildAdded;
            Children.PostAnythingRemoved += OnChildRemoved;
        }
        ~UITransform()
        {
            Children.PostAnythingAdded -= OnChildAdded;
            Children.PostAnythingRemoved -= OnChildRemoved;
        }

        private RenderCommandMethod2D _debugRC;
        public RenderCommandMethod2D DebugRenderCommand => _debugRC;

        protected override RenderInfo[] GetDebugRenderInfo()
            => [DebugRenderInfo2D = RenderInfo2D.New(this, _debugRC = new RenderCommandMethod2D((int)EDefaultRenderPass.OnTopForward, RenderDebug))];

        protected override Matrix4x4 CreateLocalMatrix() => 
            Matrix4x4.CreateScale(Scale) * 
            Matrix4x4.CreateFromAxisAngle(Globals.Backward, RotationRadians) *
            Matrix4x4.CreateTranslation(new Vector3(Translation, DepthTranslation));

        /// <summary>
        /// Scale and translate in/out to/from a specific point.
        /// </summary>
        /// <param name="delta"></param>
        /// <param name="worldScreenPoint"></param>
        /// <param name="minScale"></param>
        /// <param name="maxScale"></param>
        public void Zoom(float delta, Vector2 worldScreenPoint, Vector2? minScale, Vector2? maxScale)
        {
            if (Math.Abs(delta) < 0.0001f)
                return;

            Vector2 scale = new(_scale.X, _scale.Y);
            Vector2 newScale = scale - new Vector2(delta);

            if (minScale != null)
            {
                if (newScale.X < minScale.Value.X)
                    newScale.X = minScale.Value.X;

                if (newScale.Y < minScale.Value.Y)
                    newScale.Y = minScale.Value.Y;
            }

            if (maxScale != null)
            {
                if (newScale.X > maxScale.Value.X)
                    newScale.X = maxScale.Value.X;

                if (newScale.Y > maxScale.Value.Y)
                    newScale.Y = maxScale.Value.Y;
            }

            if (Vector2.Distance(scale, newScale) < 0.0001f)
                return;
            
            Translation += (worldScreenPoint - new Vector2(WorldTranslation.X, WorldTranslation.Y)) * Vector2.One / scale * delta;
            Scale = new Vector3(newScale, Scale.Z);
        }

        public event Action<UITransform>? LayoutInvalidated;
        protected void OnLayoutInvalidated()
            => LayoutInvalidated?.Invoke(this);
        public virtual void InvalidateLayout()
        {
            if (ParentCanvas != null && ParentCanvas != this)
                ParentCanvas.InvalidateLayout();
            MarkLocalModified();
            if (Parent is UIBoundableTransform parent && parent.UsesAutoSizing)
                parent.InvalidateLayout();
            OnLayoutInvalidated();
        }

        /// <summary>
        /// Fits the layout of this UI transform to the parent region.
        /// </summary>
        /// <param name="parentRegion"></param>
        public virtual void FitLayout(BoundingRectangleF parentRegion)
        {

        }

        public bool IsVisible => Visibility == EVisibility.Visible;
        public bool IsHidden => Visibility == EVisibility.Hidden;
        public bool IsCollapsed => Visibility == EVisibility.Collapsed;

        public void Show() => Visibility = EVisibility.Visible;
        public void Hide() => Visibility = EVisibility.Hidden;
        public void Collapse() => Visibility = EVisibility.Collapsed;

        protected EVisibility _visibility = EVisibility.Visible;
        public virtual EVisibility Visibility
        {
            get => _visibility;
            set => SetField(ref _visibility, value);
        }

        public bool IsVisibleInHierarchy => IsVisible && (Parent is not UITransform tfm || tfm.IsVisibleInHierarchy);

        private UIChildPlacementInfo? _placementInfo = null;
        /// <summary>
        /// Dictates how this UI component is arranged within the parent transform's bounds.
        /// </summary>
        public UIChildPlacementInfo? PlacementInfo
        {
            get
            {
                Parent?.VerifyPlacementInfo(this, ref _placementInfo);
                return _placementInfo;
            }
            set => _placementInfo = value;
        }

        /// <summary>
        /// Recursively registers (or unregisters) inputs on this and all child UI components.
        /// </summary>
        /// <param name="input"></param>
        internal protected virtual void RegisterInputs(InputInterface input)
        {
            //try
            //{
            //    foreach (ISceneComponent comp in ChildComponents)
            //        if (comp is IUIComponent uiComp)
            //            uiComp.RegisterInputs(input);
            //}
            //catch (Exception ex) 
            //{
            //    Engine.LogException(ex);
            //}
        }
        //protected internal override void Start()
        //{
        //    if (this is IRenderable r)
        //        OwningUserInterface?.AddRenderableComponent(r);
        //}
        //protected internal override void Stop()
        //{
        //    if (this is IRenderable r)
        //        OwningUserInterface?.RemoveRenderableComponent(r);
        //}

        protected virtual void OnResizeChildComponents(BoundingRectangleF parentRegion)
        {
            try
            {
               // lock (Children)
                {
                    foreach (var c in Children)
                        if (c is UITransform uiTfm)
                            uiTfm.FitLayout(parentRegion);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                //_childLocker.ExitReadLock();
            }
        }

        /// <summary>
        /// Converts a local-space coordinate of a parent UI component 
        /// to a local-space coordinate of a child UI component.
        /// </summary>
        /// <param name="coordinate">The coordinate relative to the parent UI component.</param>
        /// <param name="parent">The parent UI component whose space the coordinate is already in.</param>
        /// <param name="targetChild">The UI component whose space you wish to convert the coordinate to.</param>
        /// <returns></returns>
        public static Vector2 ConvertUICoordinate(Vector2 coordinate, UITransform parent, UITransform targetChild)
            => Vector2.Transform(coordinate, targetChild.InverseWorldMatrix * parent.WorldMatrix);
        /// <summary>
        /// Converts a screen-space coordinate
        /// to a local-space coordinate of a UI component.
        /// </summary>
        /// <param name="coordinate">The coordinate relative to the screen / origin of the root UI component.</param>
        /// <param name="uiComp">The UI component whose space you wish to convert the coordinate to.</param>
        /// <param name="delta">If true, the coordinate and returned value are treated like a vector offset instead of an absolute point.</param>
        /// <returns></returns>
        public Vector2 ScreenToLocal(Vector2 coordinate)
            => Vector2.Transform(coordinate, ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
        public Vector3 ScreenToLocal(Vector3 coordinate)
            => Vector3.Transform(coordinate, ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
        public Vector3 LocalToScreen(Vector3 coordinate)
            => Vector3.Transform(coordinate, ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);
        public Vector2 LocalToScreen(Vector2 coordinate)
            => Vector2.Transform(coordinate, ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);

        public virtual float GetMaxChildWidth() => 0.0f;
        public virtual float GetMaxChildHeight() => 0.0f;

        public virtual bool Contains(Vector2 worldPoint)
        {
            var worldTranslation = WorldTranslation;
            return Vector2.Distance(worldPoint, new Vector2(worldTranslation.X, worldTranslation.Y)) < 0.0001f;
        }
        public virtual Vector2 ClosestPoint(Vector2 worldPoint)
        {
            var worldTranslation = WorldTranslation;
            return new Vector2(worldTranslation.X, worldTranslation.Y);
        }

        protected virtual void OnChildAdded(TransformBase item)
        {
            //if (item is IRenderable c && c.RenderedObjects is RenderInfo2D r2D)
            //{
            //    r2D.LayerIndex = RenderInfo2D.LayerIndex;
            //    r2D.IndexWithinLayer = RenderInfo2D.IndexWithinLayer + 1;
            //}

            if (item is UITransform uic)
                uic.InvalidateLayout();
        }
        protected virtual void OnChildRemoved(TransformBase item)
        {

        }

        protected virtual void OnResizeActual(BoundingRectangleF parentBounds)
        {
            ActualLocalBottomLeftTranslation = Translation;
        }

        public override byte[] EncodeToBytes(bool delta)
        {
            return [];
        }

        public override void DecodeFromBytes(byte[] arr)
        {

        }

        //protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        //{
        //    bool change = base.OnPropertyChanging(propName, field, @new);
        //    if (change)
        //    {
        //        switch (propName)
        //        {
                    
        //        }
        //    }
        //    return change;
        //}
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Translation):
                case nameof(DepthTranslation):
                case nameof(Scale):
                    InvalidateLayout();
                    break;
                case nameof(Visibility):
                    InvalidateLayout();
                    break;
                case nameof(ParentCanvas):
                    if (this is IRenderable r)
                        foreach (var rc in r.RenderedObjects)
                            rc.UserInterfaceCanvas = ParentCanvas?.SceneNode?.GetComponent<UICanvasComponent>();
                    lock (Children)
                    {
                        foreach (var child in Children)
                            if (child is UITransform uiTransform)
                                uiTransform.ParentCanvas = ParentCanvas;
                    }
                    InvalidateLayout();
                    break;
                case nameof(PlacementInfo):
                    InvalidateLayout();
                    break;
                case nameof(Parent):
                    ParentCanvas = Parent switch
                    {
                        UICanvasTransform uiCanvas => uiCanvas,
                        UITransform uiTfm => uiTfm.ParentCanvas,
                        _ => null,
                    };
                    InvalidateLayout();
                    break;
            }
        }

        protected override void RenderDebug()
        {
            base.RenderDebug();

            if (!Engine.Rendering.Settings.RenderUITransformCoordinate || Engine.Rendering.State.IsShadowPass)
                return;
            
            Vector3 endPoint = RenderTranslation + Engine.Rendering.Debug.UIPositionBias;
            Vector3 up = RenderUp * 50.0f;
            Vector3 right = RenderRight * 50.0f;

            Engine.Rendering.Debug.RenderLine(endPoint, endPoint + up, Color.Green);
            Engine.Rendering.Debug.RenderLine(endPoint, endPoint + right, Color.Red);
        }

        /// <summary>
        /// Converts a canvas-space coordinate to a local-space coordinate of this UI component.
        /// </summary>
        /// <param name="canvasPoint"></param>
        /// <returns></returns>
        public Vector2 CanvasToLocal(Vector2 canvasPoint)
        {
            Matrix4x4 canvasToLocal = InverseWorldMatrix * (ParentCanvas?.WorldMatrix ?? Matrix4x4.Identity);
            return Vector2.Transform(canvasPoint, canvasToLocal);
        }
        /// <summary>
        /// Converts a local-space coordinate of this UI component to a canvas-space coordinate.
        /// </summary>
        /// <param name="localPoint"></param>
        /// <returns></returns>
        public Vector2 LocalToCanvas(Vector2 localPoint)
        {
            Matrix4x4 localToCanvas = WorldMatrix * (ParentCanvas?.InverseWorldMatrix ?? Matrix4x4.Identity);
            return Vector2.Transform(localPoint, localToCanvas);
        }
    }
}
