using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

public partial class UIContextMenuComponent
{
    private const int MaximumTransitionPasses = 32;
    private bool _transitioning;
    private UIContextMenuRequest? _pendingTransition;

    private void RequestTransition(UIContextMenuRequest request)
    {
        if (_tearingDown || IsDestroyed || IsDestroyQueued)
            return;
        // Reentrant callbacks replace one pending request. They do not mutate the
        // children that the current transition is building or releasing.
        _pendingTransition = request with
        {
            Owner = ObjectConstructionIdentity.Capture(this),
            Node = ObjectConstructionIdentity.Capture(SceneNode),
            Transform = ObjectConstructionIdentity.Capture(Transform),
            Parent = Transform.Parent is { } parent ? ObjectConstructionIdentity.Capture(parent) : null
        };
        if (_transitioning)
            return;
        _transitioning = true;
        try
        {
            int passes = 0;
            while (_pendingTransition is { } next && !_tearingDown && !IsDestroyed && !IsDestroyQueued)
            {
                _pendingTransition = null;
                if (!next.Owner.IsCurrent || !next.Node.IsCurrent || !next.Transform.IsCurrent ||
                    !ReferenceEquals(SceneNode, next.Node.Target) || !ReferenceEquals(Transform, next.Transform.Target) ||
                    (next.Parent is { } capturedParent && !capturedParent.IsCurrent) ||
                    !ReferenceEquals(Transform.Parent, next.Parent?.Target))
                    continue;
                if (++passes > MaximumTransitionPasses)
                    throw new InvalidOperationException("Context menu callbacks did not settle.");
                if (next.Show)
                    ExecuteShow(next);
                else
                    HideNow(next.ClearItems);
            }
        }
        catch (Exception failure)
        {
            // Failed builders must not leave live input or a partial menu. A cleanup
            // callback cannot start another request after a failed transition.
            try
            {
                HideNow(clearMenuItems: true);
                ClearMenuItems();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Context menu transition and cleanup failed.", failure, cleanupFailure);
            }
            throw;
        }
        finally
        {
            _pendingTransition = null;
            _transitioning = false;
        }
    }
}
