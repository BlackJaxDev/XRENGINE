using System.ComponentModel;
using XREngine.Components;
using XREngine.Data.Colors;

namespace XREngine.Editor;

public sealed partial class MathBvhTestComponent
{
    private const int MaxConfigurableDebugNodes = 1_000_000;

    private MathBvhQueryShape _queryShape = MathBvhQueryShape.Box;
    private MathBvhDebugNodeVisibility _nodeVisibility = MathBvhDebugNodeVisibility.All;
    private bool _renderBaseNodes = true;
    private bool _renderVisitedNodes = true;
    private bool _renderIntersectedGeometry = true;
    private bool _renderSourceGeometry = true;
    private bool _renderQuery = true;
    private bool _renderQueryResults = true;
    private bool _renderHitMarker = true;
    private bool _renderValidationMarker = true;
    private ColorF4 _leafNodeColor = new(0.20f, 1.0f, 0.42f, 0.50f);
    private ColorF4 _internalNodeColor = new(0.20f, 0.68f, 1.0f, 0.32f);
    private ColorF4 _visitedNodeColor = new(1.0f, 0.78f, 0.08f, 1.0f);
    private ColorF4 _disjointGeometryColor = new(0.48f, 0.50f, 0.54f, 0.55f);
    private ColorF4 _intersectedGeometryColor = new(1.0f, 0.18f, 0.82f, 1.0f);
    private ColorF4 _containedGeometryColor = new(0.15f, 1.0f, 0.35f, 0.95f);
    private ColorF4 _queryColor = ColorF4.LightGreen;
    private ColorF4 _queryFailureColor = ColorF4.Red;
    private ColorF4 _hitMarkerColor = ColorF4.Yellow;
    private ColorF4 _validationPassedColor = ColorF4.LightGreen;
    private ColorF4 _validationPendingColor = ColorF4.Orange;
    private ColorF4 _validationFailedColor = ColorF4.Red;
    private int _maxDebugNodes = 4096;
    private float _baseNodeLineWidth = 0.0015f;
    private float _visitedNodeLineWidth = 0.0025f;
    private bool _queryPoints;
    private bool _queryLines;
    private bool _queryTriangles = true;

    [Category("BVH Query")]
    [Description("Selects the raycast or shape query used by this CPU or GPU BVH test.")]
    public MathBvhQueryShape QueryShape
    {
        get => _queryShape;
        set => SetField(ref _queryShape, value);
    }

    [Category("BVH Mesh Shape Query")]
    [Description("Returns every source-triangle vertex contained by a mesh shape query.")]
    public bool QueryPoints
    {
        get => _queryPoints;
        set => SetField(ref _queryPoints, value);
    }

    [Category("BVH Mesh Shape Query")]
    [Description("Returns every source-triangle edge intersected by a mesh shape query.")]
    public bool QueryLines
    {
        get => _queryLines;
        set => SetField(ref _queryLines, value);
    }

    [Category("BVH Mesh Shape Query")]
    [Description("Returns every source triangle intersected by a mesh shape query.")]
    public bool QueryTriangles
    {
        get => _queryTriangles;
        set => SetField(ref _queryTriangles, value);
    }

    [Category("BVH Debug Display")]
    [Description("Selects all, leaf-only, or internal-only BVH node overlays.")]
    public MathBvhDebugNodeVisibility NodeVisibility
    {
        get => _nodeVisibility;
        set => SetField(ref _nodeVisibility, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws the stable, complete BVH topology base layer.")]
    public bool RenderBaseNodes
    {
        get => _renderBaseNodes;
        set => SetField(ref _renderBaseNodes, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws nodes visited by the current query as a final highlight layer.")]
    public bool RenderVisitedNodes
    {
        get => _renderVisitedNodes;
        set => SetField(ref _renderVisitedNodes, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws source geometry that intersects the query without being fully contained. BVH traversal-node display is unaffected.")]
    public bool RenderIntersectedGeometry
    {
        get => _renderIntersectedGeometry;
        set => SetField(ref _renderIntersectedGeometry, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws source AABBs or the source triangle wireframe.")]
    public bool RenderSourceGeometry
    {
        get => _renderSourceGeometry;
        set => SetField(ref _renderSourceGeometry, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws the animated box, sphere, frustum, or finite raycast query.")]
    public bool RenderQuery
    {
        get => _renderQuery;
        set => SetField(ref _renderQuery, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws the point, line, and triangle collections returned by mesh shape queries.")]
    public bool RenderQueryResults
    {
        get => _renderQueryResults;
        set => SetField(ref _renderQueryResults, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws the closest mesh-query hit point when one exists.")]
    public bool RenderHitMarker
    {
        get => _renderHitMarker;
        set => SetField(ref _renderHitMarker, value);
    }

    [Category("BVH Debug Display")]
    [Description("Draws the ready/pass/fail sphere above the rig.")]
    public bool ShowValidationMarker
    {
        get => _renderValidationMarker;
        set => SetField(ref _renderValidationMarker, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 LeafNodeColor
    {
        get => _leafNodeColor;
        set => SetField(ref _leafNodeColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 InternalNodeColor
    {
        get => _internalNodeColor;
        set => SetField(ref _internalNodeColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 VisitedNodeColor
    {
        get => _visitedNodeColor;
        set => SetField(ref _visitedNodeColor, value);
    }

    [Category("BVH Debug Colors")]
    [Description("Color for source points, lines, triangles, or scene AABBs disjoint from the query.")]
    public ColorF4 DisjointGeometryColor
    {
        get => _disjointGeometryColor;
        set => SetField(ref _disjointGeometryColor, value);
    }

    [Category("BVH Debug Colors")]
    [Description("Color for source geometry that overlaps the query without being fully contained.")]
    public ColorF4 IntersectedGeometryColor
    {
        get => _intersectedGeometryColor;
        set => SetField(ref _intersectedGeometryColor, value);
    }

    [Category("BVH Debug Colors")]
    [Description("Color for source geometry fully contained by the query.")]
    public ColorF4 ContainedGeometryColor
    {
        get => _containedGeometryColor;
        set => SetField(ref _containedGeometryColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 QueryColor
    {
        get => _queryColor;
        set => SetField(ref _queryColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 QueryFailureColor
    {
        get => _queryFailureColor;
        set => SetField(ref _queryFailureColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 HitMarkerColor
    {
        get => _hitMarkerColor;
        set => SetField(ref _hitMarkerColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 ValidationPassedColor
    {
        get => _validationPassedColor;
        set => SetField(ref _validationPassedColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 ValidationPendingColor
    {
        get => _validationPendingColor;
        set => SetField(ref _validationPendingColor, value);
    }

    [Category("BVH Debug Colors")]
    public ColorF4 ValidationFailedColor
    {
        get => _validationFailedColor;
        set => SetField(ref _validationFailedColor, value);
    }

    [Category("BVH GPU Debug Display")]
    [Description("Maximum GPU BVH nodes emitted into the debug-line buffer.")]
    public int MaxDebugNodes
    {
        get => _maxDebugNodes;
        set => SetField(ref _maxDebugNodes, Math.Clamp(value, 1, MaxConfigurableDebugNodes));
    }

    [Category("BVH Debug Display")]
    [Description("World-space width of CPU and GPU base-node lines.")]
    public float BaseNodeLineWidth
    {
        get => _baseNodeLineWidth;
        set => SetField(ref _baseNodeLineWidth, Math.Clamp(value, 0.0001f, 0.02f));
    }

    [Category("BVH Debug Display")]
    [Description("World-space width of CPU and GPU visited-node highlight lines.")]
    public float VisitedNodeLineWidth
    {
        get => _visitedNodeLineWidth;
        set => SetField(ref _visitedNodeLineWidth, Math.Clamp(value, 0.0001f, 0.02f));
    }

    private bool IsMeshMode
        => Mode is MathBvhTestMode.LegacyCpuMesh or MathBvhTestMode.GpuMesh;

    private bool IsGpuMode
        => Mode is MathBvhTestMode.GpuScene or MathBvhTestMode.GpuMesh;

    private bool ShouldRenderNode(bool isLeaf)
        => NodeVisibility switch
        {
            MathBvhDebugNodeVisibility.LeavesOnly => isLeaf,
            MathBvhDebugNodeVisibility.InternalOnly => !isLeaf,
            _ => true,
        };

    private uint GetGpuNodeShowFilter()
        => NodeVisibility switch
        {
            MathBvhDebugNodeVisibility.LeavesOnly => 1u,
            MathBvhDebugNodeVisibility.InternalOnly => 2u,
            _ => 0u,
        };

    private uint GetMeshQueryPrimitiveMask()
    {
        if (QueryShape == MathBvhQueryShape.Raycast)
            return 0b100u;

        uint mask = 0u;
        if (QueryPoints)
            mask |= 0b001u;
        if (QueryLines)
            mask |= 0b010u;
        if (QueryTriangles)
            mask |= 0b100u;
        return mask;
    }

    private void ApplyModeDebugDefaults(MathBvhTestMode mode)
    {
        if (mode is MathBvhTestMode.CpuScene or MathBvhTestMode.GpuScene)
        {
            QueryShape = MathBvhQueryShape.Box;
            VisitedNodeColor = new ColorF4(0.18f, 0.82f, 1.0f, 1.0f);
            DisjointGeometryColor = new ColorF4(0.48f, 0.50f, 0.54f, 0.55f);
            IntersectedGeometryColor = new ColorF4(1.0f, 0.18f, 0.82f, 1.0f);
            ContainedGeometryColor = new ColorF4(0.15f, 1.0f, 0.35f, 0.95f);
            QueryColor = new ColorF4(1.0f, 0.82f, 0.12f, 1.0f);
            VisitedNodeLineWidth = 0.0022f;
            return;
        }

        QueryShape = MathBvhQueryShape.Raycast;
        VisitedNodeColor = new ColorF4(1.0f, 0.78f, 0.08f, 1.0f);
        DisjointGeometryColor = new ColorF4(0.48f, 0.50f, 0.54f, 0.55f);
        IntersectedGeometryColor = new ColorF4(1.0f, 0.18f, 0.82f, 1.0f);
        ContainedGeometryColor = new ColorF4(0.15f, 1.0f, 0.35f, 0.95f);
        QueryColor = ColorF4.LightGreen;
        VisitedNodeLineWidth = 0.0025f;
    }

    internal void RegisterDebugControls(CustomUIComponent customUi)
    {
        customUi.Name = $"{Mode} BVH Debug Controls";
        customUi.AddBoolField(
            "Debug Rendering",
            () => DebugRenderEnabled,
            value => DebugRenderEnabled = value,
            "Master switch for this rig's debug display; the workload continues running when disabled.");
        customUi.AddEnumField(
            "Node Visibility",
            () => NodeVisibility,
            value => NodeVisibility = value,
            "Draw all BVH nodes, leaf nodes only, or internal nodes only.");
        customUi.AddEnumField(
            "Query Shape",
            () => QueryShape,
            value => QueryShape = value,
            "Use the same animated raycast, box, sphere, or frustum query on the paired CPU/GPU BVH.");
        if (IsMeshMode)
        {
            customUi.AddBoolField("Query Points", () => QueryPoints, value => QueryPoints = value);
            customUi.AddBoolField("Query Lines", () => QueryLines, value => QueryLines = value);
            customUi.AddBoolField("Query Triangles", () => QueryTriangles, value => QueryTriangles = value);
        }
        customUi.AddBoolField("Show Base Nodes", () => RenderBaseNodes, value => RenderBaseNodes = value);
        customUi.AddBoolField("Show Visited Nodes", () => RenderVisitedNodes, value => RenderVisitedNodes = value);
        customUi.AddBoolField(
            "Show Intersected Geometry",
            () => RenderIntersectedGeometry,
            value => RenderIntersectedGeometry = value,
            "Toggle magenta source geometry that intersects, but is not fully contained by, the query shape.");
        customUi.AddBoolField("Show Source Geometry", () => RenderSourceGeometry, value => RenderSourceGeometry = value);
        customUi.AddBoolField("Show Query", () => RenderQuery, value => RenderQuery = value);
        if (IsMeshMode)
            customUi.AddBoolField("Show Query Results", () => RenderQueryResults, value => RenderQueryResults = value);
        if (IsMeshMode)
            customUi.AddBoolField("Show Hit Marker", () => RenderHitMarker, value => RenderHitMarker = value);
        customUi.AddBoolField("Show Validation Marker", () => ShowValidationMarker, value => ShowValidationMarker = value);

        if (IsGpuMode)
        {
            customUi.AddFloatField(
                "Maximum Debug Nodes",
                () => MaxDebugNodes,
                value => MaxDebugNodes = (int)MathF.Round(value),
                1.0f,
                MaxConfigurableDebugNodes,
                1.0f,
                "%.0f");
            customUi.AddFloatField(
                "Base Node Line Width",
                () => BaseNodeLineWidth,
                value => BaseNodeLineWidth = value,
                0.0001f,
                0.02f,
                0.0001f,
                "%.4f");
            customUi.AddFloatField(
                "Visited Node Line Width",
                () => VisitedNodeLineWidth,
                value => VisitedNodeLineWidth = value,
                0.0001f,
                0.02f,
                0.0001f,
                "%.4f");
        }

        customUi.AddColorField("Leaf Node Color", () => LeafNodeColor, value => LeafNodeColor = value, true);
        customUi.AddColorField("Internal Node Color", () => InternalNodeColor, value => InternalNodeColor = value, true);
        customUi.AddColorField("Visited Node Color", () => VisitedNodeColor, value => VisitedNodeColor = value, true);
        customUi.AddColorField("Disjoint Geometry Color", () => DisjointGeometryColor, value => DisjointGeometryColor = value, true);
        customUi.AddColorField("Intersected Geometry Color", () => IntersectedGeometryColor, value => IntersectedGeometryColor = value, true);
        customUi.AddColorField("Contained Geometry Color", () => ContainedGeometryColor, value => ContainedGeometryColor = value, true);
        customUi.AddColorField("Query Color", () => QueryColor, value => QueryColor = value, true);
        if (IsMeshMode)
        {
            customUi.AddColorField("Query Failure Color", () => QueryFailureColor, value => QueryFailureColor = value, true);
            customUi.AddColorField("Hit Marker Color", () => HitMarkerColor, value => HitMarkerColor = value, true);
        }
        customUi.AddColorField("Validation Passed Color", () => ValidationPassedColor, value => ValidationPassedColor = value, true);
        customUi.AddColorField("Validation Pending Color", () => ValidationPendingColor, value => ValidationPendingColor = value, true);
        customUi.AddColorField("Validation Failed Color", () => ValidationFailedColor, value => ValidationFailedColor = value, true);
    }

    internal void CopySettingsFrom(MathBvhTestComponent source, bool includeDebugDisplays)
    {
        QueryShape = source.QueryShape;
        QueryPoints = source.QueryPoints;
        QueryLines = source.QueryLines;
        QueryTriangles = source.QueryTriangles;
        NodeVisibility = source.NodeVisibility;
        RenderBaseNodes = source.RenderBaseNodes;
        RenderVisitedNodes = source.RenderVisitedNodes;
        RenderIntersectedGeometry = source.RenderIntersectedGeometry;
        RenderSourceGeometry = source.RenderSourceGeometry;
        RenderQuery = source.RenderQuery;
        RenderQueryResults = source.RenderQueryResults;
        RenderHitMarker = source.RenderHitMarker;
        ShowValidationMarker = source.ShowValidationMarker;
        LeafNodeColor = source.LeafNodeColor;
        InternalNodeColor = source.InternalNodeColor;
        VisitedNodeColor = source.VisitedNodeColor;
        DisjointGeometryColor = source.DisjointGeometryColor;
        IntersectedGeometryColor = source.IntersectedGeometryColor;
        ContainedGeometryColor = source.ContainedGeometryColor;
        QueryColor = source.QueryColor;
        QueryFailureColor = source.QueryFailureColor;
        HitMarkerColor = source.HitMarkerColor;
        ValidationPassedColor = source.ValidationPassedColor;
        ValidationPendingColor = source.ValidationPendingColor;
        ValidationFailedColor = source.ValidationFailedColor;
        MaxDebugNodes = source.MaxDebugNodes;
        BaseNodeLineWidth = source.BaseNodeLineWidth;
        VisitedNodeLineWidth = source.VisitedNodeLineWidth;
        DebugRenderEnabled = includeDebugDisplays && source.DebugRenderEnabled;
    }
}
