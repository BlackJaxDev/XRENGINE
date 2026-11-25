namespace XREngine.Rendering.UI;

/// <summary>
/// The accepted text layout of one text field hit, frozen in the hit packet.
/// <see cref="Text"/> is the text that the layout places. A pointer action uses
/// the layout only while the field still holds the same text.
/// </summary>
internal sealed record UITextCaretHitMetadata(UITextInputComponent Owner, UITextLayoutResult Layout, string Text);
