namespace XREngine.Rendering.UI;

/// <summary>
/// Serializable navigation link for one direction on a UI element.
/// Stores both the resolution rule and an optional direct target interactable.
/// </summary>
public struct UINavigationLink
{
    /// <summary>
    /// Rule controlling how this direction resolves a target.
    /// </summary>
    public EUINavigationRule Rule { get; set; }

    /// <summary>
    /// Optional explicit destination interactable for this direction.
    /// </summary>
    public UIInteractableComponent? Target { get; set; }

    /// <summary>
    /// Returns a link configured for automatic resolution.
    /// </summary>
    public static UINavigationLink Auto => new() { Rule = EUINavigationRule.Auto, Target = null };

    /// <summary>
    /// Returns a link that blocks navigation in this direction.
    /// </summary>
    public static UINavigationLink Blocked => new() { Rule = EUINavigationRule.Blocked, Target = null };

    /// <summary>
    /// Returns a link that wraps to the opposite edge along the same axis when no explicit target is set.
    /// </summary>
    public static UINavigationLink Wrap => new() { Rule = EUINavigationRule.Wrap, Target = null };

    /// <summary>
    /// Creates an explicit link pointing to <paramref name="target"/>.
    /// </summary>
    public static UINavigationLink Explicit(UIInteractableComponent? target)
        => new() { Rule = EUINavigationRule.Explicit, Target = target };
}
