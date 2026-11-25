namespace XREngine.Editor;

/// <summary>
/// Lets a custom <see cref="EditorComponentAttribute"/> editor show its values in the virtual
/// rows of inspector collections. Implement this interface on the attribute type.
/// </summary>
/// <remarks>
/// The virtual row host places each row in a slot of the measured height before the row binds,
/// and it does not measure the row content. The editor must fit in the height that this
/// interface returns for the same value. The host reads the height again when the collection
/// refreshes. A row whose editor is recycled is rebuilt, so the editor does not need to rebind.
/// A custom editor type without this interface keeps the non-virtual collection fallback.
/// </remarks>
public interface IEditorMeasuredRowEditor
{
    /// <summary>
    /// Returns the row height, in pixels, for one collection value. The host uses the
    /// single-line row height for a value that is not finite or is smaller than that height,
    /// and it limits the height to <see cref="MaxRowHeight"/>.
    /// </summary>
    float MeasureRowHeight(object? value);

    /// <summary>The largest row height that the host accepts from a custom editor.</summary>
    const float MaxRowHeight = 2048;
}
