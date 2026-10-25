using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

/// <summary>Keeps a rename draft bound to its field and asset path across row refresh.</summary>
internal sealed class NativeAssetRenameDraft
{
    private readonly NativeAssetMenuRequest _request;
    private readonly ObjectConstructionIdentity _field;
    private readonly ObjectConstructionIdentity _node;
    private readonly ObjectConstructionIdentity _transform;

    internal NativeAssetRenameDraft(NativeAssetMenuRequest request, UITextInputComponent field)
    {
        _request = request;
        _field = ObjectConstructionIdentity.Capture(field);
        _node = ObjectConstructionIdentity.Capture(field.SceneNode);
        _transform = ObjectConstructionIdentity.Capture(field.Transform);
    }

    internal bool CanSubmit(UITextInputComponent field, string? selectedPath, IReadOnlyDictionary<ulong, string> paths)
        => Live(_field) && Live(_node) && Live(_transform) && ReferenceEquals(field, _field.Target) &&
            ReferenceEquals(field.SceneNode, _node.Target) && ReferenceEquals(field.Transform, _transform.Target) &&
            field.CanInteract && _request.OwnsControl(field) && selectedPath is not null &&
            NativeAssetPathPolicy.PathsEqual(selectedPath, _request.Path) &&
            paths.TryGetValue(_request.Lease.ItemId, out string? currentPath) &&
            NativeAssetPathPolicy.PathsEqual(currentPath, _request.Path);

    private static bool Live(ObjectConstructionIdentity identity)
        => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued;
}
