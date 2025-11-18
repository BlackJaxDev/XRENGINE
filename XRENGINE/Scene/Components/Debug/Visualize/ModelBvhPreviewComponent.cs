using System.Collections.Generic;
using System.Numerics;
using SimpleScene.Util.ssBVH;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Draws the BVH bounds for every mesh owned by a sibling <see cref="ModelComponent"/>.
/// Only meshes that survive camera culling are visualized so the view stays uncluttered.
/// </summary>
[Serializable]
public sealed class ModelBvhPreviewComponent : DebugVisualize3DComponent
{
    private readonly Stack<BVHNode<Triangle>> _nodeStack = new();
    private readonly HashSet<XRMesh> _attemptedBvhBuilds = new();

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

    private bool _highlightLeafNodes = true;
    public bool HighlightLeafNodes
    {
        get => _highlightLeafNodes;
        set => SetField(ref _highlightLeafNodes, value);
    }

    private bool _cullNodesAgainstCamera = true;
    public bool CullNodesAgainstCamera
    {
        get => _cullNodesAgainstCamera;
        set => SetField(ref _cullNodesAgainstCamera, value);
    }

    protected internal override void OnComponentActivated()
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

            RenderMeshTree(mesh, frustum);
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

        if (info.WorldInstance != World)
            return false;

        if (frustum is not null && !info.Intersects(frustum, false))
            return false;

        return mesh.CurrentLODRenderer?.Mesh is not null;
    }

    private void RenderMeshTree(RenderableMesh mesh, IVolume? frustum)
    {
        bool skinned = mesh.IsSkinned;
        BVH<Triangle>? tree;
        bool worldSpace;

        if (skinned)
        {
            tree = mesh.GetSkinnedBvh();
            worldSpace = true;
        }
        else
        {
            var renderer = mesh.CurrentLODRenderer;
            var xrMesh = renderer?.Mesh;
            tree = xrMesh?.BVHTree;
            if (tree?._rootBVH is null && xrMesh is not null && _attemptedBvhBuilds.Add(xrMesh))
            {
                xrMesh.GenerateBVH();
                tree = xrMesh.BVHTree;
            }
            worldSpace = false;
        }

        var root = tree?._rootBVH;
        if (root is null)
            return;

        _nodeStack.Clear();
        _nodeStack.Push(root);

        Matrix4x4 meshMatrix = worldSpace ? Matrix4x4.Identity : GetMeshWorldMatrix(mesh);
        Matrix4x4 rotationScaleMatrix = meshMatrix;
        rotationScaleMatrix.Translation = Vector3.Zero;

        while (_nodeStack.Count > 0)
        {
            var node = _nodeStack.Pop();
            if (node is null)
                continue;

            AABB nodeBounds = node.box;
            AABB worldBounds = worldSpace ? nodeBounds : TransformAabb(nodeBounds, meshMatrix);
            if (CullNodesAgainstCamera && frustum is not null)
            {
                EContainment containment = frustum.ContainsAABB(worldBounds);
                if (containment == EContainment.Disjoint)
                    continue;
            }

            ColorF4 color = node.IsLeaf && HighlightLeafNodes ? LeafNodeColor : InternalNodeColor;
            if (worldSpace)
            {
                Engine.Rendering.Debug.RenderBox(nodeBounds.HalfExtents, nodeBounds.Center, Matrix4x4.Identity, false, color);
            }
            else
            {
                Vector3 worldCenter = Vector3.Transform(nodeBounds.Center, meshMatrix);
                Engine.Rendering.Debug.RenderBox(nodeBounds.HalfExtents, worldCenter, rotationScaleMatrix, false, color);
            }

            if (node.left is not null)
                _nodeStack.Push(node.left);
            if (node.right is not null)
                _nodeStack.Push(node.right);
        }
    }

    private static Matrix4x4 GetMeshWorldMatrix(RenderableMesh mesh)
        => mesh.Component.Transform.RenderMatrix;

    private static AABB TransformAabb(AABB localBounds, Matrix4x4 transform)
        => localBounds.Transformed(point => Vector3.Transform(point, transform));
}
