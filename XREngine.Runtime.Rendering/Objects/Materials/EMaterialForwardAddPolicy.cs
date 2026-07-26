namespace XREngine.Rendering;

/// <summary>
/// Describes how a source shader's additive-light pass participates in the
/// engine's lighting architecture.
/// </summary>
public enum EMaterialForwardAddPolicy
{
    Disabled,
    FoldedIntoForwardPlusBase,
    CompatibilityPass,
}
