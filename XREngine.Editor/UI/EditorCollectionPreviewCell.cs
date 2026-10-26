using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Retains the text and binding intent of one recycled collection preview cell.</summary>
internal sealed class EditorCollectionPreviewCell
{
    internal required UITextComponent Label { get; init; }
    internal ulong RowId { get; set; }
    internal ulong Intent { get; set; }
}
