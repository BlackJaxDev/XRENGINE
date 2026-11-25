using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Keeps an asset menu tied to the exact visible row that requested it.</summary>
internal sealed class NativeAssetMenuRequest
{
    private readonly XRComponent _owner;
    private readonly UIVirtualTableRowComponent _row;
    private readonly UIListTransform _list;
    private readonly IVirtualListProvider _provider;
    private readonly ulong _providerIncarnation;
    private readonly ulong _sourceRevision;
    private readonly ObjectConstructionIdentity[] _identities;

    internal NativeAssetMenuRequest(XRComponent owner, UIVirtualTableRowComponent row,
        UIListTransform list, IVirtualListProvider provider, UIVirtualRowLease lease,
        UICanvasTransform canvas, IUICanvasInputSource input, Vector2 position, string path)
    {
        _owner = owner;
        _row = row;
        _list = list;
        _provider = provider;
        _providerIncarnation = list.VirtualProviderIncarnation;
        _sourceRevision = provider.Revision;
        Lease = lease;
        Canvas = canvas;
        Input = input;
        Position = position;
        Path = path;
        _identities =
        [
            ObjectConstructionIdentity.Capture(owner),
            ObjectConstructionIdentity.Capture(owner.SceneNode),
            ObjectConstructionIdentity.Capture(owner.Transform),
            ObjectConstructionIdentity.Capture(row),
            ObjectConstructionIdentity.Capture(row.SceneNode),
            ObjectConstructionIdentity.Capture(row.Transform),
            ObjectConstructionIdentity.Capture(list),
            ObjectConstructionIdentity.Capture(list.SceneNode!),
            ObjectConstructionIdentity.Capture(canvas),
            ObjectConstructionIdentity.Capture(canvas.SceneNode!),
        ];
        InputIdentity = input is XRObjectBase inputObject ? ObjectConstructionIdentity.Capture(inputObject) : null;
    }

    internal UIVirtualRowLease Lease { get; }
    internal UICanvasTransform Canvas { get; }
    internal IUICanvasInputSource Input { get; }
    internal Vector2 Position { get; }
    internal string Path { get; }
    internal bool AwaitingShow { get; set; } = true;
    private ObjectConstructionIdentity? InputIdentity { get; }

    internal bool IsCurrent()
    {
        if (!IsOwnerCurrent()) return false;
        for (int index = 3; index < 8; index++)
            if (!Live(_identities[index])) return false;
        if (!_row.CanInteract ||
            !ReferenceEquals(_row.SceneNode, _identities[4].Target) ||
            !ReferenceEquals(_row.Transform, _identities[5].Target) ||
            !ReferenceEquals(_list.SceneNode, _identities[7].Target) ||
            !ReferenceEquals(_list.SceneNode?.Transform, _list) ||
            !ReferenceEquals(_list.VirtualProvider, _provider) ||
            _list.VirtualProviderIncarnation != _providerIncarnation || _provider.Revision != _sourceRevision ||
            !ReferenceEquals(_row.Transform.Parent, _list) ||
            !_list.TryGetVirtualRowLease(_row.BoundableTransform, out var lease) || lease != Lease ||
            _row.BoundItemId != Lease.ItemId)
            return false;
        for (var parent = _list.Parent; parent is not null; parent = parent.Parent)
            if (ReferenceEquals(parent, _owner.Transform)) return true;
        return false;
    }

    internal bool IsOwnerCurrent()
    {
        for (int index = 0; index < 3; index++)
            if (!Live(_identities[index])) return false;
        return Live(_identities[8]) && Live(_identities[9]) &&
            (InputIdentity is not { } inputIdentity || Live(inputIdentity)) &&
            _owner.IsActiveInHierarchy && ReferenceEquals(_owner.SceneNode, _identities[1].Target) &&
            ReferenceEquals(_owner.Transform, _identities[2].Target) &&
            ReferenceEquals(Canvas.SceneNode, _identities[9].Target) &&
            ReferenceEquals(Canvas.SceneNode?.Transform, Canvas) &&
            ReferenceEquals((_owner.Transform as UITransform)?.GetCanvasTransform(), Canvas) &&
            ReferenceEquals(Canvas.SceneNode?.GetComponent<UICanvasComponent>()?.GetInputComponent(), Input);
    }

    internal bool OwnsControl(UIComponent control)
    {
        if (!IsOwnerCurrent()) return false;
        for (var transform = control.Transform; transform is not null; transform = transform.Parent)
            if (ReferenceEquals(transform, _owner.Transform)) return true;
        return false;
    }

    private static bool Live(ObjectConstructionIdentity identity)
        => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued;
}
