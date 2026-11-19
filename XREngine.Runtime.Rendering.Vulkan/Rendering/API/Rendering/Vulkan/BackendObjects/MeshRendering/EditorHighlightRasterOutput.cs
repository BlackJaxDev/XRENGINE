namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Freezes the auxiliary editor mask layout for one accepted mesh draw.
/// </summary>
internal readonly record struct EditorHighlightRasterOutput(
    EEditorHighlightRasterWriteRole Role,
    byte AttachmentIndex,
    byte AbiVersion)
{
    internal bool HasAttachment => Role != EEditorHighlightRasterWriteRole.None;
    internal bool WritesMask => Role == EEditorHighlightRasterWriteRole.SceneColor;
    internal byte PickingAttachmentIndex => 2;
}
