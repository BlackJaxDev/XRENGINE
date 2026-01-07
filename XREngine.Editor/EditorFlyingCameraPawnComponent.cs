using Extensions;
using ImageMagick;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
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
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Picking;
using XREngine.Scene;
using XREngine.Scene.Components.Editing;
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
        UpdateSelectionHighlight();
    }

    /// <summary>
    /// Updates the selection highlight, enabling stencil on selected meshes and disabling on deselected ones.
    /// </summary>
    private void UpdateSelectionHighlight()
    {
        // Clear previous selection highlights
        foreach (var material in _currentSelectionHighlightMaterials)
            DefaultRenderPipeline.SetHighlighted(material, false);
        _currentSelectionHighlightMaterials.Clear();

        if (!SelectionOutlineEnabled)
            return;

        // Highlight materials of selected nodes
        foreach (var node in Selection.SceneNodes)
        {
            var modelComponent = node.GetComponent<ModelComponent>();
            if (modelComponent is null)
                continue;

            foreach (var mesh in modelComponent.Meshes)
            {
                foreach (var lod in mesh.LODs)
                {
                    var material = lod.Renderer.Material;
                    if (material is not null && _currentSelectionHighlightMaterials.Add(material))
                        DefaultRenderPipeline.SetHighlighted(material, true, isSelection: true);
                }
            }
        }
    }

    private Vector3? _worldDragPoint = null;
    /// <summary>
    /// The reference world position to drag the camera to when dragging or rotating the camera.
    /// </summary>
    [Browsable(false)]
    public Vector3? WorldDragPoint
    {
        get => _worldDragPoint;
        private set => SetField(ref _worldDragPoint, value);
    }

    public Vector3? LastDepthHitNormalizedViewportPoint => _lastDepthHitNormalizedViewportPoint;
    /// <summary>
    /// The last hit point in normalized viewport coordinates from the depth buffer.
    /// </summary>
    [Browsable(false)]
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
    [Browsable(false)]
    public float LastHitDistance => XRMath.DepthToDistance(DepthHitNormalizedViewportPoint.HasValue ? DepthHitNormalizedViewportPoint.Value.Z : 0.0f, NearZ, FarZ);

    /// <summary>
    /// The near Z distance of the camera's frustum.
    /// </summary>
    [Browsable(false)]
    public float NearZ => GetCamera()?.Camera.NearZ ?? 0.0f;

    /// <summary>
    /// The far Z distance of the camera's frustum.
    /// </summary>
    [Browsable(false)]
    public float FarZ => GetCamera()?.Camera.FarZ ?? 0.0f;

    private bool _renderWorldDragPoint = false;
    /// <summary>
    /// If true, renders a sphere to display the world drag position in the scene.
    /// </summary>
    [Category("Debug")]
    public bool RenderWorldDragPoint
    {
        get => _renderWorldDragPoint;
        set => SetField(ref _renderWorldDragPoint, value);
    }

    private bool _renderFrustum = false;
    /// <summary>
    /// If true, renders this pawn's camera frustum in the scene.
    /// </summary>
    [Category("Debug")]
    public bool RenderFrustum
    {
        get => _renderFrustum;
        set => SetField(ref _renderFrustum, value);
    }

    #region Debug Camera Mode

    private bool _debugCameraMode = false;
    /// <summary>
    /// When enabled, switches control to a debug camera that performs no culling,
    /// allowing visualization of the actual editor camera's frustum and culling behavior.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Debug Camera Mode")]
    [Description("When enabled, control switches to a debug camera with no culling to visualize the editor camera's frustum.")]
    public bool DebugCameraMode
    {
        get => _debugCameraMode;
        set
        {
            if (_debugCameraMode != value)
            {
                SetField(ref _debugCameraMode, value);
                OnDebugCameraModeChanged(value);
            }
        }
    }

    private ColorF4 _debugFrustumColor = ColorF4.Cyan;
    /// <summary>
    /// The color used to render the editor camera's frustum in debug mode.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Frustum Color")]
    [Description("Color used to render the editor camera's frustum wireframe.")]
    public ColorF4 DebugFrustumColor
    {
        get => _debugFrustumColor;
        set => SetField(ref _debugFrustumColor, value);
    }

    private ColorF4 _debugFrustumNearPlaneColor = ColorF4.Green;
    /// <summary>
    /// The color used to render the near plane of the editor camera's frustum.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Near Plane Color")]
    [Description("Color used to render the near plane of the frustum.")]
    public ColorF4 DebugFrustumNearPlaneColor
    {
        get => _debugFrustumNearPlaneColor;
        set => SetField(ref _debugFrustumNearPlaneColor, value);
    }

    private ColorF4 _debugFrustumFarPlaneColor = ColorF4.Red;
    /// <summary>
    /// The color used to render the far plane of the editor camera's frustum.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Far Plane Color")]
    [Description("Color used to render the far plane of the frustum.")]
    public ColorF4 DebugFrustumFarPlaneColor
    {
        get => _debugFrustumFarPlaneColor;
        set => SetField(ref _debugFrustumFarPlaneColor, value);
    }

    private bool _renderFrustumPlanes = true;
    /// <summary>
    /// When true, renders semi-transparent near and far planes of the frustum.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Render Frustum Planes")]
    [Description("When enabled, renders semi-transparent near and far planes of the stored frustum.")]
    public bool RenderFrustumPlanes
    {
        get => _renderFrustumPlanes;
        set => SetField(ref _renderFrustumPlanes, value);
    }

    private bool _renderCameraPositionGizmo = true;
    /// <summary>
    /// When true, renders a gizmo at the editor camera's position in debug mode.
    /// </summary>
    [Category("Debug Camera")]
    [DisplayName("Render Camera Gizmo")]
    [Description("Shows a visual representation of the editor camera's position and orientation.")]
    public bool RenderCameraPositionGizmo
    {
        get => _renderCameraPositionGizmo;
        set => SetField(ref _renderCameraPositionGizmo, value);
    }

    // Store the editor camera's transform when entering debug mode
    private Vector3 _storedEditorCameraPosition;
    private Quaternion _storedEditorCameraRotation;

    // Debug camera component created for visualization
    private SceneNode? _debugCameraNode = null;
    private CameraComponent? _debugCameraComponent = null;

    /// <summary>
    /// Gets the stored frustum of the editor camera for visualization purposes.
    /// </summary>
    [Browsable(false)]
    public Frustum? StoredEditorCameraFrustum { get; private set; }

    #endregion

    private bool _renderHoveredPrimitive = false;
    /// <summary>
    /// If true, renders the currently hovered primitive (face, edge, or vertex) based on RaycastMode.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Render Hovered Primitive")]
    [Description("When enabled, renders the hovered face, edge, or vertex depending on the raycast mode.")]
    public bool RenderHoveredPrimitive
    {
        get => _renderHoveredPrimitive;
        set => SetField(ref _renderHoveredPrimitive, value);
    }

    private ColorF4 _hoveredFaceFillColor = new(1.0f, 0.9f, 0.0f, 0.4f);
    /// <summary>
    /// The fill color for hovered faces (with stipple pattern).
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Hovered Face Fill Color")]
    [Description("The fill color for hovered faces with diagonal stipple pattern.")]
    public ColorF4 HoveredFaceFillColor
    {
        get => _hoveredFaceFillColor;
        set => SetField(ref _hoveredFaceFillColor, value);
    }

    private ColorF4 _hoveredFaceEdgeColor = ColorF4.Yellow;
    /// <summary>
    /// The edge/outline color for hovered faces.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Hovered Face Edge Color")]
    [Description("The color of the edges around hovered faces.")]
    public ColorF4 HoveredFaceEdgeColor
    {
        get => _hoveredFaceEdgeColor;
        set => SetField(ref _hoveredFaceEdgeColor, value);
    }

    private float _stippleScale = 8.0f;
    /// <summary>
    /// The scale of the diagonal stipple pattern in pixels.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Stipple Scale")]
    [Description("The scale of the diagonal stipple pattern in screen pixels.")]
    public float StippleScale
    {
        get => _stippleScale;
        set => SetField(ref _stippleScale, value);
    }

    private float _stippleThickness = 2.0f;
    /// <summary>
    /// The thickness of the stipple lines.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Stipple Thickness")]
    [Description("The thickness of the stipple lines in the pattern.")]
    public float StippleThickness
    {
        get => _stippleThickness;
        set => SetField(ref _stippleThickness, value);
    }

    private float _stippleDepthOffset = 0.00005f;
    /// <summary>
    /// Depth offset applied to the hovered face stippled overlay to prevent z-fighting.
    /// Higher values push the overlay closer to the camera.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Stipple Depth Offset")]
    [Description("Depth offset applied to the stippled face overlay to render on top of the underlying triangle.")]
    public float StippleDepthOffset
    {
        get => _stippleDepthOffset;
        set => SetField(ref _stippleDepthOffset, value);
    }

    // Stippled triangle renderer components
    private XRMeshRenderer? _stippledTriangleRenderer;
    private XRMesh? _stippledTriangleMesh;

    private bool _hoverOutlineEnabled = false;
    /// <summary>
    /// If true, renders an outline around the mesh currently under the cursor using stencil buffer.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Hover Outline")]
    [Description("When enabled, renders an outline around the mesh under the cursor.")]
    public bool HoverOutlineEnabled
    {
        get => _hoverOutlineEnabled;
        set
        {
            if (SetField(ref _hoverOutlineEnabled, value) && !value)
                ClearHoverHighlight();
        }
    }

    private bool _renderHoveredNodeName = false;
    /// <summary>
    /// If true, renders the name of the hovered scene node as debug text.
    /// </summary>
    [Category("Raycasting")]
    [DisplayName("Render Hovered Node Name")]
    [Description("When enabled, displays the scene node name at the hit point.")]
    public bool RenderHoveredNodeName
    {
        get => _renderHoveredNodeName;
        set => SetField(ref _renderHoveredNodeName, value);
    }

    private bool _selectionOutlineEnabled = true;
    /// <summary>
    /// If true, renders an outline around selected meshes using stencil buffer.
    /// </summary>
    [Category("Selection")]
    [DisplayName("Selection Outline")]
    [Description("When enabled, renders an outline around selected meshes.")]
    public bool SelectionOutlineEnabled
    {
        get => _selectionOutlineEnabled;
        set
        {
            if (SetField(ref _selectionOutlineEnabled, value))
                UpdateSelectionHighlight();
        }
    }

    /// <summary>
    /// The material currently highlighted for hover outline.
    /// </summary>
    private XRMaterial? _currentHoverHighlightMaterial = null;

    /// <summary>
    /// The materials currently highlighted for selection outline.
    /// </summary>
    private readonly HashSet<XRMaterial> _currentSelectionHighlightMaterials = [];

    private PhysxScene.PhysxQueryFilter _physxQueryFilter = new();
    [Category("Raycasting")]
    public PhysxScene.PhysxQueryFilter PhysxQueryFilter
    {
        get => _physxQueryFilter;
        set => SetField(ref _physxQueryFilter, value);
    }

    private LayerMask _layerMask = LayerMask.GetMask(DefaultLayers.Dynamic);
    [Category("Raycasting")]
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
    [Browsable(false)]
    public SortedDictionary<float, List<(RenderInfo3D item, object? data)>> LastOctreePickResults
        => _lastOctreePickResults;

    private SortedDictionary<float, List<(RenderInfo3D item, object? data)>> _lastOctreePickResults = [];
    /// <summary>
    /// The last octree pick results from the raycast, sorted by distance.
    /// Use RaycastLock to access this safely.
    /// </summary>
    [Browsable(false)]
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
    private Triangle? _facePickResult = null;
    private Vector3? _meshHitPoint = null;
    private MeshEdgePickResult? _edgePickResult = null;
    private MeshVertexPickResult? _vertexPickResult = null;
    private MeshPickResult? _meshPickResult = null;
    private Segment _lastRaycastSegment = new(Vector3.Zero, Vector3.Zero);
    private Vector3? _depthHitNormalizedViewportPoint = null;
    private Vector3? _lastDepthHitNormalizedViewportPoint = null;

    private readonly Lock _raycastLock = new();
    /// <summary>
    /// The lock used to safely access the raycast results.
    /// </summary>
    [Browsable(false)]
    public Lock RaycastLock => _raycastLock;

    private bool _depthQueryRequested = false;

    private bool _allowWorldPicking = true;
    /// <summary>
    /// If true, raycasts will be performed to pick objects in the world.
    /// </summary>
    [Category("Raycasting")]
    public bool AllowWorldPicking
    {
        get => _allowWorldPicking;
        set => SetField(ref _allowWorldPicking, value);
    }

    private ERaycastHitMode _raycastMode = ERaycastHitMode.Faces;
    /// <summary>
    /// Determines whether the editor should raycast faces, edges, or vertices.
    /// </summary>
    [Category("Raycasting")]
    public ERaycastHitMode RaycastMode
    {
        get => _raycastMode;
        set => SetField(ref _raycastMode, value);
    }

    protected ERaycastHitMode CurrentRaycastMode => _raycastMode;
    protected Triangle? CurrentFacePickResult => _facePickResult;
    protected MeshEdgePickResult? CurrentEdgePickResult => _edgePickResult;
    protected MeshVertexPickResult? CurrentVertexPickResult => _vertexPickResult;
    protected MeshPickResult? CurrentMeshPickResult => _meshPickResult;
    protected Vector3? CurrentMeshHitPoint => _meshHitPoint;

    public bool TryGetLastMeshHit(out MeshPickResult result)
    {
        using (_raycastLock.EnterScope())
        {
            if (_meshPickResult.HasValue)
            {
                result = _meshPickResult.Value;
                return true;
            }
        }

        result = default;
        return false;
    }

    private readonly RenderCommandMethod3D _postRenderRC;
    private readonly RenderCommandMethod3D _renderHighlightRC;

    [Browsable(false)]
    public RenderInfo[] RenderedObjects { get; }

    private void RenderHighlight()
    {
        if (Engine.Rendering.State.IsShadowPass)
            return;

        if (RenderHoveredPrimitive)
            RenderPickModeOverlay();
        
        if (RenderWorldDragPoint && (WorldDragPoint.HasValue || DepthHitNormalizedViewportPoint.HasValue) && Viewport is not null)
        {
            Vector3 pos;
            if (WorldDragPoint.HasValue)
                pos = WorldDragPoint.Value;
            else
                pos = Viewport.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint!.Value);
            Engine.Rendering.Debug.RenderSphere(pos, (Viewport.Camera?.DistanceFromWorldPosition(pos) ?? 1.0f) * 0.05f, false, ColorF4.Yellow);
        }

        if (RenderFrustum && !DebugCameraMode)
        {
            var cam = GetCamera();
            if (cam is not null)
                Engine.Rendering.Debug.RenderFrustum(cam.Camera.WorldFrustum(), ColorF4.Red);
        }

        // Render debug camera visualization when in debug mode
        if (DebugCameraMode)
            RenderDebugCameraVisualization();
    }

    private void RenderPickModeOverlay()
    {
        if (_meshHitPoint is Vector3 meshHit)
            Engine.Rendering.Debug.RenderPoint(meshHit, ColorF4.Yellow);
        switch (RaycastMode)
        {
            case ERaycastHitMode.Faces when _facePickResult is Triangle hit:
                RenderStippledTriangle(hit);
                break;
            case ERaycastHitMode.Lines when _edgePickResult is MeshEdgePickResult edgeHit:
                Engine.Rendering.Debug.RenderLine(edgeHit.EdgeStart, edgeHit.EdgeEnd, ColorF4.Cyan);
                Engine.Rendering.Debug.RenderPoint(edgeHit.ClosestPoint, ColorF4.Yellow);
                break;
            case ERaycastHitMode.Points when _vertexPickResult is MeshVertexPickResult vertexHit:
                Engine.Rendering.Debug.RenderPoint(vertexHit.Position, ColorF4.Yellow);
                break;
        }
    }

    /// <summary>
    /// Renders a triangle with stippled fill and solid edge lines.
    /// </summary>
    private void RenderStippledTriangle(Triangle triangle)
    {
        // Render edge lines
        Engine.Rendering.Debug.RenderLine(triangle.A, triangle.B, HoveredFaceEdgeColor);
        Engine.Rendering.Debug.RenderLine(triangle.B, triangle.C, HoveredFaceEdgeColor);
        Engine.Rendering.Debug.RenderLine(triangle.C, triangle.A, HoveredFaceEdgeColor);

        // Render stippled fill
        EnsureStippledTriangleRenderer();
        if (_stippledTriangleRenderer is null || _stippledTriangleMesh is null)
            return;

        // Update mesh vertices to match the triangle
        UpdateStippledTriangleMesh(triangle);

        // Update material uniforms
        var mat = _stippledTriangleRenderer.Material;
        if (mat is not null)
        {
            mat.SetVector4("FillColor", HoveredFaceFillColor);
            mat.SetFloat("StippleScale", StippleScale);
            mat.SetFloat("StippleThickness", StippleThickness);
            mat.SetFloat("DepthOffset", StippleDepthOffset);
        }

        // Render the stippled triangle
        _stippledTriangleRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity);
    }

    /// <summary>
    /// Ensures the stippled triangle renderer is initialized.
    /// </summary>
    private void EnsureStippledTriangleRenderer()
    {
        if (_stippledTriangleRenderer is not null)
            return;

        // Create the mesh with placeholder vertices
        _stippledTriangleMesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);

        // Create the material with stippled shader
        var fragShader = ShaderHelper.LoadEngineShader(Path.Combine("Common", "Debug", "StippledTriangle.fs"), EShaderType.Fragment);
        ShaderVar[] vars =
        [
            new ShaderVector4(HoveredFaceFillColor, "FillColor"),
            new ShaderFloat(StippleScale, "StippleScale"),
            new ShaderFloat(StippleThickness, "StippleThickness"),
            new ShaderFloat(_stippleDepthOffset, "DepthOffset"),
        ];
        var mat = new XRMaterial(vars, fragShader);
        mat.RenderOptions.CullMode = ECullMode.None;
        mat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
        mat.RenderOptions.DepthTest.UpdateDepth = false;
        mat.EnableTransparency();
        mat.RenderPass = (int)EDefaultRenderPass.TransparentForward;

        _stippledTriangleRenderer = new XRMeshRenderer(_stippledTriangleMesh, mat);
    }

    /// <summary>
    /// Updates the stippled triangle mesh with new vertex positions.
    /// </summary>
    private void UpdateStippledTriangleMesh(Triangle triangle)
    {
        if (_stippledTriangleMesh is null)
            return;

        // Update the position buffer directly
        _stippledTriangleMesh.SetPosition(0, triangle.A);
        _stippledTriangleMesh.SetPosition(1, triangle.B);
        _stippledTriangleMesh.SetPosition(2, triangle.C);
        _stippledTriangleMesh.PositionsBuffer?.PushSubData();
    }

    #region Debug Camera Mode Implementation

    /// <summary>
    /// Called when debug camera mode is toggled on or off.
    /// </summary>
    private void OnDebugCameraModeChanged(bool enabled)
    {
        if (enabled)
            EnterDebugCameraMode();
        else
            ExitDebugCameraMode();
    }

    /// <summary>
    /// Enters debug camera mode - stores the current camera state and enables visualization.
    /// </summary>
    private void EnterDebugCameraMode()
    {
        var editorCamera = GetCamera();
        if (editorCamera is null)
            return;

        // Store the current camera's transform for visualization
        _storedEditorCameraPosition = Transform.WorldTranslation;
        _storedEditorCameraRotation = Transform.WorldRotation;

        // Store the frustum for visualization
        StoredEditorCameraFrustum = editorCamera.Camera.WorldFrustum();

        // Store the original culling camera override and set a new one that uses the stored frustum
        // Culling remains enabled - objects are actually culled based on the stored frustum
        _originalCullingCameraOverride = editorCamera.CullingCameraOverride;
        
        // Create the debug camera node immediately so we have a valid camera to return
        EnsureDebugCameraNode();
        if (_debugCameraComponent is not null)
            editorCamera.CullingCameraOverride = GetStoredFrustumCamera;

        Debug.Out("[DebugCamera] Entered debug camera mode. Press F9 to exit.");
    }

    /// <summary>
    /// Exits debug camera mode - restores the original camera state.
    /// </summary>
    private void ExitDebugCameraMode()
    {
        var editorCamera = GetCamera();
        if (editorCamera is null)
            return;

        // Restore the original culling camera override
        editorCamera.CullingCameraOverride = _originalCullingCameraOverride;
        _originalCullingCameraOverride = null;

        // Clear stored frustum
        StoredEditorCameraFrustum = null;

        // Clean up debug camera node if created
        CleanupDebugCameraNode();

        Debug.Out("[DebugCamera] Exited debug camera mode.");
    }

    private Func<XRCamera>? _originalCullingCameraOverride = null;

    /// <summary>
    /// Returns a camera configured with the stored frustum for culling visualization.
    /// This method should only be called when _debugCameraComponent is guaranteed to exist.
    /// </summary>
    private XRCamera GetStoredFrustumCamera()
    {
        // This will only be called after EnsureDebugCameraNode creates the camera
        return _debugCameraComponent!.Camera;
    }

    /// <summary>
    /// Creates the debug camera node if it doesn't exist.
    /// </summary>
    private void EnsureDebugCameraNode()
    {
        if (_debugCameraNode is not null)
            return;

        var editorCamera = GetCamera();
        if (editorCamera is null)
            return;

        // Create a temporary node for the debug camera
        _debugCameraNode = SceneNode.Parent is not null 
            ? SceneNode.Parent.NewChild("DebugCameraVisualization")
            : SceneNode.World?.RootNodes.NewRootNode("DebugCameraVisualization");
        if (_debugCameraNode is null)
            return;
            
        var debugTransform = _debugCameraNode.SetTransform<Transform>();
        debugTransform.Translation = _storedEditorCameraPosition;
        debugTransform.Rotation = _storedEditorCameraRotation;
        debugTransform.RecalculateMatrices();

        // Add camera component with same parameters as editor camera
        _debugCameraComponent = _debugCameraNode.AddComponent<CameraComponent>();
        if (_debugCameraComponent is not null && editorCamera.Camera.Parameters is XRPerspectiveCameraParameters editorParams)
        {
            var debugParams = _debugCameraComponent.Camera.Parameters as XRPerspectiveCameraParameters;
            if (debugParams is not null)
            {
                debugParams.VerticalFieldOfView = editorParams.VerticalFieldOfView;
                debugParams.NearZ = editorParams.NearZ;
                debugParams.FarZ = editorParams.FarZ;
            }
        }
    }

    /// <summary>
    /// Cleans up the debug camera node.
    /// </summary>
    private void CleanupDebugCameraNode()
    {
        if (_debugCameraNode is not null)
        {
            _debugCameraNode.Destroy();
            _debugCameraNode = null;
            _debugCameraComponent = null;
        }
    }

    /// <summary>
    /// Updates the stored frustum when the user wants to recapture it.
    /// </summary>
    public void UpdateStoredFrustum()
    {
        if (!DebugCameraMode)
            return;

        var editorCamera = GetCamera();
        if (editorCamera is null)
            return;

        // Update stored position and rotation
        _storedEditorCameraPosition = Transform.WorldTranslation;
        _storedEditorCameraRotation = Transform.WorldRotation;

        // Temporarily enable culling to get the correct frustum
        bool wasCulling = editorCamera.CullWithFrustum;
        editorCamera.CullWithFrustum = true;
        StoredEditorCameraFrustum = editorCamera.Camera.WorldFrustum();
        editorCamera.CullWithFrustum = wasCulling;

        // Update debug camera position
        if (_debugCameraNode?.Transform is Transform debugTransform)
        {
            debugTransform.Translation = _storedEditorCameraPosition;
            debugTransform.Rotation = _storedEditorCameraRotation;
        }

        Debug.Out("[DebugCamera] Updated stored frustum position.");
    }

    /// <summary>
    /// Renders the debug camera visualization including frustum and camera gizmo.
    /// </summary>
    private void RenderDebugCameraVisualization()
    {
        if (!DebugCameraMode || !StoredEditorCameraFrustum.HasValue)
            return;

        var frustum = StoredEditorCameraFrustum.Value;

        // Render the frustum wireframe
        RenderFrustumWireframe(frustum);

        // Render the near and far planes with different colors
        if (RenderFrustumPlanes)
            RenderFrustumPlanesVisualization(frustum);

        // Render camera position gizmo
        if (RenderCameraPositionGizmo)
            RenderCameraGizmo();
    }

    /// <summary>
    /// Renders the frustum as a wireframe.
    /// </summary>
    private void RenderFrustumWireframe(Frustum frustum)
    {
        // Near plane edges
        Engine.Rendering.Debug.RenderLine(frustum.LeftTopNear, frustum.RightTopNear, DebugFrustumNearPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightTopNear, frustum.RightBottomNear, DebugFrustumNearPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightBottomNear, frustum.LeftBottomNear, DebugFrustumNearPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.LeftBottomNear, frustum.LeftTopNear, DebugFrustumNearPlaneColor);

        // Far plane edges
        Engine.Rendering.Debug.RenderLine(frustum.LeftTopFar, frustum.RightTopFar, DebugFrustumFarPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightTopFar, frustum.RightBottomFar, DebugFrustumFarPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightBottomFar, frustum.LeftBottomFar, DebugFrustumFarPlaneColor);
        Engine.Rendering.Debug.RenderLine(frustum.LeftBottomFar, frustum.LeftTopFar, DebugFrustumFarPlaneColor);

        // Connecting edges (frustum sides)
        Engine.Rendering.Debug.RenderLine(frustum.LeftTopNear, frustum.LeftTopFar, DebugFrustumColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightTopNear, frustum.RightTopFar, DebugFrustumColor);
        Engine.Rendering.Debug.RenderLine(frustum.RightBottomNear, frustum.RightBottomFar, DebugFrustumColor);
        Engine.Rendering.Debug.RenderLine(frustum.LeftBottomNear, frustum.LeftBottomFar, DebugFrustumColor);
    }

    /// <summary>
    /// Renders semi-transparent planes for the near and far frustum planes.
    /// </summary>
    private void RenderFrustumPlanesVisualization(Frustum frustum)
    {
        // Render near plane as filled quad
        var nearPlaneColor = new ColorF4(DebugFrustumNearPlaneColor.R, DebugFrustumNearPlaneColor.G, DebugFrustumNearPlaneColor.B, 0.15f);
        Engine.Rendering.Debug.RenderTriangle(
            new Triangle(frustum.LeftTopNear, frustum.RightTopNear, frustum.RightBottomNear),
            nearPlaneColor, true);
        Engine.Rendering.Debug.RenderTriangle(
            new Triangle(frustum.LeftTopNear, frustum.RightBottomNear, frustum.LeftBottomNear),
            nearPlaneColor, true);

        // Render far plane as filled quad
        var farPlaneColor = new ColorF4(DebugFrustumFarPlaneColor.R, DebugFrustumFarPlaneColor.G, DebugFrustumFarPlaneColor.B, 0.1f);
        Engine.Rendering.Debug.RenderTriangle(
            new Triangle(frustum.LeftTopFar, frustum.RightBottomFar, frustum.RightTopFar),
            farPlaneColor, true);
        Engine.Rendering.Debug.RenderTriangle(
            new Triangle(frustum.LeftTopFar, frustum.LeftBottomFar, frustum.RightBottomFar),
            farPlaneColor, true);
    }

    /// <summary>
    /// Renders a visual gizmo at the stored camera position.
    /// </summary>
    private void RenderCameraGizmo()
    {
        const float gizmoSize = 0.5f;
        const float axisLength = 1.5f;

        // Calculate axes from rotation
        var forward = Vector3.Transform(Globals.Forward, _storedEditorCameraRotation);
        var right = Vector3.Transform(Globals.Right, _storedEditorCameraRotation);
        var up = Vector3.Transform(Globals.Up, _storedEditorCameraRotation);

        // Render camera position sphere
        Engine.Rendering.Debug.RenderSphere(_storedEditorCameraPosition, gizmoSize * 0.3f, false, ColorF4.White);

        // Render camera axes
        Engine.Rendering.Debug.RenderLine(_storedEditorCameraPosition, _storedEditorCameraPosition + forward * axisLength, ColorF4.Blue);
        Engine.Rendering.Debug.RenderLine(_storedEditorCameraPosition, _storedEditorCameraPosition + right * axisLength * 0.5f, ColorF4.Red);
        Engine.Rendering.Debug.RenderLine(_storedEditorCameraPosition, _storedEditorCameraPosition + up * axisLength * 0.5f, ColorF4.Green);

        // Render a small pyramid to indicate the camera direction
        var pyramidTip = _storedEditorCameraPosition + forward * gizmoSize;
        var pyramidBase = _storedEditorCameraPosition;
        float baseSize = gizmoSize * 0.3f;

        var baseCorners = new[]
        {
            pyramidBase + right * baseSize + up * baseSize,
            pyramidBase + right * baseSize - up * baseSize,
            pyramidBase - right * baseSize - up * baseSize,
            pyramidBase - right * baseSize + up * baseSize
        };

        // Draw pyramid edges
        foreach (var corner in baseCorners)
            Engine.Rendering.Debug.RenderLine(pyramidTip, corner, ColorF4.Yellow);

        // Draw base edges
        for (int i = 0; i < 4; i++)
            Engine.Rendering.Debug.RenderLine(baseCorners[i], baseCorners[(i + 1) % 4], ColorF4.Yellow);
    }

    /// <summary>
    /// Toggles the debug camera mode on/off.
    /// </summary>
    public void ToggleDebugCameraMode()
    {
        DebugCameraMode = !DebugCameraMode;
    }

    #endregion

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
                AbstractRenderer.Current?.GetScreenshotAsync(vp.Region, false, (img, index) =>
                {
                    Utility.EnsureDirPathExists(capturePath);
                    img?.Flip();
                    img?.Write(Path.Combine(capturePath, $"Screenshot_{index:D4}.png"));
                });
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

        if (AllowWorldPicking)
        {
            var octreeResults = GetOctreePickResultDict();
            var physicsResults = GetPhysicsPickResultDict();
            vp.PickSceneAsync(p, false, true, true, _layerMask, _physxQueryFilter, octreeResults, physicsResults, OctreeRaycastCallback, PhysicsRaycastCallback, RaycastMode);
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

        //foreach (var x in _lastPhysicsPickResults.Values)
        //    foreach (var (c2, _) in x)
        //        if (c2?.SceneNode is not null)
        //            Engine.Rendering.Debug.RenderLine(_lastRaycastSegment.Start, _lastRaycastSegment.End, ColorF4.Green);
    }

    private void OctreeRaycastCallback(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dictionary)
    {
        UpdateMeshHitVisualization(dictionary);

        if (HoverOutlineEnabled || RenderHoveredNodeName)
            TryRenderFirstRaycastResult(dictionary);
        else
            ClearHoverHighlight();

        using (_raycastLock.EnterScope())
        {
            var old = _lastOctreePickResults;
            _lastOctreePickResults = dictionary;
            ReturnOctreePickResultDict(old);
        }
    }

    private void TryRenderFirstRaycastResult(SortedDictionary<float, List<(RenderInfo3D item, object? data)>> dict)
    {
        if (dict.Count == 0)
        {
            ClearHoverHighlight();
            return;
        }
        try
        {
            //Enumerator may throw if modified concurrently; catch and skip frame.
            var e = dict.GetEnumerator();
            if (!e.MoveNext())
            {
                ClearHoverHighlight();
                return;
            }
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
        Triangle? facePickResult = null;
        Vector3? meshHitPoint = null;
        MeshEdgePickResult? edgePickResult = null;
        MeshVertexPickResult? vertexPickResult = null;
        MeshPickResult? meshPickResult = null;

        var results = source ?? _lastOctreePickResults;
        if (results.Count == 0)
        {
            using (_raycastLock.EnterScope())
            {
                _facePickResult = null;
                _meshHitPoint = null;
                _edgePickResult = null;
                _vertexPickResult = null;
                _meshPickResult = null;
            }
            return;
        }

        //We can be racing the async population of the dictionary. Acquire lock and copy the first list into a reusable buffer.
        //If modification sneaks in between enumerator creation and MoveNext, catch and bail (will be correct next frame).
        try
        {
            using (_raycastLock.EnterScope())
            {
                if (results.Count == 0)
                    return;

                foreach (var kvp in results)
                {
                    var list = kvp.Value;
                    _firstHitBuffer.Clear();
                    //Copy to stable buffer (list itself may be mutated by writer). Reuses allocated list.
                    for (int i = 0; i < list.Count; i++)
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

        if (_firstHitBuffer.Count == 0)
            return;

        for (int i = 0; i < _firstHitBuffer.Count; i++)
        {
            var (_, data) = _firstHitBuffer[i];
            switch (data)
            {
                case MeshPickResult meshHit:
                    facePickResult = meshHit.WorldTriangle;
                    meshHitPoint = meshHit.HitPoint;
                    meshPickResult = meshHit;
                    goto Done;
                case MeshEdgePickResult edgeHit:
                    facePickResult = edgeHit.WorldTriangle;
                    meshHitPoint = edgeHit.ClosestPoint;
                    edgePickResult = edgeHit;
                    meshPickResult = edgeHit.FaceHit;
                    goto Done;
                case MeshVertexPickResult vertexHit:
                    facePickResult = vertexHit.WorldTriangle;
                    meshHitPoint = vertexHit.Position;
                    vertexPickResult = vertexHit;
                    meshPickResult = vertexHit.FaceHit;
                    goto Done;
                case Vector3 point:
                    meshHitPoint = point;
                    goto Done;
                case Triangle triangle:
                    facePickResult = triangle;
                    goto Done;
            }
        }

    Done:
        using (_raycastLock.EnterScope())
        {
            _facePickResult = facePickResult;
            _meshHitPoint = meshHitPoint;
            _edgePickResult = edgePickResult;
            _vertexPickResult = vertexPickResult;
            _meshPickResult = meshPickResult;
        }

    }

    private void RenderRaycastResult(KeyValuePair<float, List<(RenderInfo3D item, object? data)>> result)
    {
        var list = result.Value;
        if (list is null || list.Count == 0)
        {
            ClearHoverHighlight();
            return;
        }

        XRMaterial? firstHitMaterial = null;

        //Capture stable snapshot to avoid modifications mid-iteration.
        for (int i = 0; i < list.Count; i++)
        {
            var (info, data) = list[i];
            Vector3? point = data switch
            {
                Vector3 p => p,
                MeshPickResult meshHit => meshHit.HitPoint,
                MeshEdgePickResult edgeHit => edgeHit.ClosestPoint,
                MeshVertexPickResult vertexHit => vertexHit.Position,
                _ => null
            };

            // Get the material from the first mesh hit for hover outline
            if (firstHitMaterial is null && HoverOutlineEnabled)
            {
                firstHitMaterial = data switch
                {
                    MeshPickResult meshHit => meshHit.Mesh?.CurrentLODRenderer?.Material,
                    MeshEdgePickResult edgeHit => edgeHit.FaceHit.Mesh?.CurrentLODRenderer?.Material,
                    MeshVertexPickResult vertexHit => vertexHit.FaceHit.Mesh?.CurrentLODRenderer?.Material,
                    _ => null
                };
                // Skip materials that are already highlighted for selection
                if (firstHitMaterial is not null && _currentSelectionHighlightMaterials.Contains(firstHitMaterial))
                    firstHitMaterial = null;
            }

            if (point is null)
                continue;

            if (RenderHoveredNodeName)
            {
                string? name = info.Owner switch
                {
                    XRComponent component when component.SceneNode?.Name is string nodeName => nodeName,
                    TransformBase transform when transform.Name is not null => transform.Name,
                    _ => null
                };

                if (name is not null)
                    Engine.Rendering.Debug.RenderText(point.Value, name, ColorF4.Black);
            }
            //Engine.Rendering.Debug.RenderPoint(point.Value, ColorF4.Red);
        }

        // Update hover highlight
        UpdateHoverHighlight(firstHitMaterial);
    }

    /// <summary>
    /// Updates the hover highlight, enabling stencil on the new material and disabling on the previous.
    /// </summary>
    private void UpdateHoverHighlight(XRMaterial? newMaterial)
    {
        if (_currentHoverHighlightMaterial == newMaterial)
            return;

        // Disable highlight on previous material
        if (_currentHoverHighlightMaterial is not null)
            DefaultRenderPipeline.SetHighlighted(_currentHoverHighlightMaterial, false);

        // Enable highlight on new material
        if (newMaterial is not null && HoverOutlineEnabled)
            DefaultRenderPipeline.SetHighlighted(newMaterial, true);

        _currentHoverHighlightMaterial = newMaterial;
    }

    /// <summary>
    /// Clears any current hover highlight.
    /// </summary>
    private void ClearHoverHighlight()
    {
        if (_currentHoverHighlightMaterial is not null)
        {
            DefaultRenderPipeline.SetHighlighted(_currentHoverHighlightMaterial, false);
            _currentHoverHighlightMaterial = null;
        }
    }

    private bool TryCollectModelComponentHits(List<SceneNode> currentHits)
    {
        bool found = false;

        using var scope = _raycastLock.EnterScope();
        if (_lastOctreePickResults.Count == 0)
            return false;

        foreach (var kvp in _lastOctreePickResults)
        {
            var list = kvp.Value;
            for (int i = 0; i < list.Count; i++)
            {
                var (_, data) = list[i];
                if (!TryExtractMeshPickResult(data, out MeshPickResult meshHit))
                    continue;

                if (meshHit.Component is ModelComponent modelComponent && modelComponent.SceneNode is SceneNode node)
                {
                    currentHits.Add(node);
                    found = true;
                }
            }
        }

        if (found)
            UpdateMeshHitVisualization(_lastOctreePickResults);

        return found;
    }

    private Segment GetWorldSegment(XRViewport vp)
        => vp.GetWorldSegment(GetNormalizedCursorPosition(vp));

    private Vector2 GetNormalizedCursorPosition(XRViewport vp)
    {
        // Clamp to viewport bounds to avoid dispatching picks with UVs outside [0,1],
        // which can happen with multi-monitor/HiDPI coordinate drift and results in
        // empty pick results (hover outline never enabled).
        var uv = vp.NormalizeInternalCoordinate(GetCursorInternalCoordinatePosition(vp));
        // Clamp to just inside the viewport to avoid rays on the exact 1.0 edge
        // being treated as out-of-bounds by the picker.
        const float epsilon = 1e-4f;
        uv = Vector2.Clamp(uv, Vector2.Zero, Vector2.One - new Vector2(epsilon));
        return uv;
    }

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

        var scroll = _lastScrollDelta;
        _lastScrollDelta = null;

        var trans = _lastMouseTranslationDelta;
        _lastMouseTranslationDelta = null;

        var rot = _lastRotateDelta;
        _lastRotateDelta = null;

        bool hasInput = scroll.HasValue || trans.HasValue || rot.HasValue;
        if (_cameraFocusLerp.HasValue)
        {
            if (hasInput)
                CancelCameraFocusLerp();
            else
            {
                UpdateCameraFocusLerp();
                return;
            }
        }

        if (scroll.HasValue)
        {
            float scrollSpeed = scroll.Value;
            if (DepthHitNormalizedViewportPoint.HasValue)
            {
                if (ShiftPressed)
                    scrollSpeed *= ShiftSpeedModifier;

                //Make scroll-based dolly tickrate independent.
                float delta = Engine.UndilatedDelta;
                Vector3 worldCoord = vp.NormalizedViewportToWorldCoordinate(DepthHitNormalizedViewportPoint.Value);
                float dist = tfm.WorldTranslation.Distance(worldCoord);
                Vector3 newWorldPos = Segment.PointAtLineDistance(tfm.WorldTranslation, worldCoord, scrollSpeed * dist * 0.1f * ScrollSpeed * delta);
                tfm.SetWorldTranslation(newWorldPos);
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
        // Accumulate all scroll events until the next tick so none are dropped when multiple arrive in a frame.
        _lastScrollDelta = (_lastScrollDelta ?? 0.0f) + diff;
        _depthQueryRequested = true;
    }

    public void TakeScreenshot() => _wantsScreenshot = true;

    public void FocusOnNode(SceneNode node, float durationSeconds = DefaultFocusDurationSeconds)
    {
        if (node?.Transform is null)
            return;
        
        var tfm = TransformAs<Transform>(); 
        if (tfm is null)
            return;
        
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
        if (!_cameraFocusLerp.HasValue) 
            return;
        
        if (HasContinuousMovementInput()) 
        {
            CancelCameraFocusLerp(); 
            return;
        }

        var tfm = TransformAs<Transform>();
        if (tfm is null)
        {
            _cameraFocusLerp = null;
            return;
        }

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
        if (!_cameraFocusLerp.HasValue)
            return;
        
        _cameraFocusLerp = null;

        var tfm = TransformAs<Transform>(); 
        if (tfm is not null)
            SyncYawPitchWithRotation(tfm.WorldRotation);
    }

    private float ComputeFocusDistance(TransformBase focusTransform)
    {
        float radius = EstimateHierarchyRadius(focusTransform);
        if (!float.IsFinite(radius) || radius < XRMath.Epsilon)
            radius = DefaultFocusRadius;
        float distance = radius + FocusRadiusPadding;
        var cameraComponent = GetCamera();
        if (cameraComponent?.Camera.Parameters is XRPerspectiveCameraParameters perspective)
        {
            float fovRadians = XRMath.DegToRad(perspective.VerticalFieldOfView);
            float halfFov = float.Clamp(fovRadians * 0.5f, 0.1f, XRMath.PIf * 0.45f);
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
            var current = stack.Pop();
            if (current is null)
                continue;
            
            float distance = Vector3.Distance(center, current.WorldTranslation);
            if (distance > radius)
                radius = distance;

            foreach (var child in current.Children)
                if (child is not null)
                    stack.Push(child);
        }
        return radius;
    }

    private static float EaseInOut(float t)
    {
        t = float.Clamp(t,0.0f,1.0f);
        return t * t * (3.0f -2.0f * t);
    }

    private void SyncYawPitchWithRotation(Quaternion worldRotation)
    {
        var euler = XRMath.QuaternionToEuler(worldRotation);
        SetYawPitch(XRMath.RadToDeg(euler.Y), XRMath.RadToDeg(euler.X));
    }

    private bool HasContinuousMovementInput()
    {
        const float threshold = 0.0001f;
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
        input.RegisterKeyEvent(EKey.F5, EButtonInputType.Pressed, SetPickModeFaces);
        input.RegisterKeyEvent(EKey.F6, EButtonInputType.Pressed, SetPickModeLines);
        input.RegisterKeyEvent(EKey.F7, EButtonInputType.Pressed, SetPickModePoints);
        
        // Debug camera mode controls
        input.RegisterKeyEvent(EKey.F9, EButtonInputType.Pressed, ToggleDebugCameraMode);
        input.RegisterKeyEvent(EKey.F10, EButtonInputType.Pressed, UpdateStoredFrustum);
    }

    private void SetTransformModeParent() => TransformTool3D.TransformSpace = ETransformSpace.Parent;
    private void SetTransformModeScreen() => TransformTool3D.TransformSpace = ETransformSpace.Screen;
    private void SetTransformModeLocal() => TransformTool3D.TransformSpace = ETransformSpace.Local;
    private void SetTransformModeWorld() => TransformTool3D.TransformSpace = ETransformSpace.World;
    private void SetTransformScale() => TransformTool3D.TransformMode = ETransformMode.Scale;
    private void SetTransformRotation() => TransformTool3D.TransformMode = ETransformMode.Rotate;
    private void SetTransformTranslation() => TransformTool3D.TransformMode = ETransformMode.Translate;
    private void SetPickModeFaces() => RaycastMode = ERaycastHitMode.Faces;
    private void SetPickModeLines() => RaycastMode = ERaycastHitMode.Lines;
    private void SetPickModePoints() => RaycastMode = ERaycastHitMode.Points;

    private void Select()
    {
        if (TransformTool3D.GetActiveInstance(out var tfmComp) && tfmComp is not null && tfmComp.Highlighted)
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

    private static bool TryExtractMeshPickResult(object? data, out MeshPickResult meshHit)
    {
        switch (data)
        {
            case MeshPickResult faceHit:
                meshHit = faceHit;
                return true;
            case MeshEdgePickResult edgeHit:
                meshHit = edgeHit.FaceHit;
                return true;
            case MeshVertexPickResult vertexHit:
                meshHit = vertexHit.FaceHit;
                return true;
            default:
                meshHit = default;
                return false;
        }
    }
}
