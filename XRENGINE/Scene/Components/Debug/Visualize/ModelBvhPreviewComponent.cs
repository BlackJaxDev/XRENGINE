using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Compute;

namespace XREngine.Components;

/// <summary>
/// Draws the BVH bounds for every mesh owned by a sibling <see cref="ModelComponent"/>.
/// Only meshes that survive camera culling are visualized so the view stays uncluttered.
/// </summary>
[Serializable]
[HideInInspector]
[Obsolete("BVH preview is now available from the ModelComponent ImGui editor. This component remains for backward compatibility.")]
public sealed class ModelBvhPreviewComponent : DebugVisualize3DComponent
{
    private readonly GpuBvhDebugLineRenderer _gpuRenderer = new();

    private ModelComponent? _modelComponent;
    private bool _missingModelLogged;

    private ColorF4 _internalNodeColor = ColorF4.Cyan;
    public ColorF4 InternalNodeColor
    {
        get => _internalNodeColor;
        set => SetField(ref _internalNodeColor, value);
    }

    private ColorF4 _leafNodeColor = ColorF4.LightGreen;
    public ColorF4 LeafNodeColor
    {
        get => _leafNodeColor;
        set => SetField(ref _leafNodeColor, value);
    }

    private GpuBvhDebugNodeRenderMode _nodeRenderMode = GpuBvhDebugNodeRenderMode.HighlightLeafNodes;
    public GpuBvhDebugNodeRenderMode NodeRenderMode
    {
        get => _nodeRenderMode;
        set => SetField(ref _nodeRenderMode, value);
    }

    [Obsolete("Use NodeRenderMode instead.")]
    public bool HighlightLeafNodes
    {
        get => NodeRenderMode == GpuBvhDebugNodeRenderMode.HighlightLeafNodes;
        set => NodeRenderMode = value
            ? GpuBvhDebugNodeRenderMode.HighlightLeafNodes
            : GpuBvhDebugNodeRenderMode.AllNodesSame;
    }

    private bool _onlyUpdateBvhOnRequest = true;
    public bool OnlyUpdateBvhOnRequest
    {
        get => _onlyUpdateBvhOnRequest;
        set => SetField(ref _onlyUpdateBvhOnRequest, value);
    }

    private bool _onlyRenderBvhOnRequest = true;
    public bool OnlyRenderBvhOnRequest
    {
        get => _onlyRenderBvhOnRequest;
        set => SetField(ref _onlyRenderBvhOnRequest, value);
    }

    [Obsolete("Use OnlyUpdateBvhOnRequest instead.")]
    public bool LiveSkinnedGpuPreview
    {
        get => !OnlyUpdateBvhOnRequest;
        set => OnlyUpdateBvhOnRequest = !value;
    }

    private float _lineWidth = 0.0015f;
    public float LineWidth
    {
        get => _lineWidth;
        set => SetField(ref _lineWidth, MathF.Max(0.0001f, value));
    }

    private int _maxGpuNodes = 16384;
    public int MaxGpuNodes
    {
        get => _maxGpuNodes;
        set => SetField(ref _maxGpuNodes, Math.Max(1, value));
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _missingModelLogged = false;
        ResolveModelComponent();
    }

    protected override void Render()
    {
        if (Engine.Rendering.State.IsShadowPass)
            return;

        var model = ResolveModelComponent();
        if (model is null)
            return;

        var camera = Engine.Rendering.State.RenderingCamera;
        IVolume? frustum = camera?.WorldFrustum();

        foreach (RenderableMesh mesh in model.Meshes)
        {
            if (!MeshIsVisible(mesh, frustum))
                continue;

            RenderMeshTree(mesh);
        }
    }

    private ModelComponent? ResolveModelComponent()
    {
        if (_modelComponent is { } cached && cached.SceneNode == SceneNode)
            return cached;

        if (TryGetSiblingComponent(out ModelComponent? sibling) && sibling is not null)
        {
            _modelComponent = sibling;
            _missingModelLogged = false;
            return sibling;
        }

        if (!_missingModelLogged)
        {
            Debug.LogWarning($"{nameof(ModelBvhPreviewComponent)} on '{SceneNode?.Name ?? "<unnamed>"}' requires a sibling {nameof(ModelComponent)}.");
            _missingModelLogged = true;
        }

        _modelComponent = null;
        return null;
    }

    private bool MeshIsVisible(RenderableMesh mesh, IVolume? frustum)
    {
        if (World is null)
            return false;

        var info = mesh.RenderInfo;
        if (info is null || !mesh.Component.IsActiveInHierarchy)
            return false;

        if (!info.IsVisible || !info.ShouldRender)
            return false;

        if (!ReferenceEquals(info.WorldInstance, World))
            return false;

        if (frustum is not null && !info.Intersects(frustum, false))
            return false;

        return mesh.CurrentLODRenderer?.Mesh is not null;
    }

    private void RenderMeshTree(RenderableMesh mesh)
    {
        bool requestActive = RequestGpuMeshBvhRefreshIfCursorIntersects(mesh);
        if (mesh.IsSkinned && OnlyRenderBvhOnRequest && !requestActive)
            return;

        bool refreshRequested = mesh.HasGpuMeshBvhRefreshRequest;
        bool realtimeSkinned = mesh.IsSkinned && (!OnlyUpdateBvhOnRequest || refreshRequested);
        if ((realtimeSkinned || refreshRequested || !mesh.HasCurrentGpuMeshBvh) &&
            !mesh.PrepareGpuMeshBvh(realtimeSkinned))
        {
            return;
        }

        if (refreshRequested)
            mesh.ClearGpuMeshBvhRefreshRequestIfPrepared();

        var gpuBvh = mesh.GpuMeshBvh;
        var nodeBuffer = gpuBvh?.BvhNodeBuffer;
        if (gpuBvh?.IsBvhReady != true || nodeBuffer is null)
            return;

        Vector4 internalColor = ToVector4(InternalNodeColor);
        Vector4 leafColor = NodeRenderMode is GpuBvhDebugNodeRenderMode.HighlightLeafNodes or GpuBvhDebugNodeRenderMode.LeafNodesOnly
            ? ToVector4(LeafNodeColor)
            : internalColor;

        _gpuRenderer.Queue(
            nodeBuffer,
            gpuBvh.BvhNodeCount,
            gpuBvh.LocalToWorldMatrix,
            (uint)Math.Max(1, MaxGpuNodes),
            LineWidth,
            leafColor,
            internalColor,
            GetBvhNodeShowFilter(NodeRenderMode));
    }

    private bool RequestGpuMeshBvhRefreshIfCursorIntersects(RenderableMesh mesh)
    {
        if (!mesh.IsSkinned)
            return false;

        var subMesh = mesh.CurrentLODRenderer?.SourceSubMeshAsset;
        if (subMesh is null || !subMesh.RealtimeGpuMeshBvhForSkinnedMeshes)
            return false;

        if (Engine.State.MainPlayer?.ControlledPawnComponent is not PawnComponent pawn)
            return false;

        Segment worldSegment = pawn.CursorPositionWorld;
        if (!TryGetCurrentHoverBounds(mesh, out AABB worldBounds) || !worldBounds.IsValid)
            return false;

        if (GeoUtil.Intersect.SegmentWithAABB(
                worldSegment.Start,
                worldSegment.End,
                worldBounds.Min,
                worldBounds.Max,
                out _,
                out _))
        {
            if (OnlyUpdateBvhOnRequest)
                mesh.RequestGpuMeshBvhRefresh();
            return true;
        }

        return false;
    }

    private static bool TryGetCurrentHoverBounds(RenderableMesh mesh, out AABB worldBounds)
        => mesh.TryGetGpuMeshBvhRequestWorldBounds(out worldBounds);

    private static Vector4 ToVector4(ColorF4 color)
        => new(color.R, color.G, color.B, color.A);

    private static uint GetBvhNodeShowFilter(GpuBvhDebugNodeRenderMode mode)
        => mode switch
        {
            GpuBvhDebugNodeRenderMode.LeafNodesOnly => 1u,
            GpuBvhDebugNodeRenderMode.InternalNodesOnly => 2u,
            _ => 0u,
        };

    protected override void OnDestroying()
    {
        _gpuRenderer.Dispose();
        base.OnDestroying();
    }
}
