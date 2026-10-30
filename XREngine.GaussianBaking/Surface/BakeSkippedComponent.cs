namespace XREngine.Rendering.Models.Gaussian.Baking.Surface;

/// <summary>
/// Describes one component that renders content but that the bake does not capture.
/// </summary>
/// <param name="NodePath">The path of the component node, from the content root, with node names joined by <c>/</c>.</param>
/// <param name="ComponentType">The type name of the component.</param>
/// <param name="Reason">Why the bake skips the component, for example <see cref="NotCapturedReason"/>.</param>
public sealed record BakeSkippedComponent(string NodePath, string ComponentType, string Reason)
{
    /// <summary>The reason for content that the bake cannot turn into splats, such as particles and UI.</summary>
    public const string NotCapturedReason = "not-captured";
}
