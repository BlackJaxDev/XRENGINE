using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Checks the search field and its text intent across live view transfer callbacks.</summary>
internal sealed class NativeAnimationSearchFieldReceipt
{
    private readonly SceneNode _node;
    private readonly TransformBase _transform;
    private readonly TransformBase _parent, _panel;
    private readonly UITextComponent _display;
    private readonly ObjectConstructionIdentity[] _owners;
    private readonly ulong _textIntent, _displayIntent, _caretIntent;
    private readonly long _focusEpoch;
    private readonly bool _focused;
    private readonly string _preText, _postText;
    private readonly IRuntimeWorldContext? _world;

    private NativeAnimationSearchFieldReceipt(AnimationPanel panel, UITextInputComponent input, UITextComponent display)
    {
        Input = input;
        _panel = panel.Transform;
        _node = input.SceneNode;
        _transform = _node.Transform;
        _parent = _transform.Parent!;
        _display = display;
        _owners = [ObjectConstructionIdentity.Capture(input), ObjectConstructionIdentity.Capture(display),
            ObjectConstructionIdentity.Capture(_node), ObjectConstructionIdentity.Capture(_transform),
            ObjectConstructionIdentity.Capture(_parent), ObjectConstructionIdentity.Capture(_panel)];
        _textIntent = input.TextWriteIntentRevision;
        _displayIntent = display.TextWriteIntentRevision;
        _caretIntent = input.CaretWriteIntentRevision;
        _focusEpoch = input.FocusEpoch;
        _focused = input.IsFocused;
        _preText = input.PreText;
        _postText = input.PostText;
        _world = input.World;
    }

    internal UITextInputComponent Input { get; }

    internal static bool TryCapture(AnimationPanel panel, UITextInputComponent? input,
        out NativeAnimationSearchFieldReceipt receipt)
    {
        receipt = null!;
        if (input?.SceneNode is not { IsTransformNull: false } node || node.Transform.Parent is null ||
            node.GetComponent<UITextComponent>() is not { } display) return false;
        bool attached = false;
        for (TransformBase? current = node.Transform.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, panel.Transform)) { attached = true; break; }
        if (!attached) return false;
        receipt = new(panel, input, display);
        return receipt.IsCurrent(0, string.Empty);
    }

    internal bool IsCurrent(int phase, string query)
    {
        foreach (var owner in _owners)
            if (!owner.IsCurrent || owner.Target.IsDestroyed || owner.Target.IsDestroyQueued) return false;
        if (!ReferenceEquals(Input.SceneNode, _node) || !ReferenceEquals(_display.SceneNode, _node) ||
            _node.IsTransformNull || !ReferenceEquals(_node.Transform, _transform) ||
            !ReferenceEquals(_transform.Parent, _parent) || !_node.Components.Contains(Input) ||
            !_node.Components.Contains(_display) || !ReferenceEquals(Input.World, _world) ||
            Input.CaretWriteIntentRevision != _caretIntent || Input.FocusEpoch != _focusEpoch ||
            Input.IsFocused != _focused || Input.PreText != _preText || Input.PostText != _postText)
            return false;
        bool attached = false;
        for (TransformBase? current = _parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, _panel)) { attached = true; break; }
        if (!attached) return false;
        if (phase == 0) return Input.TextWriteIntentRevision == _textIntent &&
            _display.TextWriteIntentRevision == _displayIntent;
        if (Input.TextWriteIntentRevision != _textIntent + 1) return false;
        if (phase == 1) return _display.TextWriteIntentRevision == _displayIntent ||
            _display.TextWriteIntentRevision == _displayIntent + 1;
        return phase == 2 && _display.TextWriteIntentRevision == _displayIntent + 1 &&
            Input.Text == query && DisplayMatches(query);
    }

    private bool DisplayMatches(string query)
    {
        if (_focused) return _display.Text == query;
        ReadOnlySpan<char> text = _display.Text.AsSpan();
        return text.Length == _preText.Length + query.Length + _postText.Length &&
            text.StartsWith(_preText, StringComparison.Ordinal) &&
            text.Slice(_preText.Length, query.Length).SequenceEqual(query) &&
            text.EndsWith(_postText, StringComparison.Ordinal);
    }
}
