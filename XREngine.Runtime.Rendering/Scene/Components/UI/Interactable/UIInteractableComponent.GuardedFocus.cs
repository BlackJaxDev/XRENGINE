using XREngine.Data.Core;

namespace XREngine.Rendering.UI;

public abstract partial class UIInteractableComponent
{
    private Func<bool>? _activeFocusWriteAdmission;

    /// <summary>Applies focus only while the exact caller admission survives callbacks.</summary>
    public bool SetFocusGuarded(bool value, Func<bool> isCurrent)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (SceneNode is not { IsTransformNull: false } node ||
            !node.Components.Contains(this))
            return false;
        var transform = node.Transform;
        ObjectConstructionIdentity componentIdentity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(transform);
        bool Current() => componentIdentity.IsCurrent && nodeIdentity.IsCurrent &&
            transformIdentity.IsCurrent && !IsDestroyed && !IsDestroyQueued &&
            !node.IsDestroyed && !node.IsDestroyQueued && !node.IsTransformNull &&
            !transform.IsDestroyed && !transform.IsDestroyQueued &&
            ReferenceEquals(SceneNode, node) && ReferenceEquals(node.Transform, transform) &&
            node.Components.Contains(this) && isCurrent() &&
            componentIdentity.IsCurrent && nodeIdentity.IsCurrent && transformIdentity.IsCurrent &&
            ReferenceEquals(SceneNode, node) && !node.IsTransformNull &&
            ReferenceEquals(node.Transform, transform) && node.Components.Contains(this);
        bool rejected = false;
        void Require()
        {
            if (Current()) return;
            rejected = true;
            throw new InvalidOperationException("The focused control owner changed.");
        }
        Func<bool>? previousAdmission = _activeFocusWriteAdmission;
        _activeFocusWriteAdmission = Current;
        try
        {
            try
            {
                SetFieldValidated(ref _isFocused, value, (Action)Require,
                    (require, original, current, _) =>
                    {
                        require();
                        if (original != current)
                        {
                            rejected = true;
                            throw new InvalidOperationException("Focus changed during assignment.");
                        }
                    }, propertyName: nameof(IsFocused));
                return Current() && _isFocused == value;
            }
            catch (InvalidOperationException) when (rejected) { return false; }
        }
        finally { _activeFocusWriteAdmission = previousAdmission; }
    }
}
