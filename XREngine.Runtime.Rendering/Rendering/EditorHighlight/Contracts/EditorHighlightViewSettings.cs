using System.Numerics;

namespace XREngine.Rendering.EditorHighlight.Contracts;

/// <summary>
/// Holds the editor colors and enabled flags captured for one rendered view.
/// Scene materials never use these values as stencil state.
/// </summary>
public readonly record struct EditorHighlightViewSettings(
    Vector3 HoverColor,
    Vector3 SelectionColor,
    bool HoverEnabled,
    bool SelectionEnabled);
