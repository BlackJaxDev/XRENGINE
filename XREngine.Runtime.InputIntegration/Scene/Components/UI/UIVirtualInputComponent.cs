using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using YamlDotNet.Serialization;

namespace XREngine.Components;

/// <summary>
/// Authored UI hit surface feeding the owning player's existing input mappings.
/// Compose material/text components for its appearance, as with other interactable UI.
/// </summary>
public abstract class UIVirtualInputComponent : UIInteractableComponent
{
    // Transient sampled state deliberately avoids property notifications and per-frame event allocations.
    private object? _pointerOwner;

    protected UIVirtualInputComponent() => RegisterInputsOnFocus = false;

    [YamlIgnore]
    public bool IsPressed => _pointerOwner is not null;

    internal bool IsCapturedBy(object owner) => ReferenceEquals(_pointerOwner, owner);

    internal bool BeginPointer(object owner, Vector2 localPosition)
    {
        if (_pointerOwner is not null || !IsActiveInHierarchy || !UITransform.IsVisibleInHierarchy)
            return false;
        _pointerOwner = owner;
        UpdatePointer(localPosition);
        return true;
    }

    internal void EndPointer(object owner)
    {
        if (IsCapturedBy(owner))
            CancelPointer();
    }

    protected void CancelPointer()
    {
        _pointerOwner = null;
        ResetValue();
    }

    internal abstract void UpdatePointer(Vector2 localPosition);
    internal virtual ushort ButtonMask => 0;
    internal virtual void WriteAxes(Span<float> axes) { }
    protected virtual void ResetValue() { }

    protected override void OnComponentDeactivated()
    {
        CancelPointer();
        base.OnComponentDeactivated();
    }

    protected override void OnTransformChanging()
    {
        CancelPointer();
        base.OnTransformChanging();
    }

    protected override void UITransformPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
    {
        if (e.PropertyName is nameof(UITransform.Parent) or nameof(UITransform.ParentCanvas) or nameof(UITransform.Visibility))
            CancelPointer();
        base.UITransformPropertyChanging(sender, e);
    }
}
