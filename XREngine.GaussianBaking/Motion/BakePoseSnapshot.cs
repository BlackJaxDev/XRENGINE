using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Models.Gaussian.Baking.Motion;

/// <summary>
/// Records the pose state of content before <see cref="BakeFrameSampler"/> plays clips on it, and puts that state
/// back after: the transforms, the blendshape weights, and the active state of the animation components that do not
/// play in the bake.
/// </summary>
/// <remarks>
/// The snapshot covers the content roots and the top node of each player that is not under a content root, so that
/// the sampler also puts back the transforms that such a player changes.
/// </remarks>
internal sealed class BakePoseSnapshot
{
    private readonly List<SceneNode> _roots = [];
    private readonly List<(TransformBase Transform, TransformState? FrameState, Matrix4x4 LocalMatrix, Matrix4x4 RenderMatrix, TransformDiagnosticInvalidationState Invalidation)> _transforms = [];
    private readonly List<(XRComponent Component, bool WasActive)> _animationComponents = [];
    private readonly List<(XRMeshRenderer Renderer, float[] Weights)> _blendshapeWeights = [];

    private BakePoseSnapshot()
    {
    }

    /// <summary>
    /// Gets the nodes whose hierarchies the snapshot covers: the content roots first, and then the top node of each
    /// player that is not under a content root.
    /// </summary>
    public IReadOnlyList<SceneNode> Roots => _roots;

    /// <summary>
    /// Records the state of the hierarchies of <see cref="Roots"/>. The animation clip components and state machine
    /// components that are not in <paramref name="players"/> are recorded so that they can be disabled.
    /// </summary>
    /// <param name="contentRoots">The root nodes of the content.</param>
    /// <param name="players">The components that play the clips.</param>
    public static BakePoseSnapshot Capture(IReadOnlyList<SceneNode> contentRoots, IReadOnlyList<AnimationClipComponent> players)
    {
        BakePoseSnapshot snapshot = new();
        for (int i = 0; i < contentRoots.Count; i++)
            snapshot.AddRoot(contentRoots[i]);
        for (int i = 0; i < players.Count; i++)
            snapshot.AddRoot(FindTop(players[i].SceneNode, contentRoots));

        foreach (SceneNode poseRoot in snapshot._roots)
        {
            snapshot.CaptureTransforms(poseRoot.Transform);
            poseRoot.IterateComponents(component =>
            {
                if ((component is AnimationClipComponent or AnimStateMachineComponent) && !Contains(players, component))
                    snapshot._animationComponents.Add((component, component.IsActive));
                if (component is RenderableComponent renderable)
                    snapshot.CaptureBlendshapeWeights(renderable);
            }, true);
        }

        return snapshot;
    }

    /// <summary>Disables every recorded animation component, so that only the players change the pose.</summary>
    public void DisableOtherAnimationComponents()
    {
        foreach ((XRComponent component, _) in _animationComponents)
            component.IsActive = false;
    }

    /// <summary>
    /// Puts back the active state of the animation components, then the blendshape weights, and then the transforms.
    /// Each step runs even when an earlier step fails.
    /// </summary>
    public void Restore()
    {
        try
        {
            for (int i = _animationComponents.Count - 1; i >= 0; i--)
                _animationComponents[i].Component.IsActive = _animationComponents[i].WasActive;
        }
        finally
        {
            try
            {
                RestoreBlendshapeWeights();
            }
            finally
            {
                RestoreTransforms();
            }
        }
    }

    /// <summary>
    /// Returns the content root that <paramref name="node"/> is under, or else the top node of the node's hierarchy.
    /// </summary>
    private static SceneNode FindTop(SceneNode node, IReadOnlyList<SceneNode> contentRoots)
    {
        SceneNode top = node;
        while (!IsContentRoot(top, contentRoots) && top.Parent is SceneNode parent)
            top = parent;
        return top;
    }

    private static bool IsContentRoot(SceneNode node, IReadOnlyList<SceneNode> contentRoots)
    {
        for (int i = 0; i < contentRoots.Count; i++)
        {
            if (ReferenceEquals(contentRoots[i], node))
                return true;
        }

        return false;
    }

    private void AddRoot(SceneNode root)
    {
        if (!_roots.Exists(existing => ReferenceEquals(existing, root)))
            _roots.Add(root);
    }

    private static bool Contains(IReadOnlyList<AnimationClipComponent> players, XRComponent component)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (ReferenceEquals(players[i], component))
                return true;
        }

        return false;
    }

    private void CaptureTransforms(TransformBase transform)
    {
        TransformDiagnosticInvalidationState invalidation = transform.CaptureDiagnosticInvalidationState();
        if (transform.IsLocalMatrixDirty)
            transform.RecalcLocal();

        _transforms.Add((
            transform,
            transform is Transform concrete ? concrete.FrameState : null,
            transform.LocalMatrix,
            transform.RenderMatrix,
            invalidation));

        foreach (TransformBase child in transform.Children)
        {
            if (child is not null)
                CaptureTransforms(child);
        }
    }

    private void CaptureBlendshapeWeights(RenderableComponent component)
    {
        foreach (RenderableMesh mesh in component.Meshes)
        {
            foreach (RenderableMesh.RenderableLOD lod in mesh.LODs)
            {
                XRMeshRenderer renderer = lod.Renderer;
                uint count = renderer.Mesh?.BlendshapeCount ?? 0u;
                if (count == 0u)
                    continue;

                float[] weights = new float[count];
                for (uint shape = 0u; shape < count; shape++)
                    weights[shape] = renderer.GetBlendshapeWeightNormalized(shape);
                _blendshapeWeights.Add((renderer, weights));
            }
        }
    }

    private void RestoreBlendshapeWeights()
    {
        foreach ((XRMeshRenderer renderer, float[] weights) in _blendshapeWeights)
        {
            for (uint shape = 0u; shape < weights.Length; shape++)
                renderer.SetBlendshapeWeightNormalized(shape, weights[shape]);
        }
    }

    /// <summary>
    /// Puts back the local state of each transform, calculates the world matrices again, and then puts back the
    /// render matrices and the dirty flags as they were before the bake.
    /// </summary>
    private void RestoreTransforms()
    {
        foreach ((TransformBase transform, TransformState? frameState, Matrix4x4 localMatrix, _, _) in _transforms)
        {
            if (transform is Transform concrete && frameState.HasValue)
                concrete.SetFrameState(frameState.Value);
            else
                transform.DeriveLocalMatrix(localMatrix);
        }

        foreach (SceneNode root in _roots)
            root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);

        foreach ((TransformBase transform, _, _, Matrix4x4 renderMatrix, TransformDiagnosticInvalidationState invalidation) in _transforms)
        {
            if (!transform.RenderMatrix.Equals(renderMatrix))
                transform.SetRenderMatrixImmediate(renderMatrix);
            transform.RestoreDiagnosticInvalidationState(invalidation);
        }
    }
}
