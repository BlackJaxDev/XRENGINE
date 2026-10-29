namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>Sets the colors of the leaf splats of a bake.</summary>
public interface ISplatColorBaker
{
    /// <summary>Gets the name of the color mode, for example <c>lit-projection</c>.</summary>
    string Mode { get; }

    /// <summary>
    /// Writes the color of each leaf splat of <paramref name="context"/> in place.
    /// </summary>
    /// <param name="context">The leaves, the rest surface, and the views.</param>
    /// <param name="cancellationToken">A token that stops the bake.</param>
    /// <returns>The color scale. The stored colors are the real colors divided by this value.</returns>
    Task<float> BakeAsync(SplatColorBakeContext context, CancellationToken cancellationToken);
}
