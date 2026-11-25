using System;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

public partial class UITextInputComponent
{
    /// <summary>Writes a caret and anchor only while their owner and text stay current.</summary>
    public bool SetSelectionGuarded<TState>(int cursor, int anchor, TState state,
        Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (SceneNode is not { IsTransformNull: false } node ||
            !TryGetSiblingComponent<UITextComponent>(out var display) || display is null)
            throw new InvalidOperationException("The input selection owner is unavailable.");

        TransformBase transform = node.Transform;
        ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(this);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(transform);
        ObjectConstructionIdentity displayIdentity = ObjectConstructionIdentity.Capture(display);
        var world = node.World;
        var transformWorld = transform.World;
        var inputWorld = World;
        var displayWorld = display.World;
        TransformBase? parent = transform.Parent;
        string text = _text;
        ulong textRevision = _textWriteIntentRevision;
        ulong caretRevision = checked(++_caretWriteIntentRevision);
        ulong displayRevision = display.TextWriteIntentRevision;
        ulong formattingRevision = _displayFormattingRevision;
        long focusEpoch = _focusEpoch;
        string preText = PreText;
        string postText = PostText;
        int originalCursor = _cursorPosition;
        int originalAnchor = _selectionAnchor;

        void CheckOwner()
        {
            if (!identity.IsCurrent || !nodeIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                !displayIdentity.IsCurrent || node.IsDestroyed || node.IsDestroyQueued ||
                transform.IsDestroyed || transform.IsDestroyQueued || _terminal ||
                IsDestroyed || IsDestroyQueued || display.IsDestroyed || display.IsDestroyQueued ||
                !ReferenceEquals(SceneNode, node) || node.IsTransformNull ||
                !ReferenceEquals(node.Transform, transform) ||
                !ReferenceEquals(transform.SceneNode, node) ||
                !ReferenceEquals(node.World, world) ||
                !ReferenceEquals(transform.World, transformWorld) ||
                !ReferenceEquals(World, inputWorld) ||
                !ReferenceEquals(display.World, displayWorld) ||
                !ReferenceEquals(transform.Parent, parent) ||
                !ReferenceEquals(display.SceneNode, node) ||
                !UITextWriteOwner.Contains(node, this) || !UITextWriteOwner.Contains(node, display) ||
                !ReferenceEquals(_text, text) || _textWriteIntentRevision != textRevision ||
                _caretWriteIntentRevision != caretRevision ||
                display.TextWriteIntentRevision != displayRevision ||
                _displayFormattingRevision != formattingRevision || _focusEpoch != focusEpoch ||
                PreText != preText || PostText != postText)
                throw new InvalidOperationException("The input selection owner changed during assignment.");
        }

        void Require()
        {
            CheckOwner();
            requireCurrent(state);
            CheckOwner();
        }

        Require();
        SetCursorPositionForTextWrite(cursor, Require);
        Require();
        SetSelectionAnchorForTextWrite(anchor, Require);
        Require();
        return _cursorPosition != originalCursor || _selectionAnchor != originalAnchor;
    }
}
