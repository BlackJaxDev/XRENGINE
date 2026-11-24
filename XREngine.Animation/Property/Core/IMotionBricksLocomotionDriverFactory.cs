namespace XREngine.Animation;

/// <summary>
/// Factory registered by <c>XREngine.Animation.MotionBricks</c> to construct runtime
/// locomotion drivers when an <see cref="AnimStateMachine"/> initializes a
/// <see cref="MotionBricksLocomotionMotion"/>.
/// </summary>
public interface IMotionBricksLocomotionDriverFactory
{
    IMotionBricksLocomotionDriver CreateDriver(MotionBricksLocomotionMotion motion);

    /// <summary>
    /// Starts asynchronous runtime warmup (such as loading cached TensorRT plans) off the
    /// application thread so the first locomotion activation does not stall a frame.
    /// </summary>
    void Prewarm()
    {
    }
}
