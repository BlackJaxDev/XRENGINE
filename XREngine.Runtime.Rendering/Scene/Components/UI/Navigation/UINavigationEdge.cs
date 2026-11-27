namespace XREngine.Rendering.UI;

/// <summary>
/// Serializable directed or bidirectional navigation edge inside a <see cref="UINavigationFlowMap"/>.
/// Designed for visual inspection and editing in a UI flow-map editor.
/// </summary>
public sealed class UINavigationEdge
{
    /// <summary>
    /// Interactable where this navigation transition originates.
    /// </summary>
    public UIInteractableComponent? Source { get; set; }

    /// <summary>
    /// Input direction that triggers this edge from <see cref="Source"/>.
    /// </summary>
    public EUINavigationDirection Direction { get; set; }

    /// <summary>
    /// Destination interactable when <see cref="Rule"/> resolves to an explicit target.
    /// </summary>
    public UIInteractableComponent? Target { get; set; }

    /// <summary>
    /// Navigation rule applied when leaving <see cref="Source"/> along <see cref="Direction"/>.
    /// </summary>
    public EUINavigationRule Rule { get; set; } = EUINavigationRule.Explicit;

    /// <summary>
    /// When true and <see cref="Rule"/> is <see cref="EUINavigationRule.Explicit"/>, also allows navigating
    /// from <see cref="Target"/> back to <see cref="Source"/> along the opposite direction unless overridden.
    /// </summary>
    public bool Bidirectional { get; set; }
}
