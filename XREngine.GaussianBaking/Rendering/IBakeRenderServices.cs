using XREngine.Rendering.Models.Gaussian.Baking.Build;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Rendering.Models.Gaussian.Baking.Video;
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

    /// <summary>
    /// Runs work that reads or changes the content of <paramref name="input"/>, for example a pose for a capture or the
    /// sampling of the animation frames. The content is in the render world before the work starts, and no update of
    /// that world runs at the same time as the work.
    /// </summary>
    /// <remarks>
    /// The editor runs the work on the update thread of the engine. A pose that the work sets stays until other work
    /// changes it, so a capture after the work shows that pose. Do not start a call from inside the work.
    /// </remarks>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="input">The cooked content that the work uses.</param>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Stops the wait for the work.</param>
    /// <returns>The result of <paramref name="work"/>. An exception of the work goes to the caller.</returns>
    Task<T> RunOnContentThreadAsync<T>(CookedBakeInput input, Func<T> work, CancellationToken cancellationToken);

    /// <summary>
    /// Records the preview loop of a cache: the four videos and the two posters of <see cref="LoopRendition.All"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="XREngine.Components.Scene.Mesh.GaussianCacheComponent"/> plays the cache in the render world of the
    /// input: the stage world of an avatar or an item, or the world itself (with its own lighting). The component holds
    /// a copy of the cache with all chunks accepted before frame 0, and its <c>Playing</c> value is false. Only the
    /// component is drawn: the splat colors are baked, so the original content is not part of the loop images.
    /// </para>
    /// <para>
    /// For each frame <c>i</c>, the component time is set to <c>i / fps</c> and pose <c>i</c> is rendered at
    /// 1920 × 1080. <see cref="Imaging.BakeTonemapper"/> converts the frame to 8-bit sRGB, and the frame goes to the four
    /// video sessions (the 480p sessions scale it down). The posters show frame 0. At most a few frames are in memory
    /// at a time.
    /// </para>
    /// </remarks>
    /// <param name="input">The cooked content that the cache was baked from.</param>
    /// <param name="cache">The cache to play.</param>
    /// <param name="poses">One camera pose for each video frame, from <see cref="LoopCameraPlanner.Plan"/>.</param>
    /// <param name="fps">The video frame rate. Frame <c>i</c> shows the cache at <c>i / fps</c> seconds.</param>
    /// <param name="encoder">The ffmpeg encoder.</param>
    /// <param name="outputDirectory">The folder that receives the files. The method creates it when it does not exist.</param>
    /// <param name="cancellationToken">Stops the recording and the ffmpeg processes.</param>
    /// <exception cref="GaussianBakeException">
    /// The render failed (<see cref="GaussianBakeFailureCodes.CaptureFailed"/>), or ffmpeg failed
    /// (<see cref="GaussianBakeFailureCodes.EncodeFailed"/>).
    /// </exception>
    Task RecordLoopAsync(
        CookedBakeInput input,
        GaussianCacheBuildResult cache,
        IReadOnlyList<BakeCameraPose> poses,
        float fps,
        FfmpegEncoder encoder,
        string outputDirectory,
        CancellationToken cancellationToken);
}
