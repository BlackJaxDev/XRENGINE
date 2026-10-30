namespace XREngine.Rendering.Models.Gaussian.Baking.Views;

/// <summary>
/// Gives the length and the frame rate of a preview loop. <see cref="LoopCameraPlanner.ChooseTiming"/> makes it.
/// Video frame <c>i</c> is at <c>i / FramesPerSecond</c> seconds, and frame <see cref="FrameCount"/> is frame 0 again.
/// </summary>
/// <param name="DurationSeconds">The length of one loop, in seconds: <c>FrameCount / FramesPerSecond</c>.</param>
/// <param name="FramesPerSecond">The video frame rate, in frames per second.</param>
/// <param name="FrameCount">The number of video frames in one loop.</param>
public readonly record struct LoopTiming(float DurationSeconds, float FramesPerSecond, int FrameCount);
