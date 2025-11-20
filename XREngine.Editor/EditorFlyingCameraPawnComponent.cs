using Extensions;
using ImageMagick;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Actors.Types;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Components.Scene.Volumes;
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
    private const float DefaultFocusDurationSeconds = 0.35f;
    private const float DefaultFocusRadius = 1.0f;
    private const float FocusRadiusPadding = 0.75f;
    private const float MinimumFocusDistance = 0.5f;
    private const float FocusCompletionThreshold = 0.999f;

    private CameraFocusLerpState? _cameraFocusLerp = null;

    private struct CameraFocusLerpState
    {
        public Vector3 StartPosition;
        public Vector3 TargetPosition;
        public Quaternion StartRotation;
        public Quaternion TargetRotation;
        public float StartTime;
        public float Duration;
    }

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

    private SortedDictionary<float, List<(XRComponent? item, object? data)>> _lastPhysicsPickResults = [];
    /// <summary>
    /// The last physics pick results from the raycast, sorted by distance.
    /// Use RaycastLock to access this safely.
    /// </summary>
    public SortedDictionary<float, List<(RenderInfo3D item, object? data)>> LastOctreePickResults
        => _lastOctreePickResults;

    private SortedDictionary<float, List<(RenderInfo3D item, object? data)>> _lastOctreePickResults = [];
    private readonly SortedDictionary<float, List<(RenderInfo3D item, object? data)>> _selectionPickResults = [];
    /// <summary>
    /// The last octree pick results from the raycast, sorted by distance.
    /// Use RaycastLock to access this safely.
    /// </summary>
    public SortedDictionary<float, List<(XRComponent? item, object? data)>> LastPhysicsPickResults
        => _lastPhysicsPickResults;

    private readonly ConcurrentQueue<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> _octreePickResultPool = new();
    private readonly ConcurrentQueue<SortedDictionary<float, List<(XRComponent? item, object? data)>>> _physicsPickResultPool = new();

    private SortedDictionary<float, List<(RenderInfo3D item, object? data)>> GetOctreePickResultDict()
    {
        if (_octreePickResultPool.TryDequeue(out var dict))
        {
            dict.Clear();
            return dict;
        }
        return [];
    }

    private void ReturnOctreePickResultDict(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dict)
    {
        dict.Clear();
        _octreePickResultPool.Enqueue(dict);
    }

    private SortedDictionary<float, List<(XRComponent? item, object? data)>> GetPhysicsPickResultDict()
    {
        if (_physicsPickResultPool.TryDequeue(out var dict))
        {
            dict.Clear();
            return dict;
        }
        return [];
    }

    private void ReturnPhysicsPickResultDict(SortedDictionary<float, List<(XRComponent? item, object? data)>> dict)
    {
        dict.Clear();
        _physicsPickResultPool.Enqueue(dict);
    }

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
            var octreeResults = GetOctreePickResultDict();
            var physicsResults = GetPhysicsPickResultDict();
            vp.PickSceneAsync(p, false, true, true, _layerMask, _physxQueryFilter, octreeResults, physicsResults, OctreeRaycastCallback, PhysicsRaycastCallback);
        }

        ApplyTransformations(vp);
    }

    private void PhysicsRaycastCallback(SortedDictionary<float, List<(XRComponent? item, object? data)>>? dictionary)
    {
        if (dictionary is not null)
        {
            using (_raycastLock.EnterScope())
            {
                var old = _lastPhysicsPickResults;
                _lastPhysicsPickResults = dictionary;
                ReturnPhysicsPickResultDict(old);
            }
        }

        if (!RenderRaycast)
            return;

        //foreach (var x in _lastPhysicsPickResults.Values)
        //    foreach (var (c2, _) in x)
        //        if (c2?.SceneNode is not null)
        //            Engine.Rendering.Debug.RenderLine(_lastRaycastSegment.Start, _lastRaycastSegment.End, ColorF4.Green);
    }

    private void OctreeRaycastCallback(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dictionary)
    {
        UpdateMeshHitVisualization(dictionary);

        if (RenderRaycast)
            TryRenderFirstRaycastResult(dictionary);

        using (_raycastLock.EnterScope())
        {
            var old = _lastOctreePickResults;
            _lastOctreePickResults = dictionary;
            ReturnOctreePickResultDict(old);
        }
    }

    private void TryRenderFirstRaycastResult(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dict)
    {
        if (dict.Count ==0)
            return;
        try
        {
            //Enumerator may throw if modified concurrently; catch and skip frame.
            var e = dict.GetEnumerator();
            if (!e.MoveNext())
                return;
            RenderRaycastResult(e.Current);
        }
        catch (InvalidOperationException)
        {
            //Concurrent modification; skip this frame.
        }
    }

    // Reusable buffer to avoid per-call allocations when copying first hit list safely.
    private readonly List<(RenderInfo3D item, object? data)> _firstHitBuffer = new();

    private void UpdateMeshHitVisualization(SortedDictionary<float, List<(RenderInfo3D item, object? data)>>? source = null)
    {
        _hitTriangle = null;
        _meshHitPoint = null;

        var results = source ?? _lastOctreePickResults;
        if (results.Count ==0)
            return;

        //We can be racing the async population of the dictionary. Acquire lock and copy the first list into a reusable buffer.
        //If modification sneaks in between enumerator creation and MoveNext, catch and bail (will be correct next frame).
        try
        {
            using (_raycastLock.EnterScope())
            {
                if (results.Count ==0)
                    return;

                foreach (var kvp in results)
                {
                    var list = kvp.Value;
                    _firstHitBuffer.Clear();
                    //Copy to stable buffer (list itself may be mutated by writer). Reuses allocated list.
                    for (int i =0; i < list.Count; i++)
                    {
                        var (item, _) = list[i];
                        if (item.Owner is TriggerVolumeComponent or BlockingVolumeComponent)
                            continue;

                        _firstHitBuffer.Add(list[i]);
                    }

                    if (_firstHitBuffer.Count > 0)
                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            //Dictionary modified during enumeration; skip this frame.
            return;
        }

        for (int i =0; i < _firstHitBuffer.Count; i++)
        {
            var (_, data) = _firstHitBuffer[i];
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
        var list = result.Value;
        if (list is null || list.Count ==0)
            return;

        //Capture stable snapshot to avoid modifications mid-iteration.
        for (int i =0; i < list.Count; i++)
        {
            var (info, data) = list[i];
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

    private bool TryCollectModelComponentHits(List<SceneNode> currentHits)
    {
        var vp = Viewport;
        var world = vp?.World;
        if (vp is null || world is null)
            return false;

        Segment segment = GetWorldSegment(vp);
        if (!world.RaycastOctree(segment, _selectionPickResults))
            return false;

        bool found = false;
        //Iterate without copying the dictionary; use its enumerator once then index into lists.
        foreach (var kvp in _selectionPickResults)
        {
            var list = kvp.Value;
            for (int i =0; i < list.Count; i++)
            {
                var (_, data) = list[i];
                if (data is not MeshPickResult meshHit)
                    continue;

                if (meshHit.Component is ModelComponent modelComponent && modelComponent.SceneNode is SceneNode node)
                {
                    currentHits.Add(node);
                    found = true;
                }
            }
        }

        if (found)
            UpdateMeshHitVisualization(_selectionPickResults);

        return found;
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
        bool validDepth = depth is not null && depth.Value >0.0f && depth.Value <1.0f;
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
        var fbo = vp.RenderPipelineInstance?.GetFBO<XRFrameBuffer>(DefaultRenderPipeline.ForwardPassFBOName);
        if (fbo is null)
            return null;
        float? depth = vp.GetDepth(fbo, (IVector2)internalSizeCoordinate);
        return depth;
    }

    private bool NeedsDepthHit() => _depthQueryRequested;

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

    private Vector2? _lastRotateDelta = null;
    private Vector3? _arcballRotationPosition = null;
    private Vector2? _lastMouseTranslationDelta = null;
    private float? _lastScrollDelta = null;
    private bool _wantsScreenshot = false;

    private void ApplyTransformations(XRViewport vp)
    {
        var tfm = TransformAs<Transform>();
        if (tfm is null)
            return;

        var scroll = _lastScrollDelta; _lastScrollDelta = null;
        var trans = _lastMouseTranslationDelta; _lastMouseTranslationDelta = null;
        var rot = _lastRotateDelta; _lastRotateDelta = null;

        bool hasInput = scroll.HasValue || trans.HasValue || rot.HasValue;
        if (_cameraFocusLerp.HasValue)
        {
            if (hasInput)
                CancelCameraFocusLerp();
            else
                return;
        }

        if (scroll.HasValue)
        {
            float scrollSpeed = scroll.Value;
            if (DepthHitNormalizedViewportPoint.HasValue)
            {
                if (ShiftPressed)
                    scrollSpeed *= ShiftSpeedModifier;
                Vector3 worldCoord = vp.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint.Value);
                float dist = Transform.WorldTranslation.Distance(worldCoord);
                tfm.Translation = Segment.PointAtLineDistance(Transform.WorldTranslation, worldCoord, scrollSpeed * dist *0.1f * ScrollSpeed);
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
                float x = rot.Value.X; float y = rot.Value.Y;
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

    protected override void MouseRotate(float x, float y)
    {
        if (_arcballRotationPosition is not null)
            _lastRotateDelta = new Vector2(-x * MouseRotateSpeed, y * MouseRotateSpeed);
        else
            base.MouseRotate(x, y);
    }

    private bool GetAverageSelectionPoint(out Vector3 avgPoint)
    {
        if (Selection.SceneNodes.Length ==0)
        {
            avgPoint = Vector3.Zero; return false;
        }
        avgPoint = GetAverageSelectionPoint();
        if (!(GetCamera()?.Camera?.WorldFrustum().ContainsPoint(avgPoint) ?? false))
        {
            avgPoint = Vector3.Zero; return false;
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

    protected override void MouseTranslate(float x, float y)
    {
        if (WorldDragPoint.HasValue)
        {
            if (Math.Abs(x) <0.00001f && Math.Abs(y) <0.00001f)
                return;
            _lastMouseTranslationDelta = new Vector2(-x, -y);
        }
        else
            base.MouseTranslate(x, y);
    }

    protected override void OnScrolled(float diff)
    {
        if (IsHoveringUI())
            return;
        _lastScrollDelta = diff;
        _depthQueryRequested = true;
    }

    public void TakeScreenshot() => _wantsScreenshot = true;

    public void FocusOnNode(SceneNode node, float durationSeconds = DefaultFocusDurationSeconds)
    {
        if (node?.Transform is null) return;
        var tfm = TransformAs<Transform>(); if (tfm is null) return;
        CancelCameraFocusLerp();
        Vector3 focusPoint = node.Transform.WorldTranslation;
        Vector3 cameraPosition = tfm.WorldTranslation;
        Vector3 direction = focusPoint - cameraPosition;
        if (direction.LengthSquared() < XRMath.Epsilon)
            direction = tfm.WorldForward;
        else
            direction = Vector3.Normalize(direction);
        float focusDistance = ComputeFocusDistance(node.Transform);
        Vector3 targetPosition = focusPoint - direction * focusDistance;
        Vector3 up = Globals.Up;
        if (MathF.Abs(Vector3.Dot(direction, up)) >0.999f)
            up = Globals.Right;
        Quaternion targetRotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(targetPosition, direction, up));
        BeginCameraFocusLerp(targetPosition, targetRotation, durationSeconds);
    }

    private void BeginCameraFocusLerp(Vector3 targetPosition, Quaternion targetRotation, float durationSeconds)
    {
        var tfm = TransformAs<Transform>(); if (tfm is null) return;
        float clampedDuration = MathF.Max(0.01f, durationSeconds);
        _cameraFocusLerp = new CameraFocusLerpState
        {
            StartPosition = tfm.WorldTranslation,
            TargetPosition = targetPosition,
            StartRotation = tfm.WorldRotation,
            TargetRotation = Quaternion.Normalize(targetRotation),
            StartTime = Engine.Time.Timer.Time(),
            Duration = clampedDuration
        };
    }

    private void UpdateCameraFocusLerp()
    {
        if (!_cameraFocusLerp.HasValue) return;
        if (HasContinuousMovementInput()) { CancelCameraFocusLerp(); return; }
        var tfm = TransformAs<Transform>(); if (tfm is null) { _cameraFocusLerp = null; return; }
        var lerp = _cameraFocusLerp.Value;
        float elapsed = Engine.Time.Timer.Time() - lerp.StartTime;
        float t = float.Clamp(elapsed / lerp.Duration,0.0f,1.0f);
        float eased = EaseInOut(t);
        Vector3 position = Vector3.Lerp(lerp.StartPosition, lerp.TargetPosition, eased);
        Quaternion rotation = Quaternion.Normalize(Quaternion.Slerp(lerp.StartRotation, lerp.TargetRotation, eased));
        tfm.SetWorldTranslationRotation(position, rotation);
        if (t >= FocusCompletionThreshold)
        {
            _cameraFocusLerp = null; SyncYawPitchWithRotation(rotation);
        }
    }

    private void CancelCameraFocusLerp()
    {
        if (!_cameraFocusLerp.HasValue) return;
        _cameraFocusLerp = null;
        var tfm = TransformAs<Transform>(); if (tfm is not null) SyncYawPitchWithRotation(tfm.WorldRotation);
    }

    private float ComputeFocusDistance(TransformBase focusTransform)
    {
        float radius = EstimateHierarchyRadius(focusTransform);
        if (!float.IsFinite(radius) || radius < XRMath.Epsilon) radius = DefaultFocusRadius;
        float distance = radius + FocusRadiusPadding;
        var cameraComponent = GetCamera();
        if (cameraComponent?.Camera.Parameters is XRPerspectiveCameraParameters perspective)
        {
            float fovRadians = XRMath.DegToRad(perspective.VerticalFieldOfView);
            float halfFov = float.Clamp(fovRadians *0.5f,0.1f, XRMath.PIf *0.45f);
            float perspectiveDistance = (radius + FocusRadiusPadding) / MathF.Tan(halfFov);
            distance = Math.Max(distance, perspectiveDistance);
        }
        return Math.Max(distance, MinimumFocusDistance);
    }

    private static float EstimateHierarchyRadius(TransformBase focusTransform)
    {
        Vector3 center = focusTransform.WorldTranslation;
        float radius =0.0f;
        Stack<TransformBase> stack = new(); stack.Push(focusTransform);
        while (stack.Count >0)
        {
            var current = stack.Pop(); if (current is null) continue;
            float distance = Vector3.Distance(center, current.WorldTranslation);
            if (distance > radius) radius = distance;
            foreach (var child in current.Children)
                if (child is not null) stack.Push(child);
        }
        return radius;
    }

    private static float EaseInOut(float t)
    {
        t = float.Clamp(t,0.0f,1.0f); return t * t * (3.0f -2.0f * t);
    }

    private void SyncYawPitchWithRotation(Quaternion worldRotation)
    {
        var euler = XRMath.QuaternionToEuler(worldRotation);
        SetYawPitch(XRMath.RadToDeg(euler.Y), XRMath.RadToDeg(euler.X));
    }

    private bool HasContinuousMovementInput()
    {
        const float threshold =0.0001f;
        return MathF.Abs(_incRight) > threshold || MathF.Abs(_incForward) > threshold || MathF.Abs(_incUp) > threshold || MathF.Abs(_incPitch) > threshold || MathF.Abs(_incYaw) > threshold;
    }

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

    private void Select()
    {
        if (TransformTool3D.GetActiveInstance(out var tfmComp) && tfmComp is not null && tfmComp.Highlighted || IsHoveringUI())
            return;
        List<SceneNode> currentHits = [];
        if (!TryCollectModelComponentHits(currentHits))
            CollectHits(currentHits);
        if (_lastHits is null)
            SetSelection(currentHits);
        else
            UpdateSelection(LocalInput, CompareAgainstLastHits(currentHits, ref _lastHits, ref _lastHitIndex));
    }

    private void SetSelection(List<SceneNode> currentHits)
    {
        _lastHits = currentHits; _lastHitIndex =0;
        Selection.SceneNodes = currentHits.Count >=1 ? [currentHits[0]] : [];
    }

    private static SceneNode? CompareAgainstLastHits(List<SceneNode> currentHits, ref List<SceneNode> lastHits, ref int lastHitIndex)
    {
        bool sameNodes = currentHits.Count >0 && currentHits.Intersect(lastHits).Count() == currentHits.Count;
        SceneNode? node;
        if (sameNodes)
        {
            lastHitIndex = (lastHitIndex +1) % currentHits.Count;
            node = currentHits[lastHitIndex];
        }
        else
        {
            lastHits = currentHits; lastHitIndex =0;
            node = currentHits.Count >0 ? currentHits[lastHitIndex] : null;
        }
        return node;
    }

    private void CollectHits(List<SceneNode> currentHits)
    {
        using var scope = _raycastLock.EnterScope();
        foreach (var x in _lastPhysicsPickResults.Values)
            for (int i =0; i < x.Count; i++)
            {
                var (comp, _) = x[i];
                if (comp?.SceneNode is not null)
                    currentHits.Add(comp.SceneNode);
            }
        foreach (var x in _lastOctreePickResults.Values)
            for (int i =0; i < x.Count; i++)
            {
                var (info, _) = x[i];
                if (info.Owner is XRComponent comp && comp.SceneNode is not null)
                    currentHits.Add(comp.SceneNode);
                else if (info.Owner is TransformBase tfm && tfm.SceneNode is not null)
                    currentHits.Add(tfm.SceneNode);
            }
    }

    private static void UpdateSelection(LocalInputInterface? input, SceneNode? node)
    {
        if (node is null) { Selection.SceneNodes = []; return; }
        var kbd = input?.Keyboard;
        if (kbd is not null)
        {
            if (kbd.GetKeyState(EKey.ControlLeft, EButtonInputType.Pressed) || kbd.GetKeyState(EKey.ControlRight, EButtonInputType.Pressed))
            {
                if (Selection.SceneNodes.Contains(node))
                    RemoveNode(node);
                else
                    AddNode(node);
                return;
            }
            else if (kbd.GetKeyState(EKey.AltLeft, EButtonInputType.Pressed) || kbd.GetKeyState(EKey.AltRight, EButtonInputType.Pressed))
            {
                RemoveNode(node); return;
            }
            else if (kbd.GetKeyState(EKey.ShiftLeft, EButtonInputType.Pressed) || kbd.GetKeyState(EKey.ShiftRight, EButtonInputType.Pressed))
            {
                AddNode(node); return;
            }
        }
        Selection.SceneNodes = [node];
    }

    private static void AddNode(SceneNode node)
    {
        if (Selection.SceneNodes.Contains(node)) return;
        Selection.SceneNodes = [.. Selection.SceneNodes, node];
    }

    private static void RemoveNode(SceneNode node)
    {
        if (!Selection.SceneNodes.Contains(node)) return;
        Selection.SceneNodes = [.. Selection.SceneNodes.Where(n => n != node)];
    }
}
