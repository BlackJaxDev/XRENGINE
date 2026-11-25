using System;
using System.ComponentModel;
using XREngine.Extensions;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    private ulong _textWriteIntentRevision;
    private ulong _caretWriteIntentRevision;
    private ulong _displayFormattingRevision;
    private Action? _activeTextWriteAdmission;
    private Action? _activeDisplayTextAdvance;

    /// <summary>Identifies the latest input text write attempt.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong TextWriteIntentRevision => _textWriteIntentRevision;

    /// <summary>Identifies the latest caret or selection write attempt.</summary>
    [YamlIgnore, Browsable(false)]
    public ulong CaretWriteIntentRevision => _caretWriteIntentRevision;

    /// <summary>Writes text only while this input, its display, and the caller stay current.</summary>
    public bool SetTextGuarded<TState>(string value, TState state,
        Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { } node ||
            !TryGetSiblingComponent<UITextComponent>(out var display) || display is null)
            throw new InvalidOperationException("The input display text is unavailable.");
        ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ObjectConstructionIdentity displayIdentity = ObjectConstructionIdentity.Capture(display);
        ulong revision = checked(++_textWriteIntentRevision);
        ulong caretRevision = _caretWriteIntentRevision;
        ulong displayRevision = display.TextWriteIntentRevision;
        ulong formattingRevision = _displayFormattingRevision;
        long focusEpoch = _focusEpoch;
        string preText = PreText;
        string postText = PostText;
        Action? previousAdmission = _activeTextWriteAdmission;
        Action? previousDisplayAdvance = _activeDisplayTextAdvance;
        Action admission = null!;
        void Require()
        {
            if (!identity.IsCurrent || !nodeIdentity.IsCurrent || !displayIdentity.IsCurrent ||
                node.IsDestroyed || node.IsDestroyQueued ||
                _terminal || IsDestroyed || IsDestroyQueued ||
                display.IsDestroyed || display.IsDestroyQueued ||
                !ReferenceEquals(SceneNode, node) ||
                !ReferenceEquals(display.SceneNode, node) ||
                !UITextWriteOwner.Contains(node, this) ||
                !UITextWriteOwner.Contains(node, display) ||
                _textWriteIntentRevision != revision ||
                _caretWriteIntentRevision != caretRevision ||
                display.TextWriteIntentRevision != displayRevision ||
                _displayFormattingRevision != formattingRevision ||
                _focusEpoch != focusEpoch ||
                PreText != preText || PostText != postText ||
                !ReferenceEquals(_activeTextWriteAdmission, admission))
                throw new InvalidOperationException("The input text owner changed during assignment.");
            requireCurrent(state);
            if (!identity.IsCurrent || !nodeIdentity.IsCurrent || !displayIdentity.IsCurrent ||
                node.IsDestroyed || node.IsDestroyQueued ||
                _terminal || IsDestroyed || IsDestroyQueued ||
                display.IsDestroyed || display.IsDestroyQueued ||
                !ReferenceEquals(SceneNode, node) ||
                !ReferenceEquals(display.SceneNode, node) ||
                !UITextWriteOwner.Contains(node, this) ||
                !UITextWriteOwner.Contains(node, display) ||
                _textWriteIntentRevision != revision ||
                _caretWriteIntentRevision != caretRevision ||
                display.TextWriteIntentRevision != displayRevision ||
                _displayFormattingRevision != formattingRevision ||
                _focusEpoch != focusEpoch ||
                PreText != preText || PostText != postText ||
                !ReferenceEquals(_activeTextWriteAdmission, admission))
                throw new InvalidOperationException("The input text owner changed during validation.");
        }

        admission = Require;
        void AdvanceDisplay()
        {
            Require();
            displayRevision++;
        }
        _activeTextWriteAdmission = admission;
        _activeDisplayTextAdvance = AdvanceDisplay;
        try
        {
            string originalText = _text;
            bool changed = SetFieldValidated(ref _text, value, admission,
                static (admission, original, current, _) =>
                {
                    admission();
                    if (!string.Equals(original, current, StringComparison.Ordinal))
                        throw new InvalidOperationException("The input text changed during assignment.");
                }, propertyName: nameof(Text));
            Require();
            if (!changed && !ReferenceEquals(originalText, value))
                throw new InvalidOperationException("The input text write was rejected.");
            if (!string.Equals(_text, value, StringComparison.Ordinal))
                throw new InvalidOperationException("The input text write was rejected.");
            if (!changed && !XRBase.ArePropertyNotificationsSuppressed)
                PublishGuardedTextEffects(admission, AdvanceDisplay);
            return changed;
        }
        finally
        {
            if (ReferenceEquals(_activeTextWriteAdmission, admission))
            {
                _activeTextWriteAdmission = previousAdmission;
                _activeDisplayTextAdvance = previousDisplayAdvance;
            }
        }
    }

    private void PublishGuardedTextEffects(Action requireCurrent,
        Action advanceDisplay)
    {
        requireCurrent();
        _preferredCaretX = null;
        _verticalVisualLine = null;
        SetCursorPositionForTextWrite(_cursorPosition, requireCurrent);
        requireCurrent();
        SetSelectionAnchorForTextWrite(_selectionAnchor, requireCurrent);
        requireCurrent();
        if (!TryGetSiblingComponent<UITextComponent>(out var display) || display is null)
            throw new InvalidOperationException("The input display text is unavailable.");
        advanceDisplay();
        display.SetTextGuarded(IsFocused ? Text : FormatText(Text),
            requireCurrent, static current => current());
        requireCurrent();
    }

    private void SetCursorPositionForTextWrite(int value, Action requireCurrent)
    {
        int requested = SnapGraphemeBoundary(value.Clamp(0, Text.Length));
        bool changed = SetFieldValidated(ref _cursorPosition, requested, requireCurrent,
            static (admission, original, current, _) =>
            {
                admission();
                if (original != current)
                    throw new InvalidOperationException("The input caret changed during assignment.");
            }, propertyName: nameof(CursorPosition));
        requireCurrent();
        if (_cursorPosition != requested)
            throw new InvalidOperationException("The input caret write was rejected.");
        if (!changed) return;
        _visualCaretLayout = null;
        _visualCaretAffinity = UITextCaretAffinity.Downstream;
        if (!_movingCaretVertically)
        {
            _preferredCaretX = null;
            _verticalVisualLine = null;
        }
    }

    private void SetSelectionAnchorForTextWrite(int value, Action requireCurrent)
    {
        int requested = SnapGraphemeBoundary(value.Clamp(0, Text.Length));
        SetFieldValidated(ref _selectionAnchor, requested, requireCurrent,
            static (admission, original, current, _) =>
            {
                admission();
                if (original != current)
                    throw new InvalidOperationException("The input selection changed during assignment.");
            }, propertyName: nameof(SelectionAnchor));
        requireCurrent();
        if (_selectionAnchor != requested)
            throw new InvalidOperationException("The input selection write was rejected.");
    }
}
