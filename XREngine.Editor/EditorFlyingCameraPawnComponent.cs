using Extensions;
using ImageMagick;
using System.Linq;
using System.Numerics;
using XREngine.Actors.Types;
using XREngine.Components;
using XREngine.Components.Scene.Transforms;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Input.Devices;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Picking;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public partial class EditorFlyingCameraPawnComponent : FlyingCameraPawnComponent, IRenderable
{
    public EditorFlyingCameraPawnComponent()
    {
        _postRenderRC = new((int)EDefaultRenderPass.PostRender, PostRender);
        _renderHighlightRC = new((int)EDefaultRenderPass.OpaqueForward, RenderHighlight);
        RenderedObjects =
        [
            RenderInfo3D.New(this, _postRenderRC),
            RenderInfo3D.New(this, _renderHighlightRC)
        ];
        Selection.SelectionChanged += Selection_SelectionChanged;
    }

    private void Selection_SelectionChanged(SceneNode[] selection)
    {
        if (selection.Length == 0)
        {
            TransformToolUndoAdapter.Attach(null);
            TransformTool3D.DestroyInstance();
        }
        else
        {
            var tool = TransformTool3D.GetInstance(selection[0].Transform);
            TransformToolUndoAdapter.Attach(tool);
        }
    }

    private Vector3? _worldDragPoint = null;
    /// <summary>
    /// The reference world position to drag the camera to when dragging or rotating the camera.
    /// </summary>
    public Vector3? WorldDragPoint
    {
        get => _worldDragPoint;
        private set => SetField(ref _worldDragPoint, value);
    }

    public Vector3? LastDepthHitNormalizedViewportPoint => _lastDepthHitNormalizedViewportPoint;
    /// <summary>
    /// The last hit point in normalized viewport coordinates from the depth buffer.
    /// </summary>
    public Vector3? DepthHitNormalizedViewportPoint
    {
        get => _depthHitNormalizedViewportPoint;
        private set
        {
            _lastDepthHitNormalizedViewportPoint = _depthHitNormalizedViewportPoint;
            _depthHitNormalizedViewportPoint = value;
        }
    }

    /// <summary>
    /// Converts DepthHitNormalizedViewportPoint's depth Z-value to a distance based on the camera's near and far planes.
    /// </summary>
    public float LastHitDistance => XRMath.DepthToDistance(DepthHitNormalizedViewportPoint.HasValue ? DepthHitNormalizedViewportPoint.Value.Z : 0.0f, NearZ, FarZ);

    /// <summary>
    /// The near Z distance of the camera's frustum.
    /// </summary>
    public float NearZ => GetCamera()?.Camera.NearZ ?? 0.0f;

    /// <summary>
    /// The far Z distance of the camera's frustum.
    /// </summary>
    public float FarZ => GetCamera()?.Camera.FarZ ?? 0.0f;

    private bool _renderWorldDragPoint = false;
    /// <summary>
    /// If true, renders a sphere to display the world drag position in the scene.
    /// </summary>
    public bool RenderWorldDragPoint
        {
        get => _renderWorldDragPoint;
        set => SetField(ref _renderWorldDragPoint, value);
    }

    private bool _renderFrustum = false;
    /// <summary>
    /// If true, renders this pawn's camera frustum in the scene.
    /// </summary>
    public bool RenderFrustum
    {
        get => _renderFrustum;
        set => SetField(ref _renderFrustum, value);
    }

    private bool _renderRaycast = true;
    /// <summary>
    /// If true, renders debug information for raycast hits.
    /// </summary>
    public bool RenderRaycast
    {
        get => _renderRaycast;
        set => SetField(ref _renderRaycast, value);
    }

    private PhysxScene.PhysxQueryFilter _physxQueryFilter = new();
    public PhysxScene.PhysxQueryFilter PhysxQueryFilter
    {
        get => _physxQueryFilter;
        set => SetField(ref _physxQueryFilter, value);
    }

    private LayerMask _layerMask = LayerMask.GetMask(DefaultLayers.Default);
    public LayerMask LayerMask
    {
        get => _layerMask;
        set => SetField(ref _layerMask, value);
    }

    private readonly SortedDictionary<float, List<(XRComponent? item, object? data)>> _lastPhysicsPickResults = [];
    /// <summary>
    /// The last physics pick results from the raycast, sorted by distance.
    /// Use RaycastLock to access this safely.
    /// </summary>
    public SortedDictionary<float, List<(RenderInfo3D item, object? data)>> LastOctreePickResults
        => _lastOctreePickResults;

    private readonly SortedDictionary<float, List<(RenderInfo3D item, object? data)>> _lastOctreePickResults = [];
    /// <summary>
    /// The last octree pick results from the raycast, sorted by distance.
    /// Use RaycastLock to access this safely.
    /// </summary>
    public SortedDictionary<float, List<(XRComponent? item, object? data)>> LastPhysicsPickResults
        => _lastPhysicsPickResults;

    private List<SceneNode>? _lastHits = null;
    private int _lastHitIndex = 0;
    private Triangle? _hitTriangle = null;
    private Vector3? _meshHitPoint = null;
    private Segment _lastRaycastSegment = new(Vector3.Zero, Vector3.Zero);
    private Vector3? _depthHitNormalizedViewportPoint = null;
    private Vector3? _lastDepthHitNormalizedViewportPoint = null;

    private readonly Lock _raycastLock = new();
    /// <summary>
    /// The lock used to safely access the raycast results.
    /// </summary>
    public Lock RaycastLock => _raycastLock;

    private bool _depthQueryRequested = false;

    private bool _allowWorldPicking = true;
    /// <summary>
    /// If true, raycasts will be performed to pick objects in the world.
    /// </summary>
    public bool AllowWorldPicking
    {
        get => _allowWorldPicking;
        set => SetField(ref _allowWorldPicking, value);
    }

    private readonly RenderCommandMethod3D _postRenderRC;
    private readonly RenderCommandMethod3D _renderHighlightRC;

    public RenderInfo[] RenderedObjects { get; }

    private void RenderHighlight()
    {
        if (Engine.Rendering.State.IsShadowPass)
            return;

        if (_hitTriangle is not null)
            Engine.Rendering.Debug.RenderTriangle(_hitTriangle.Value, ColorF4.Yellow, true);
        if (_meshHitPoint is Vector3 meshHit)
            Engine.Rendering.Debug.RenderPoint(meshHit, ColorF4.Yellow);
        
        if (RenderWorldDragPoint && (WorldDragPoint.HasValue || DepthHitNormalizedViewportPoint.HasValue) && Viewport is not null)
        {
            Vector3 pos;
            if (WorldDragPoint.HasValue)
                pos = WorldDragPoint.Value;
            else
                pos = Viewport.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint!.Value);
            Engine.Rendering.Debug.RenderSphere(pos, (Viewport.Camera?.DistanceFromWorldPosition(pos) ?? 1.0f) * 0.05f, false, ColorF4.Yellow);
        }

        if (RenderFrustum)
        {
            var cam = GetCamera();
            if (cam is not null)
                Engine.Rendering.Debug.RenderFrustum(cam.Camera.WorldFrustum(), ColorF4.Red);
        }
    }

    static void ScreenshotCallback(MagickImage img, int index)
    {
        var path = GetScreenshotPath();
        Utility.EnsureDirPathExists(path);
        img?.Flip();
        img?.Write(path);
    }

    private static string GetScreenshotPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), GetGameName(), GetDay(), $"{GetTime()}.png");

    private static string GetGameName()
    {
        string? name = Engine.GameSettings.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = "XREngine";
        return name;
    }
    private static string GetDay()
    {
        DateTime now = DateTime.Now;
        return $"{now.Year}-{now.Month}-{now.Day}";
    }
    private static string GetTime()
    {
        DateTime now = DateTime.Now;
        return $"{now.Hour}-{now.Minute}-{now.Second}";
    }

    protected override void Tick()
    {
        base.Tick();
        ApplyInput(Viewport);
    }

    private void PostRender()
    {
        var vp = Viewport;
        if (vp is null)
            return;

        if (_wantsScreenshot)
        {
            _wantsScreenshot = false;
            //rend.GetScreenshotAsync(vp.Region, false, ScreenshotCallback);

            var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
            if (pipeline is not null)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string capturePath = Path.Combine(desktop, $"{pipeline.GetType().Name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
                Engine.Rendering.State.CurrentRenderingPipeline?.CaptureAllTextures(capturePath);
            }
        }

        if (NeedsDepthHit())
            GetDepthHit(vp, GetCursorInternalCoordinatePosition(vp));
    }

    private void ApplyInput(XRViewport? vp)
    {
        if (vp is null)
            return;

        var cam = GetCamera();
        if (cam is null)
            return;

        var p = GetNormalizedCursorPosition(vp);
        _lastRaycastSegment = vp.GetWorldSegment(p);

        SceneNode? tfmTool = TransformTool3D.InstanceNode;
        if (tfmTool is not null && tfmTool.TryGetComponent<TransformTool3D>(out var comp))
            comp?.MouseMove(_lastRaycastSegment, cam.Camera, LeftClickPressed);

        if (AllowWorldPicking && !IsHoveringUI())
        {
            using (_raycastLock.EnterScope())
            {
                _lastOctreePickResults.Clear();
                _lastPhysicsPickResults.Clear();
                vp.PickSceneAsync(p, false, true, true, _layerMask, _physxQueryFilter, _lastOctreePickResults, _lastPhysicsPickResults, OctreeRaycastCallback, PhysicsRaycastCallback);
            }
        }

        ApplyTransformations(vp);
    }

    private void PhysicsRaycastCallback(SortedDictionary<float, List<(XRComponent? item, object? data)>>? dictionary)
    {
        if (!RenderRaycast)
            return;

        //foreach (var x in _lastPhysicsPickResults.Values)
        //    foreach (var (c2, _) in x)
        //        if (c2?.SceneNode is not null)
        //            Engine.Rendering.Debug.RenderLine(_lastRaycastSegment.Start, _lastRaycastSegment.End, ColorF4.Green);
    }

    private void OctreeRaycastCallback(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dictionary)
    {
        UpdateMeshHitVisualization();

        if (!RenderRaycast)
            return;

        if (_lastOctreePickResults.Count > 0)
            RenderRaycastResult(_lastOctreePickResults.First());
    }

    private void UpdateMeshHitVisualization()
    {
        _hitTriangle = null;
        _meshHitPoint = null;

        if (_lastOctreePickResults.Count == 0)
            return;

        foreach ((RenderInfo3D _, object? data) in _lastOctreePickResults.First().Value)
        {
            switch (data)
            {
                case MeshPickResult meshHit:
                    _hitTriangle = meshHit.WorldTriangle;
                    _meshHitPoint = meshHit.HitPoint;
                    return;
                case Vector3 point:
                    _meshHitPoint = point;
                    return;
                case Triangle triangle:
                    _hitTriangle = triangle;
                    return;
            }
        }
    }

    private static void RenderRaycastResult(KeyValuePair<float, List<(RenderInfo3D item, object? data)>> result)
    {
        if (result.Value is null || result.Value.Count == 0)
            return;

        foreach ((RenderInfo3D info, object? data) in result.Value)
        {
            Vector3? point = data switch
            {
                Vector3 p => p,
                MeshPickResult meshHit => meshHit.HitPoint,
                _ => null
            };

            if (point is null)
                continue;

            string? name = info.Owner switch
            {
                XRComponent component when component.SceneNode?.Name is string nodeName => nodeName,
                TransformBase transform when transform.Name is not null => transform.Name,
                _ => null
            };

            if (name is not null)
                Engine.Rendering.Debug.RenderText(point.Value, name, ColorF4.Black);
            Engine.Rendering.Debug.RenderPoint(point.Value, ColorF4.Red);
        }
    }

    private Segment GetWorldSegment(XRViewport vp)
        => vp.GetWorldSegment(GetNormalizedCursorPosition(vp));

    private Vector2 GetNormalizedCursorPosition(XRViewport vp)
        => vp.NormalizeInternalCoordinate(GetCursorInternalCoordinatePosition(vp));

    private Vector2 GetCursorInternalCoordinatePosition(XRViewport vp)
    {
        Vector2 p = Vector2.Zero;

        var input = LocalInput;
        if (input is null)
            return p;

        var pos = input?.Mouse?.CursorPosition;
        if (pos is null)
            return p;

        p = pos.Value;
        p.Y = vp.Height - p.Y;
        p = vp.ScreenToViewportCoordinate(p);
        p = vp.ViewportToInternalCoordinate(p);

        return p;
    }

    private void GetDepthHit(XRViewport vp, Vector2 p)
    {
        float? depth = GetDepth(vp, p);
        p = vp.NormalizeInternalCoordinate(p);
        bool validDepth = depth is not null && depth.Value > 0.0f && depth.Value < 1.0f;
        if (validDepth)
        {
            DepthHitNormalizedViewportPoint = new Vector3(p.X, p.Y, depth!.Value);
            WorldDragPoint = Viewport?.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint!.Value);
        }
        else
        {
            DepthHitNormalizedViewportPoint = null;
            WorldDragPoint = null;
        }
        _depthQueryRequested = false;
    }

    private static float? GetDepth(XRViewport vp, Vector2 internalSizeCoordinate)
    {
        //TODO: severe framerate drop using synchronous depth read - async pbo is better but needs to be optimized
        var fbo = vp.RenderPipelineInstance?.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.ForwardPassFBOName);
        if (fbo is null)
            return null;

        float? depth = vp.GetDepth(fbo, (IVector2)internalSizeCoordinate);
        //Debug.Out($"Depth: {depth}");
        return depth;
    }

    private bool NeedsDepthHit()
        => _depthQueryRequested;

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(RightClickPressed):
                if (_rightClickPressed && !IsHoveringUI())
                    _depthQueryRequested = true;
                break;
        }
    }

    private void ApplyTransformations(XRViewport vp)
    {
        var tfm = TransformAs<Transform>();
        if (tfm is null)
            return;

        var scroll = _lastScrollDelta;
        _lastScrollDelta = null;

        var trans = _lastMouseTranslationDelta;
        _lastMouseTranslationDelta = null;

        var rot = _lastRotateDelta;
        _lastRotateDelta = null;

        if (scroll.HasValue)
        {
            //Zoom towards the hit point
            float scrollSpeed = scroll.Value;
            if (DepthHitNormalizedViewportPoint.HasValue)
            {
                if (ShiftPressed)
                    scrollSpeed *= ShiftSpeedModifier;
                Vector3 worldCoord = vp.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint.Value);
                float dist = Transform.WorldTranslation.Distance(worldCoord);
                tfm.Translation = Segment.PointAtLineDistance(Transform.WorldTranslation, worldCoord, scrollSpeed * dist * 0.1f * ScrollSpeed);
            }
            else
                base.OnScrolled(scrollSpeed);
        }
        if (trans.HasValue && WorldDragPoint.HasValue && DepthHitNormalizedViewportPoint.HasValue)
        {
            Vector3 normCoord = DepthHitNormalizedViewportPoint.Value;
            Vector3 worldCoord = vp.NormalizedViewportToWorldCoordinate(normCoord);
            Vector2 screenCoord = vp.DenormalizeViewportCoordinate(normCoord.XY());
            Vector2 newScreenCoord = screenCoord + trans.Value;
            Vector3 newNormCoord = new(vp.NormalizeViewportCoordinate(newScreenCoord), normCoord.Z);
            Vector3 worldDelta = vp.NormalizedViewportToWorldCoordinate(newNormCoord) - worldCoord;
            tfm.ApplyTranslation(worldDelta);
        }
        if (rot.HasValue)
        {
            var pos = _arcballRotationPosition;
            if (pos.HasValue)
            {
                float x = rot.Value.X;
                float y = rot.Value.Y;
                ArcBallRotate(y, x, pos.Value);
            }
        }
    }

    protected override void OnRightClick(bool pressed)
    {
        base.OnRightClick(pressed);

        if (pressed && !IsHoveringUI())
        {
            if (GetAverageSelectionPoint(out Vector3 avgPoint))
                _arcballRotationPosition = avgPoint;
            else if (WorldDragPoint.HasValue)
                _arcballRotationPosition = WorldDragPoint;
            else
                _arcballRotationPosition = null;
        }
        else
            _arcballRotationPosition = null;
    }

    private Vector2? _lastRotateDelta = null;
    private Vector3? _arcballRotationPosition = null;
    protected override void MouseRotate(float x, float y)
    {
        if (_arcballRotationPosition is not null)
            _lastRotateDelta = new Vector2(-x * MouseRotateSpeed, y * MouseRotateSpeed);
        else
            base.MouseRotate(x, y);
    }

    private bool GetAverageSelectionPoint(out Vector3 avgPoint)
    {
        if (Selection.SceneNodes.Length == 0)
        {
            avgPoint = Vector3.Zero;
            return false;
        }

        avgPoint = GetAverageSelectionPoint();

        //Determine if the point is on screen
        if (!(GetCamera()?.Camera?.WorldFrustum().ContainsPoint(avgPoint) ?? false))
        {
            avgPoint = Vector3.Zero;
            return false;
        }

        return true;
    }
    private static Vector3 GetAverageSelectionPoint()
    {
        Vector3 avgPoint = Vector3.Zero;
        foreach (var node in Selection.SceneNodes)
            avgPoint += node.Transform.WorldTranslation;
        avgPoint /= Selection.SceneNodes.Length;
        return avgPoint;
    }

    private Vector2? _lastMouseTranslationDelta = null;
    protected override void MouseTranslate(float x, float y)
    {
        if (WorldDragPoint.HasValue)
        {
            //This fixes stationary jitter caused by float imprecision
            //when recalculating the same hit point every update
            if (Math.Abs(x) < 0.00001f &&
                Math.Abs(y) < 0.00001f)
                return;

            _lastMouseTranslationDelta = new Vector2(-x, -y);
            //_queryDepth = true;
        }
        else
            base.MouseTranslate(x, y);
    }

    private float? _lastScrollDelta = null;
    protected override void OnScrolled(float diff)
    {
        if (IsHoveringUI())
            return;

        _lastScrollDelta = diff;
        _depthQueryRequested = true;
    }

    private bool _wantsScreenshot = false;

    /// <summary>
    /// Takes a screenshot of the current viewport and saves it to the desktop.
    /// </summary>
    public void TakeScreenshot()
        => _wantsScreenshot = true;

    public override void RegisterInput(InputInterface input)
    {
        base.RegisterInput(input);

        input.RegisterMouseButtonEvent(EMouseButton.LeftClick, EButtonInputType.Pressed, Select);
        input.RegisterKeyEvent(EKey.F12, EButtonInputType.Pressed, TakeScreenshot);

        input.RegisterKeyEvent(EKey.Number1, EButtonInputType.Pressed, SetTransformTranslation);
        input.RegisterKeyEvent(EKey.Number2, EButtonInputType.Pressed, SetTransformRotation);
        input.RegisterKeyEvent(EKey.Number3, EButtonInputType.Pressed, SetTransformScale);

        input.RegisterKeyEvent(EKey.F1, EButtonInputType.Pressed, SetTransformModeWorld);
        input.RegisterKeyEvent(EKey.F2, EButtonInputType.Pressed, SetTransformModeLocal);
        input.RegisterKeyEvent(EKey.F3, EButtonInputType.Pressed, SetTransformModeParent);
        input.RegisterKeyEvent(EKey.F4, EButtonInputType.Pressed, SetTransformModeScreen);
    }

    private void SetTransformModeParent() => TransformTool3D.TransformSpace = ETransformSpace.Parent;
    private void SetTransformModeScreen() => TransformTool3D.TransformSpace = ETransformSpace.Screen;
    private void SetTransformModeLocal() => TransformTool3D.TransformSpace = ETransformSpace.Local;
    private void SetTransformModeWorld() => TransformTool3D.TransformSpace = ETransformSpace.World;
    private void SetTransformScale() => TransformTool3D.TransformMode = ETransformMode.Scale;
    private void SetTransformRotation() => TransformTool3D.TransformMode = ETransformMode.Rotate;
    private void SetTransformTranslation() => TransformTool3D.TransformMode = ETransformMode.Translate;

    /// <summary>
    /// Selects a node to transform based on the last raycast results.
    /// </summary>
    private void Select()
    {
        //Don't select new nodes while the transform tool is active and highlighted
        if (TransformTool3D.GetActiveInstance(out var tfmComp) && tfmComp is not null && tfmComp.Highlighted || IsHoveringUI())
            return;

        //Collect new hits from the last raycast results
        List<SceneNode> currentHits = [];
        CollectHits(currentHits);

        if (_lastHits is null)
            SetSelection(currentHits); //if we have no last hits, just set the selection to the current hits
        else //otherwise, compare the current hits against the last hits and use modifier keys to update the selection
            UpdateSelection(LocalInput, CompareAgainstLastHits(currentHits, ref _lastHits, ref _lastHitIndex));
    }

    /// <summary>
    /// Sets the selection to the first node in the current hits list and updates the last hits and index.
    /// </summary>
    /// <param name="currentHits"></param>
    private void SetSelection(List<SceneNode> currentHits)
    {
        _lastHits = currentHits;
        _lastHitIndex = 0;
        Selection.SceneNodes = currentHits.Count >= 1 ? [currentHits[0]] : [];
    }

    /// <summary>
    /// Determines which node to select based on the current hits and the last hits.
    /// If the nodes are the same as the last hits, it cycles through them.
    /// Otherwise, it updates the last hits to the current hits and selects the first node.
    /// </summary>
    /// <param name="currentHits"></param>
    /// <param name="lastHits"></param>
    /// <param name="lastHitIndex"></param>
    /// <returns></returns>
    private static SceneNode? CompareAgainstLastHits(List<SceneNode> currentHits, ref List<SceneNode> lastHits, ref int lastHitIndex)
    {
        //intersect with the last hit values to see if we are still hitting the same thing
        bool sameNodes = currentHits.Count > 0 && currentHits.Intersect(lastHits).Count() == currentHits.Count;

        SceneNode? node;
        if (sameNodes)
        {
            //cycle the selection
            lastHitIndex = (lastHitIndex + 1) % currentHits.Count;
            node = currentHits[lastHitIndex];
        }
        else
        {
            lastHits = currentHits;
            lastHitIndex = 0;
            node = currentHits.Count > 0 ? currentHits[lastHitIndex] : null;
        }

        return node;
    }

    /// <summary>
    /// Collects the hits from the last raycast results into the current hits list.
    /// </summary>
    /// <param name="currentHits"></param>
    private void CollectHits(List<SceneNode> currentHits)
    {
        using var scope = _raycastLock.EnterScope();
        
        foreach (var x in _lastPhysicsPickResults.Values)
            foreach (var (comp, _) in x)
                if (comp?.SceneNode is not null)
                    currentHits.Add(comp.SceneNode);

        foreach (var x in _lastOctreePickResults.Values)
            foreach (var (info, _) in x)
                if (info.Owner is XRComponent comp && comp.SceneNode is not null)
                    currentHits.Add(comp.SceneNode);
                else if (info.Owner is TransformBase tfm && tfm.SceneNode is not null)
                    currentHits.Add(tfm.SceneNode);
    }

    /// <summary>
    /// Uses modifier keys to set, add, remove, or toggle selection of a node.
    /// </summary>
    /// <param name="input"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    private static void UpdateSelection(LocalInputInterface? input, SceneNode? node)
    {
        if (node is null)
        {
            Selection.SceneNodes = [];
            return;
        }

        var kbd = input?.Keyboard;
        if (kbd is not null)
        {
            //control is toggle, alt is remove, shift is add

            if (kbd.GetKeyState(EKey.ControlLeft, EButtonInputType.Pressed) ||
                kbd.GetKeyState(EKey.ControlRight, EButtonInputType.Pressed))
            {
                if (Selection.SceneNodes.Contains(node))
                    RemoveNode(node);
                else
                    AddNode(node);

                return;
            }
            else if (kbd.GetKeyState(EKey.AltLeft, EButtonInputType.Pressed) ||
                     kbd.GetKeyState(EKey.AltRight, EButtonInputType.Pressed))
            {
                RemoveNode(node);
                return;
            }
            else if (kbd.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed) ||
                     kbd.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed))
            {
                AddNode(node);
                return;
            }
        }

        Selection.SceneNodes = [node];
    }

    private static void AddNode(SceneNode node)
    {
        if (Selection.SceneNodes.Contains(node))
            return;

        Selection.SceneNodes = [.. Selection.SceneNodes, node];
    }

    private static void RemoveNode(SceneNode node)
    {
        if (!Selection.SceneNodes.Contains(node))
            return;

        Selection.SceneNodes = [.. Selection.SceneNodes.Where(n => n != node)];
    }
}
