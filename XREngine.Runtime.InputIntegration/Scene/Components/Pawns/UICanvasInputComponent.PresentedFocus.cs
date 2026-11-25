using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    /// <summary>Acquires an exact presented control without replacing another focus owner.</summary>
    public bool TryFocusPresented(UIInteractableComponent target, ulong expectedFocusIntentRevision,
        Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (_activeGuardedFocusAdmission is not null || HasPendingFocusRequest ||
            FocusIntentRevision != expectedFocusIntentRevision || !IsActiveInHierarchy ||
            !ProofCurrent(isCurrent) || RefreshPresentedInput() is not { } publication)
            return false;
        try
        {
            if (!_inputRecords.TryGetValue(target.RenderInfo2D.MembershipId, out UIHitRecord record) ||
                !record.IsInteractable || !record.IsTabStop ||
                record.VirtualListIdentity != default || record.VirtualRowIdentity != default ||
                record.VirtualRowLease != default || record.VirtualProviderIncarnation != 0 ||
                record.FocusScopeIdentity != publication.ActiveFocusScopeIdentity ||
                !IsCurrentHitRecord(record) || !target.CanInteract ||
                GetCameraCanvasNonCreating() is not { IsActiveInHierarchy: true } canvas ||
                !ReferenceEquals(target.UserInterfaceCanvas, canvas)) return false;
            var owner = UICanvasFocusOwnerProof.Capture(this, null, canvas, target);
            if (owner is not { IsCurrent: true }) return false;
            List<TransformBase> path = [];
            for (TransformBase? transform = target.Transform; transform is not null; transform = transform.Parent)
            {
                path.Add(transform);
                if (ReferenceEquals(transform, canvas.Transform)) break;
            }
            if (path.Count == 0 || !ReferenceEquals(path[^1], canvas.Transform)) return false;
            var identities = path.Select(ObjectConstructionIdentity.Capture).ToArray();
            var nodes = path.Select(transform => transform.SceneNode).ToArray();
            if (nodes.Any(node => node is null)) return false;
            var nodeIdentities = nodes.Select(node => ObjectConstructionIdentity.Capture(node!)).ToArray();
            var worlds = path.Select(transform => transform.World).ToArray();
            var nodeWorlds = nodes.Select(node => node!.World).ToArray();
            var inputWorld = World;
            var canvasWorld = canvas.World;
            var targetWorld = target.World;
            ulong publicationRevision = _inputPublicationRevision;
            ulong intent = expectedFocusIntentRevision;
            var controller = OwningPawn?.Controller;
            bool LocalCurrent()
            {
                if (!owner.Value.IsCurrent || !IsActiveInHierarchy || HasPendingFocusRequest ||
                    !ReferenceEquals(World, inputWorld) || !ReferenceEquals(canvas.World, canvasWorld) ||
                    !ReferenceEquals(target.World, targetWorld) ||
                    !ReferenceEquals(OwningPawn?.Controller, controller) ||
                    FocusIntentRevision != intent || _inputPublicationRevision != publicationRevision ||
                    !ReferenceEquals(_inputPublication, publication) || !IsCurrentHitRecord(record) ||
                    !target.CanInteract || !ReferenceEquals(target.UserInterfaceCanvas, canvas) ||
                    _focusedComponent is { } focused && !ReferenceEquals(focused, target) ||
                    controller?.FocusedInteractable is { } controlled && !ReferenceEquals(controlled, target) ||
                    _linkedFocusedComponent is { IsFocused: true } linked && !ReferenceEquals(linked, target))
                    return false;
                for (int index = 0; index < path.Count; index++)
                    if (!identities[index].IsCurrent || !nodeIdentities[index].IsCurrent ||
                        path[index].IsDestroyed || path[index].IsDestroyQueued ||
                        nodes[index]!.IsDestroyed || nodes[index]!.IsDestroyQueued ||
                        !ReferenceEquals(path[index].SceneNode, nodes[index]) ||
                        !ReferenceEquals(nodes[index]!.Transform, path[index]) ||
                        !ReferenceEquals(path[index].World, worlds[index]) ||
                        !ReferenceEquals(nodes[index]!.World, nodeWorlds[index]) ||
                        index + 1 < path.Count && !ReferenceEquals(path[index].Parent, path[index + 1])) return false;
                return true;
            }
            bool Current() => LocalCurrent() && ProofCurrent(isCurrent) && LocalCurrent();
            if (!Current()) return false;
            if (ReferenceEquals(_focusedComponent, target) && target.IsFocused) return true;
            FocusTargetGuardedCore(target, Current, accepted => intent = accepted);
            return Current() && ReferenceEquals(_focusedComponent, target) && target.IsFocused;
        }
        finally { publication.Release(); }
    }
}
