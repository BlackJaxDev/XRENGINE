using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

/// <summary>Owns only one nested collection pane construction and its exact scene objects.</summary>
internal sealed class EditorNestedListPaneAttempt(SceneNode root, SceneNode container,
    ObjectConstructionIdentity containerIdentity, TransformBase containerTransform,
    ObjectConstructionIdentity containerTransformIdentity,
    Action<DetachedLeafRetirementReceipt>? retainFailedLeaf = null)
{
    private readonly ObjectConstructionIdentity _rootIdentity = ObjectConstructionIdentity.Capture(root);
    private readonly TransformBase _initialRootTransform = root.Transform;
    private readonly ObjectConstructionIdentity _initialRootTransformIdentity =
        ObjectConstructionIdentity.Capture(root.Transform);
    private readonly ObjectConstructionIdentity _containerIdentity = containerIdentity;
    private readonly TransformBase _containerTransform = containerTransform;
    private readonly ObjectConstructionIdentity _containerTransformIdentity = containerTransformIdentity;
    private UIListTransform? _layout;
    private ObjectConstructionIdentity? _layoutIdentity;
    private UIListTransform? _unattachedLayout;
    private ObjectConstructionIdentity? _unattachedLayoutIdentity;
    private DetachedLeafRetirementReceipt? _unattachedLeafReceipt;
    private readonly Action<DetachedLeafRetirementReceipt>? _retainFailedLeaf = retainFailedLeaf;
    private SceneNode? _detail;
    private ObjectConstructionIdentity? _detailIdentity;
    private TransformBase? _detailTransform;
    private ObjectConstructionIdentity? _detailTransformIdentity;
    private InspectorListDetailRefreshComponent? _lifetime;
    private ObjectConstructionIdentity? _lifetimeIdentity;

    public SceneNode Root { get; } = root;
    public SceneNode Container { get; } = container;
    public bool IsUnattachedRootOwned => Live(_rootIdentity) && Live(_containerIdentity) &&
        Live(_containerTransformIdentity) && ReferenceEquals(Container.Transform, _containerTransform) &&
        HasOwnedUnattachedRoot() && !Root.IsDestroyed && !Root.IsDestroyQueued &&
        !Container.IsDestroyed && !Container.IsDestroyQueued;

    private bool HasOwnedUnattachedRoot()
        => _unattachedLayout is { } layout && _unattachedLeafReceipt is not null &&
            _unattachedLayoutIdentity is { } identity && Live(identity) &&
            ReferenceEquals(Root.Transform, layout) && layout.Parent is null &&
            !Root.IsDestroyed && !Root.IsDestroyQueued;

    public bool SetUnattachedLayout(UIListTransform layout)
    {
        if (!Live(_rootIdentity) ||
            !ReferenceEquals(Root.Transform, layout) || layout.IsDestroyed || layout.IsDestroyQueued ||
            layout.Parent is not null || Root.IsDestroyed || Root.IsDestroyQueued)
            return false;
        try
        {
            DetachedLeafRetirementReceipt leaf = new(Root);
            if (!Live(_rootIdentity) || !ReferenceEquals(Root.Transform, layout) ||
                layout.IsDestroyed || layout.IsDestroyQueued || layout.Parent is not null)
                return false;
            _unattachedLayout = layout;
            _unattachedLayoutIdentity = ObjectConstructionIdentity.Capture(layout);
            _unattachedLeafReceipt = leaf;
            return HasOwnedUnattachedRoot();
        }
        catch (Exception error)
        {
            Debug.LogException(error, "Could not capture the detached nested-pane root.");
            return false;
        }
    }
    public bool IsDirectChildOwned => Live(_rootIdentity) && Live(_containerIdentity) &&
        Live(_containerTransformIdentity) && ReferenceEquals(Container.Transform, _containerTransform) &&
        (_layout is { } layout
            ? _layoutIdentity is { } layoutIdentity && Live(layoutIdentity) && ReferenceEquals(Root.Transform, layout)
            : _unattachedLayout is { } unattachedLayout
                ? _unattachedLayoutIdentity is { } unattachedIdentity && Live(unattachedIdentity) &&
                    ReferenceEquals(Root.Transform, unattachedLayout)
            : Live(_initialRootTransformIdentity) && ReferenceEquals(Root.Transform, _initialRootTransform)) &&
        ReferenceEquals(Root.Transform.Parent, _containerTransform);

    public bool IsCurrent => IsDirectChildOwned && _layout is { } layout &&
        _layoutIdentity is { } layoutIdentity && Live(layoutIdentity) &&
        ReferenceEquals(Root.Transform, layout) &&
        !Root.IsDestroyed && !Root.IsDestroyQueued &&
        !Container.IsDestroyed && !Container.IsDestroyQueued;

    public UIListTransform? SetLayout(UIListTransform layout)
    {
        if (!ReferenceEquals(_unattachedLayout, layout) || _unattachedLayoutIdentity is not { } unattachedIdentity ||
            !Live(unattachedIdentity) || !Live(_rootIdentity) || !Live(_containerIdentity) ||
            !Live(_containerTransformIdentity) || !ReferenceEquals(Container.Transform, _containerTransform) ||
            !ReferenceEquals(Root.Transform.Parent, _containerTransform) ||
            !ReferenceEquals(Root.Transform, layout) ||
            layout.IsDestroyed || layout.IsDestroyQueued)
            return null;
        _layout = layout;
        _layoutIdentity = ObjectConstructionIdentity.Capture(layout);
        return IsCurrent ? layout : null;
    }

    public bool OwnsLayout(UIListTransform layout)
        => IsCurrent && ReferenceEquals(_layout, layout);

    public bool SetDetail(SceneNode detail)
    {
        if (!IsCurrent || !ReferenceEquals(detail.Transform.Parent, _layout))
            return false;
        _detail = detail;
        _detailIdentity = ObjectConstructionIdentity.Capture(detail);
        _detailTransform = detail.Transform;
        _detailTransformIdentity = ObjectConstructionIdentity.Capture(_detailTransform);
        return IsCurrent && OwnsDetail(detail);
    }

    public bool OwnsDetail(SceneNode detail)
        => IsCurrent && ReferenceEquals(_detail, detail) && _detailIdentity is { } identity &&
            Live(identity) && _detailTransform is { } transform &&
            _detailTransformIdentity is { } transformIdentity && Live(transformIdentity) &&
            ReferenceEquals(detail.Transform, transform) && ReferenceEquals(transform.Parent, _layout) &&
            !detail.IsDestroyed && !detail.IsDestroyQueued;

    public bool IsDetailCurrent => _detail is { } detail && OwnsDetail(detail);

    public bool SetLifetime(InspectorListDetailRefreshComponent lifetime)
    {
        if (_detail is null || !OwnsDetail(_detail) ||
            !ReferenceEquals(lifetime.SceneNode, _detail))
            return false;
        _lifetime = lifetime;
        _lifetimeIdentity = ObjectConstructionIdentity.Capture(lifetime);
        return OwnsLifetime(lifetime);
    }

    public InspectorListDetailRefreshComponent? Lifetime
        => _lifetime is { } lifetime && OwnsLifetime(lifetime) ? lifetime : null;

    public bool OwnsLifetime(InspectorListDetailRefreshComponent lifetime)
        => _detail is { } detail && OwnsDetail(detail) &&
            ReferenceEquals(_lifetime, lifetime) && _lifetimeIdentity is { } identity &&
            Live(identity) && ReferenceEquals(lifetime.SceneNode, detail) &&
            detail.Components.Contains(lifetime) && !lifetime.IsDestroyed && !lifetime.IsDestroyQueued;

    public void RetireLifetimeIfOwned()
    {
        InspectorListDetailRefreshComponent? lifetime = Lifetime;
        if (lifetime is null) return;
        try { lifetime.Retire(); }
        catch (Exception error) { Debug.LogException(error, "Could not retire nested list detail lifetime."); }
    }

    public void DestroyRootIfOwned()
    {
        if (Root.IsDestroyed || Root.IsDestroyQueued) return;
        if (HasOwnedUnattachedRoot())
        {
            DetachedLeafRetirementReceipt receipt = _unattachedLeafReceipt!;
            if (receipt.TryRetire()) return;
            _retainFailedLeaf?.Invoke(receipt);
            Debug.LogException(receipt.Failure ?? new InvalidOperationException(
                "The detached nested-pane leaf could not be retired."),
                "Could not retire an unattached nested detail root.");
            return;
        }
        if (!IsDirectChildOwned) return;
        try { Root.Destroy(false); }
        catch (Exception error) { Debug.LogException(error, "Could not destroy nested list detail."); }
    }

    private static bool Live(ObjectConstructionIdentity identity)
        => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued;
}
