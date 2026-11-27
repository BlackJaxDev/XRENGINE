namespace XREngine.Rendering.UI;

/// <summary>
/// Reusable CSS-class-like visual preset bundling base properties, interaction state overrides,
/// and transition curves for native UI elements.
/// </summary>
public sealed class UIStyle
{
    /// <summary>
    /// Base visual properties applied in <see cref="EUIInteractionState.Default"/>.
    /// </summary>
    public UIStyleProperties Base { get; set; }

    /// <summary>
    /// Property overrides applied when hovered.
    /// </summary>
    public UIStyleProperties? Hover { get; set; }

    /// <summary>
    /// Property overrides applied when pressed.
    /// </summary>
    public UIStyleProperties? Pressed { get; set; }

    /// <summary>
    /// Property overrides applied when focused.
    /// </summary>
    public UIStyleProperties? Focused { get; set; }

    /// <summary>
    /// Property overrides applied when disabled.
    /// </summary>
    public UIStyleProperties? Disabled { get; set; }

    /// <summary>
    /// Transition curve for background, gradient, border, shadow, text, and opacity changes.
    /// </summary>
    public UITransitionSpec ColorTransition { get; set; } = UITransitionSpec.AppleEaseOut(0.14f);

    /// <summary>
    /// When true, entering the hover or pressed state applies its style at once, and only leaving it uses
    /// <see cref="ColorTransition"/>. A hover highlight that fades in trails a moving pointer, so desktop menus
    /// and lists highlight at once and fade out.
    /// </summary>
    public bool ImmediateHighlight { get; set; }

    /// <summary>
    /// Transition curve for <see cref="UIBoundableTransform.VisualScale"/> and <see cref="UIBoundableTransform.VisualOffset"/>.
    /// </summary>
    public UITransitionSpec TransformTransition { get; set; } = UITransitionSpec.AppleEaseOut(0.14f);

    /// <summary>
    /// Transition curve for backdrop blur radius and blend changes.
    /// </summary>
    public UITransitionSpec BlurTransition { get; set; } = UITransitionSpec.AppleEaseOut(0.20f);

    /// <summary>
    /// Resolves the complete set of style properties for the requested interaction state.
    /// </summary>
    public UIStyleProperties ResolveForState(EUIInteractionState state)
    {
        UIStyleProperties resolved = Base;
        switch (state)
        {
            case EUIInteractionState.Hover:
                if (Hover is { } hover)
                    resolved = hover.ResolveOver(in resolved);
                break;
            case EUIInteractionState.Pressed:
                if (Hover is { } pressedHover)
                    resolved = pressedHover.ResolveOver(in resolved);
                if (Pressed is { } pressed)
                    resolved = pressed.ResolveOver(in resolved);
                break;
            case EUIInteractionState.Focused:
                if (Focused is { } focused)
                    resolved = focused.ResolveOver(in resolved);
                break;
            case EUIInteractionState.Disabled:
                if (Disabled is { } disabled)
                    resolved = disabled.ResolveOver(in resolved);
                break;
        }

        return resolved;
    }
}
