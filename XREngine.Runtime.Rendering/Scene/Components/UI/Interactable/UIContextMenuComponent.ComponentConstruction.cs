using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private readonly List<RuntimeComponentCreationReceipt> _failedComponentCreations = [];
    private readonly HashSet<XRComponent> _incompleteComponentCreations = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyList<RuntimeComponentCreationReceipt> FailedComponentCreations => _failedComponentCreations;

    /// <summary>Builds a component on an existing owner without claiming the owner's graph.</summary>
    private T? BuildMenuComponent<T>(SceneNode node, ulong showRevision, Func<T> factory,
        Action<T, Action<Action>> configure, Func<bool>? ownsSource = null) where T : XRComponent
    {
        InvalidOperationException? cancellation = null;
        RuntimeComponentCreationReceipt? receipt = null;
        bool creationCompleted = false;
        bool RequireShow()
        {
            if (!IsCurrentShow(showRevision) ||
                (!ReferenceEquals(node, SceneNode) && !OwnsConstructionParent(node)) ||
                ownsSource is not null && !ownsSource())
                throw new InvalidOperationException("The menu component source changed during construction.");
            // Finish an already committed insertion before honoring a queued request.
            // Ownership and source checks still apply at every creation boundary.
            if (_pendingTransition is not null && (creationCompleted || receipt?.IsInserted != true))
                throw cancellation ??= new InvalidOperationException("Menu component construction was superseded.");
            return true;
        }
        try
        {
            RequireShow();
            receipt = new(node, RequireShow);
            T component = node.AddComponentGuarded(factory, receipt) ??
                throw new InvalidOperationException("A menu component factory returned no component.");
            creationCompleted = true;
            receipt.Validate();
            void Write(Action write)
            {
                receipt.Validate();
                write();
                receipt.Validate();
            }
            configure(component, Write);
            receipt.Validate();
            return component;
        }
        catch (Exception failure)
        {
            if (receipt is not null)
            {
                receipt.RecordFailure(failure);
                bool aborted = receipt.Component is null || receipt.TryAbortUnchangedComponent();
                if (!aborted)
                {
                    if (ReferenceEquals(failure, cancellation) && creationCompleted)
                    {
                        try
                        {
                            receipt.RequireCommittedInsertion();
                            receipt.ValidateIntrinsicOwner();
                            return null;
                        }
                        catch (Exception ownershipFailure)
                        {
                            _failedComponentCreations.Add(receipt);
                            throw new AggregateException("Cancelled menu component retains changed ownership.",
                                failure, ownershipFailure);
                        }
                    }
                    _failedComponentCreations.Add(receipt);
                    if (!creationCompleted && receipt.Component is { } incomplete)
                        _incompleteComponentCreations.Add(incomplete);
                    if (receipt.CleanupFailure is { } cleanupFailure)
                        throw new AggregateException("Menu component creation and cleanup failed.", failure, cleanupFailure);
                    if (ReferenceEquals(failure, cancellation))
                        throw new InvalidOperationException("Cancelled menu component could not be aborted.", failure);
                }
            }
            if (ReferenceEquals(failure, cancellation))
                return null;
            throw;
        }
    }

    private void BuildVirtualMenuScroll(SceneNode node, UIListTransform list, ulong showRevision)
    {
        IVirtualListProvider provider = _virtualProvider!;
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ObjectConstructionIdentity listIdentity = ObjectConstructionIdentity.Capture(list);
        IVirtualListProvider? priorProvider = list.VirtualProvider;
        ulong priorIncarnation = list.VirtualProviderIncarnation;
        ulong targetIncarnation = ReferenceEquals(priorProvider, provider)
            ? priorIncarnation : unchecked(priorIncarnation + 1);
        bool OwnsList(IVirtualListProvider? expectedProvider, ulong incarnation)
            => nodeIdentity.IsCurrent && listIdentity.IsCurrent &&
                ReferenceEquals(node.Transform, list) && OwnsConstructionParent(node) &&
                ReferenceEquals(_virtualProvider, provider) &&
                ReferenceEquals(list.VirtualProvider, expectedProvider) &&
                list.VirtualProviderIncarnation == incarnation;
        bool ContinueSetup(IVirtualListProvider? expectedProvider, ulong incarnation)
        {
            if (!IsCurrentShow(showRevision) || !OwnsList(expectedProvider, incarnation))
                throw new InvalidOperationException("The virtual menu source changed during setup.");
            return _pendingTransition is null;
        }
        if (!ContinueSetup(priorProvider, priorIncarnation)) return;
        list.ItemSize = _itemHeight;
        if (!ContinueSetup(priorProvider, priorIncarnation)) return;
        list.VirtualOverscan = 2;
        if (!ContinueSetup(priorProvider, priorIncarnation)) return;
        list.VirtualProvider = provider;
        if (!ContinueSetup(provider, targetIncarnation)) return;
        _virtualList = list;
        BuildMenuComponent(node, showRevision, () => new UIVirtualPopupScrollComponent(),
            (scroll, write) => write(() => scroll.List = list),
            () => ReferenceEquals(_virtualList, list) && OwnsList(provider, targetIncarnation));
    }
}
