using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.UI;

/// <summary>Tracks guarded popup leaves until one exact cleanup consumes them.</summary>
public sealed class UIContextMenuContentBuilder
{
    private readonly SceneNode _root;
    private readonly Func<bool> _isCurrent;
    private readonly AdoptedLeafRetirementReceipt _rootRetirement;
    private readonly List<AdoptedLeafRetirementReceipt> _leaves = [];
    private readonly List<UIContextMenuLeafConstruction> _failedConstructions = [];
    private bool _consumed;
    private Exception? _failure;

    internal UIContextMenuContentBuilder(SceneNode root, Func<bool> isCurrent)
    {
        _root = root;
        _isCurrent = isCurrent;
        _rootRetirement = new(root, root.Transform.Parent ??
            throw new InvalidOperationException("The popup content root has no parent."),
            Array.Empty<RuntimeComponentCreationReceipt>());
    }

    internal Exception? Failure => _failure;
    internal IReadOnlyList<AdoptedLeafRetirementReceipt> Leaves => _leaves;
    internal IReadOnlyList<UIContextMenuLeafConstruction> FailedConstructions => _failedConstructions;
    public SceneNode Root => _root;

    internal bool CanReleaseTransferredRoot(SceneNode root)
        => _failure is null && _failedConstructions.Count == 0 &&
            ReferenceEquals(root, _root) && _rootRetirement.HasCurrentLifetime &&
            !ReferenceEquals(root.Transform.Parent, _rootRetirement.Parent) &&
            !_rootRetirement.ParentChildren.Contains(root.Transform);

    internal void ReleaseTransferredRoot() => _consumed = true;

    /// <summary>Adds one guarded leaf to the content root or an earlier leaf.</summary>
    public SceneNode Add(SceneNode parent, string name, UIBoundableTransform transform,
        Action<UIContextMenuContentLeaf> configure)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(configure);
        if (_consumed || !_isCurrent() || !OwnsParent(parent))
            throw new InvalidOperationException("The popup content owner changed.");
        UIContextMenuLeafConstruction construction = new(parent,
            () => { if (_consumed || !_isCurrent() || !OwnsParent(parent))
                throw new InvalidOperationException("The popup content owner changed."); }, transform, name);
        bool captureAttempted = false;
        try
        {
            configure(new(construction));
            construction.Attach();
            captureAttempted = true;
            _leaves.Add(new(construction.Node, parent.Transform, construction.Attempts));
            return construction.Node;
        }
        catch (Exception original)
        {
            if (captureAttempted)
            {
                _failedConstructions.Add(construction);
                _failure ??= original;
            }
            else if (construction.OwnsAttachedNode())
            {
                try { _leaves.Add(new(construction.Node, parent.Transform, construction.Attempts)); }
                catch (Exception admissionFailure)
                {
                    _failedConstructions.Add(construction);
                    _failure ??= admissionFailure;
                }
            }
            else if (!construction.RetireFailedLeaf())
            {
                _failedConstructions.Add(construction);
                _failure ??= construction.Retirement.Failure;
            }
            throw;
        }
    }

    private bool OwnsParent(SceneNode parent)
    {
        if (!_rootRetirement.HasCurrentIdentity ||
            !ReferenceEquals(_root.Transform.Parent, _rootRetirement.Parent))
            return false;
        int remaining = _leaves.Count + 1;
        for (SceneNode? node = parent; !ReferenceEquals(node, _root); node = node?.Parent)
        {
            if (node is null || --remaining < 0) return false;
            AdoptedLeafRetirementReceipt? receipt = _leaves.Find(leaf => ReferenceEquals(leaf.Node, node));
            if (receipt is null || receipt.State != EOwnedHierarchyLifetimeState.Retained ||
                !receipt.HasCurrentIdentity || !ReferenceEquals(receipt.Transform.Parent, receipt.Parent))
                return false;
        }
        return true;
    }

    internal bool Retire()
    {
        if (_consumed) return _failure is null;
        _consumed = true;
        for (int index = _leaves.Count - 1; index >= 0; index--)
        {
            if (!HasRootAuthority())
                return CanReleaseTransferredRoot(_root) || RetainChangedRoot();
            AdoptedLeafRetirementReceipt receipt = _leaves[index];
            if (IsTransferred(receipt)) continue;
            if (!receipt.TryRetire())
            {
                _failure = receipt.Failure;
                return false;
            }
        }
        if (_failure is not null || _failedConstructions.Count != 0)
            return false;
        if (!HasRootAuthority())
            return CanReleaseTransferredRoot(_root) || RetainChangedRoot();
        if (!_rootRetirement.TryRetire())
        {
            _failure = _rootRetirement.Failure;
            return false;
        }
        return true;
    }

    private bool HasRootAuthority()
        => _rootRetirement.HasCurrentIdentity &&
           ReferenceEquals(_root.Transform.Parent, _rootRetirement.Parent);

    private bool RetainChangedRoot()
    {
        _failure ??= new InvalidOperationException("The popup content root changed ownership or lifetime.");
        return false;
    }

    private bool IsTransferred(AdoptedLeafRetirementReceipt receipt)
    {
        if (!receipt.HasCurrentLifetime) return false;
        if (!ReferenceEquals(receipt.Transform.Parent, receipt.Parent))
            return !receipt.ParentChildren.Contains(receipt.Transform);
        for (SceneNode? parent = receipt.Parent.SceneNode; parent is not null &&
            !ReferenceEquals(parent, _root); parent = parent.Parent)
            if (_leaves.Exists(leaf => ReferenceEquals(leaf.Node, parent) &&
                leaf.HasCurrentLifetime && !ReferenceEquals(leaf.Transform.Parent, leaf.Parent) &&
                !leaf.ParentChildren.Contains(leaf.Transform)))
                return true;
        return false;
    }
}
