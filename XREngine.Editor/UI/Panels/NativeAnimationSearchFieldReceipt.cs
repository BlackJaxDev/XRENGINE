using XREngine.Data.Core;
using XREngine.Components;
using XREngine.Editor.UI;
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
    private readonly ulong _textIntent, _caretIntent;
    private ulong _displayIntent;
    private readonly long _focusEpoch;
    private readonly bool _focused;
    private readonly string _preText, _postText;
    private readonly string? _displayText;
    private readonly IRuntimeWorldContext? _world;
    private readonly EditorVirtualRowControlLease _inputPath, _displayPath;
    private readonly AnimationPanel _owner;
    private readonly IUICanvasInputSource? _canvasInput;
    private readonly ulong _focusIntent;
    private readonly XRComponent? _canvas;
    private readonly SceneNode? _canvasNode, _canvasInputNode;
    private readonly EditorVirtualRowControlLease? _canvasPath, _canvasInputPath;
    private readonly ObjectConstructionIdentity? _canvasInputIdentity;
    private readonly IRuntimeWorldContext? _canvasWorld, _canvasInputWorld;
    private readonly UIInteractableComponent? _focusedControl;
    private readonly bool _pendingFocus;
    internal NativeAnimationSearchViewValues Values { get; }
    private bool? _transferredFocus;

    private NativeAnimationSearchFieldReceipt(AnimationPanel panel, UITextInputComponent input, UITextComponent display)
    {
        Input = input;
        _owner = panel;
        _inputPath = EditorVirtualRowControlLease.Capture(input, panel.SceneNode);
        _displayPath = EditorVirtualRowControlLease.Capture(display, panel.SceneNode);
        _canvas = input.UserInterfaceCanvas;
        _canvasNode = _canvas?.SceneNode;
        _canvasPath = _canvas is null ? null : EditorVirtualRowControlLease.Capture(_canvas, _canvasNode!);
        _canvasWorld = _canvas?.World;
        _canvasInput = input.UserInterfaceCanvas?.GetInputComponent();
        _canvasInputIdentity = _canvasInput is XRObjectBase inputOwner ? ObjectConstructionIdentity.Capture(inputOwner) : null;
        if (_canvasInput is XRComponent component)
        {
            _canvasInputNode = component.SceneNode;
            _canvasInputPath = EditorVirtualRowControlLease.Capture(component, _canvasInputNode);
            _canvasInputWorld = component.World;
        }
        _focusIntent = _canvasInput?.FocusIntentRevision ?? 0;
        _focusedControl = _canvasInput?.FocusedComponent;
        _pendingFocus = _canvasInput?.HasPendingFocusRequest ?? false;
        Values = new(input.Text, input.CursorPosition, input.SelectionAnchor,
            input.IsFocused && ReferenceEquals(_focusedControl, input));
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
        _displayText = display.Text;
        _world = input.World;
    }

    internal UITextInputComponent Input { get; }
    internal bool OwnsDisplay(UITextComponent display) => ReferenceEquals(display, _display);

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
        if (input.Binding is not null || input.Property is not null || input.Targets is not null ||
            input.CommitOnSubmit || input.IsComposing) return false;
        receipt = new(panel, input, display);
        return receipt.IsCurrent(0, string.Empty);
    }

    internal bool IsCurrent(int phase, string query)
        => IsCurrentCore(phase, query, _caretIntent);

    internal bool IsTransferCurrent(int phase, NativeAnimationSearchViewValues values)
        => IsCurrentCore(Math.Min(phase, 2), values.Text,
                phase >= 3 ? checked(_caretIntent + 1) : _caretIntent, phase >= 5) &&
            (phase < 4 || Input.CursorPosition == values.Cursor && Input.SelectionAnchor == values.Anchor) &&
            (phase != 6 || _transferredFocus is { } focused && Input.IsFocused == focused &&
                Input.FocusEpoch == _focusEpoch + 1 &&
                (_canvasInput?.FocusIntentRevision ?? 0) == NextFocusIntent &&
                ReferenceEquals(_canvasInput?.FocusedComponent, focused ? Input : null));

    private ulong NextFocusIntent => _focusIntent == ulong.MaxValue ? 1 : _focusIntent + 1;

    internal void RestoreFocus(NativeAnimationSearchViewValues values, bool restorePrior,
        Action<int> setPhase, Action requireCurrent)
    {
        if (_canvasInput is null || _pendingFocus ||
            values.Focused && (_focused || _focusedControl is not null) ||
            !values.Focused && (!restorePrior || !_focused)) return;
        requireCurrent();
        _transferredFocus = values.Focused;
        setPhase(5);
        bool Current()
        {
            try { requireCurrent(); return true; }
            catch (InvalidOperationException) { return false; }
        }
        bool accepted = values.Focused
            ? _canvasInput.TryFocusPresented(Input, _focusIntent, Current)
            : _canvasInput.TryReleaseFocusPreservingDraft(Input, _focusIntent, Current, out _);
        if (accepted)
        {
            setPhase(6);
            requireCurrent();
            return;
        }
        // A rejected presentation request may leave the draft intact without focus.
        _transferredFocus = null;
        setPhase(4);
        requireCurrent();
    }

    private bool IsCurrentCore(int phase, string query, ulong caretIntent, bool focusTransition = false)
    {
        bool changingFocus = focusTransition && _transferredFocus is not null;
        if (!_inputPath.IsCurrent(_owner.SceneNode) || !_displayPath.IsCurrent(_owner.SceneNode) ||
            !ReferenceEquals(Input.UserInterfaceCanvas, _canvas) ||
            _canvas is not null && (_canvasPath?.IsCurrent(_canvasNode!) != true ||
                !ReferenceEquals(_canvas.World, _canvasWorld)) ||
            _canvasInputIdentity is { } inputIdentity && (!inputIdentity.IsCurrent ||
                inputIdentity.Target.IsDestroyed || inputIdentity.Target.IsDestroyQueued) ||
            _canvasInput is XRComponent component && (_canvasInputPath?.IsCurrent(_canvasInputNode!) != true ||
                !ReferenceEquals(component.World, _canvasInputWorld)) ||
            !ReferenceEquals(Input.UserInterfaceCanvas?.GetInputComponent(), _canvasInput) ||
            (changingFocus
                ? ((_canvasInput?.FocusIntentRevision ?? 0) != _focusIntent &&
                    (_canvasInput?.FocusIntentRevision ?? 0) != NextFocusIntent) ||
                    (!ReferenceEquals(_canvasInput?.FocusedComponent, _focusedControl) &&
                     !ReferenceEquals(_canvasInput?.FocusedComponent, _transferredFocus == true ? Input : null))
                : (_canvasInput?.FocusIntentRevision ?? 0) != _focusIntent ||
                    !ReferenceEquals(_canvasInput?.FocusedComponent, _focusedControl)) ||
            (_canvasInput?.HasPendingFocusRequest ?? false) != _pendingFocus ||
            Input.Binding is not null || Input.Property is not null || Input.Targets is not null ||
            Input.CommitOnSubmit || Input.IsComposing) return false;
        foreach (var owner in _owners)
            if (!owner.IsCurrent || owner.Target.IsDestroyed || owner.Target.IsDestroyQueued) return false;
        if (!ReferenceEquals(Input.SceneNode, _node) || !ReferenceEquals(_display.SceneNode, _node) ||
            _node.IsTransformNull || !ReferenceEquals(_node.Transform, _transform) ||
            !ReferenceEquals(_transform.Parent, _parent) || !_node.Components.Contains(Input) ||
            !_node.Components.Contains(_display) || !ReferenceEquals(Input.World, _world) ||
            Input.CaretWriteIntentRevision != caretIntent ||
            (changingFocus ? Input.FocusEpoch != _focusEpoch && Input.FocusEpoch != _focusEpoch + 1 :
                Input.FocusEpoch != _focusEpoch || Input.IsFocused != _focused) || Input.PreText != _preText || Input.PostText != _postText)
            return false;
        bool attached = false;
        for (TransformBase? current = _parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, _panel)) { attached = true; break; }
        if (!attached) return false;
        if (changingFocus)
            return Input.TextWriteIntentRevision == _textIntent + 1 && Input.Text == query &&
                (_display.TextWriteIntentRevision == _displayIntent + 1 && DisplayMatches(query) ||
                 _transferredFocus == true && _display.TextWriteIntentRevision == _displayIntent + 2 &&
                    _display.Text == query);
        if (phase == 0) return Input.TextWriteIntentRevision == _textIntent &&
            _display.TextWriteIntentRevision == _displayIntent;
        if (Input.TextWriteIntentRevision != _textIntent + 1) return false;
        if (phase == 1) return _display.TextWriteIntentRevision == _displayIntent ||
            _display.TextWriteIntentRevision == _displayIntent + 1;
        return phase == 2 && _display.TextWriteIntentRevision == _displayIntent + 1 &&
            Input.Text == query && DisplayMatches(query);
    }

    internal void WriteActivationDisplay(UITextComponent display, string value, Action requireCurrent)
    {
        requireCurrent();
        if (!ReferenceEquals(display, _display))
            throw new InvalidOperationException("The Animation search display changed.");
        _displayIntent = checked(_displayIntent + 1);
        display.SetTextGuarded(value, requireCurrent, static require => require());
        requireCurrent();
    }

    internal bool IsGuardedTextWriteOwned(string query)
        => IsCurrent(1, query) && (_display.TextWriteIntentRevision == _displayIntent &&
            _display.Text == _displayText ||
            IsCurrent(2, query));

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
