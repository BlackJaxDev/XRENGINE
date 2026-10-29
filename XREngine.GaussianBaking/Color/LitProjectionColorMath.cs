using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>Sets leaf colors from a full set of lit images in one call.</summary>
public static class LitProjectionColorMath
{
    /// <summary>
    /// Writes the color of each leaf from the images of all views. See <see cref="LitProjectionColorAccumulator"/>.
    /// </summary>
    /// <param name="leaves">The leaf splats. Their colors are written in place.</param>
    /// <param name="rest">The surface at the rest pose.</param>
    /// <param name="views">The cameras of the images.</param>
    /// <param name="images">One linear RGBA image for each view, with the top row first.</param>
    /// <param name="width">The width of each image, in pixels.</param>
    /// <param name="height">The height of each image, in pixels.</param>
    /// <returns>The color scale. The stored colors are the real colors divided by this value.</returns>
    public static float Apply(
        BakedSplat[] leaves,
        DeformedSurface rest,
        IReadOnlyList<BakeCameraPose> views,
        float[][] images,
        int width,
        int height)
    {
        LitProjectionColorAccumulator accumulator = new(leaves, rest, width, height);
        accumulator.Accumulate(views, images);
        return accumulator.Finish();
    }
}
