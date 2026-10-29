using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Baking.Surface;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>Gives a color baker the data that it needs.</summary>
public sealed class SplatColorBakeContext
{
    /// <summary>Gets the leaf splats. The baker writes <see cref="BakedSplat.LinearColor"/> in place.</summary>
    public required BakedSplat[] Leaves { get; init; }

    /// <summary>Gets the surface of the content at the rest pose.</summary>
    public required DeformedSurface Rest { get; init; }

    /// <summary>Gets the camera views.</summary>
    public required IReadOnlyList<BakeCameraPose> Views { get; init; }

    /// <summary>Gets the width of each captured image, in pixels.</summary>
    public int Width { get; init; } = 1920;

    /// <summary>Gets the height of each captured image, in pixels.</summary>
    public int Height { get; init; } = 1080;

    /// <summary>
    /// Gets the function that renders the content from the given views. It returns one linear RGBA image for each
    /// view, with <see cref="Width"/> × <see cref="Height"/> × 4 values. The first row is the top row.
    /// </summary>
    public required Func<IReadOnlyList<BakeCameraPose>, CancellationToken, Task<float[][]>> Capture { get; init; }
}
