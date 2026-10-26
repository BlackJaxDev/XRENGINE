using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Rendering.Models.Gaussian.Baking.Rendering;

/// <summary>
/// Gives the bake access to the engine renderer. The editor supplies the implementation; tests can supply a fake.
/// </summary>
public interface IBakeRenderServices
{
    /// <summary>
    /// Renders the input content from each pose with the engine render pipeline and reads back the color.
    /// </summary>
    /// <param name="input">The cooked content to render.</param>
    /// <param name="poses">The camera poses. The aspect ratio of each view is <paramref name="width"/> / <paramref name="height"/>.</param>
    /// <param name="width">The width of each view, in pixels.</param>
    /// <param name="height">The height of each view, in pixels.</param>
    /// <param name="cancellationToken">Stops the capture.</param>
    /// <returns>
    /// One array for each pose, in pose order. Each array holds <paramref name="width"/> × <paramref name="height"/>
    /// linear RGBA pixels as four floats each. The first row is the top of the image.
    /// </returns>
    Task<float[][]> CaptureViewsAsync(
        CookedBakeInput input,
        IReadOnlyList<BakeCameraPose> poses,
        int width,
        int height,
        CancellationToken cancellationToken);
}
