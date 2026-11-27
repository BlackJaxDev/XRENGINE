namespace XREngine.Animation;

/// <summary>
/// Process-wide registration seam that connects <see cref="MotionBricksLocomotionMotion"/>
/// in <c>XREngine.Animation</c> to the optional TensorRT runtime backend in
/// <c>XREngine.Animation.MotionBricks</c> without introducing an upward project dependency.
/// </summary>
public static class MotionBricksLocomotionRuntimeServices
{
    private static IMotionBricksLocomotionDriverFactory? _currentFactory;

    public static IMotionBricksLocomotionDriverFactory? CurrentFactory
    {
        get => Volatile.Read(ref _currentFactory);
        set => Volatile.Write(ref _currentFactory, value);
    }

    /// <summary>
    /// Triggers optional background warmup on the registered locomotion factory without
    /// blocking the calling thread.
    /// </summary>
    public static void Prewarm()
        => Volatile.Read(ref _currentFactory)?.Prewarm();
}
