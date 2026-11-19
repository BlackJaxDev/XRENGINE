using System.Numerics;
using XREngine.Rendering.EditorHighlight.Contracts;

namespace XREngine.Editor.Rendering.EditorHighlight;

/// <summary>
/// Reads editor outline preferences when an editor pipeline requests a view.
/// Runtime pipelines do not construct this module for non-editor applications.
/// </summary>
public sealed class EditorHighlightModule : IEditorHighlightModule
{
    public EditorHighlightViewSettings CaptureViewSettings()
    {
        var preferences = Engine.EditorPreferences.Selection;
        var hover = preferences.HoverOutlineColor;
        var selection = preferences.SelectionOutlineColor;
        return new(
            new Vector3((float)hover.R, (float)hover.G, (float)hover.B),
            new Vector3((float)selection.R, (float)selection.G, (float)selection.B),
            preferences.HoverOutlineEnabled,
            preferences.SelectionOutlineEnabled);
    }
}
