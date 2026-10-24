using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Sizes one text detail from its accepted layout result.</summary>
public sealed class InspectorStringDetailSizeComponent : XRComponent
{
    private UITextComponent? _text;
    private UIBoundableTransform? _bounds;
    private bool _retired;

    public void Bind(UITextComponent text, UIBoundableTransform bounds)
    {
        if (_retired || IsDestroyed || IsDestroyQueued || SceneNode.IsDestroyed ||
            SceneNode.IsDestroyQueued || !ReferenceEquals(bounds.SceneNode, SceneNode) ||
            !ReferenceEquals(text.SceneNode.Parent, SceneNode))
            return;
        if (ReferenceEquals(_text, text) && ReferenceEquals(_bounds, bounds)) return;
        UITextComponent? previous = _text;
        SetField(ref _text, text, publishNotifications: false);
        SetField(ref _bounds, bounds, publishNotifications: false);
        if (previous is not null)
            previous.AcceptedLayoutChanged -= AcceptedLayoutChanged;
        if (_retired || IsDestroyed || IsDestroyQueued) return;
        text.AcceptedLayoutChanged += AcceptedLayoutChanged;
        if (text.TryGetAcceptedLayoutResult(out UITextLayoutResult result, out Vector2 actualSize))
            ApplyAcceptedLayout(text, result, actualSize, allowInactive: true);
    }

    public void Retire()
    {
        if (_retired) return;
        SetField(ref _retired, true, publishNotifications: false);
        UITextComponent? text = _text;
        SetField(ref _text, null, publishNotifications: false);
        SetField(ref _bounds, null, publishNotifications: false);
        if (text is not null)
            text.AcceptedLayoutChanged -= AcceptedLayoutChanged;
    }

    private void AcceptedLayoutChanged(UITextComponent source, UITextLayoutResult result,
        Vector2 actualSize)
        => ApplyAcceptedLayout(source, result, actualSize, allowInactive: false);

    private void ApplyAcceptedLayout(UITextComponent source, UITextLayoutResult result,
        Vector2 actualSize, bool allowInactive)
    {
        UITextComponent? text = _text;
        UIBoundableTransform? bounds = _bounds;
        if (_retired || IsDestroyed || IsDestroyQueued || !allowInactive && !IsActiveInHierarchy ||
            !ReferenceEquals(source, text) || bounds is null ||
            !ReferenceEquals(bounds.SceneNode, SceneNode) ||
            !ReferenceEquals(source.SceneNode.Parent, SceneNode) ||
            !source.TryGetAcceptedLayoutResult(out UITextLayoutResult current, out Vector2 currentSize) ||
            currentSize != actualSize ||
            !ReferenceEquals(current, result))
            return;
        float height = result.IntrinsicSize.Y + 12.0f;
        if (float.IsFinite(height))
            bounds.Height = Math.Clamp(height, 180.0f, 20000.0f);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (_retired || !IsActiveInHierarchy || _text is not { } text ||
            !text.TryGetAcceptedLayoutResult(out UITextLayoutResult result, out Vector2 actualSize))
            return;
        ApplyAcceptedLayout(text, result, actualSize, allowInactive: false);
    }

    protected override void OnDestroying()
    {
        try { Retire(); }
        finally { base.OnDestroying(); }
    }
}
