namespace XREngine.Rendering.EditorHighlight.Contracts;

/// <summary>
/// Supplies editor highlight settings to an optional Advanced render module.
/// The pipeline owns its mask resources and never reads editor preferences when
/// no module is installed.
/// </summary>
public interface IEditorHighlightModule
{
    /// <summary>Captures the colors and enabled flags for the current view.</summary>
    EditorHighlightViewSettings CaptureViewSettings();
}
