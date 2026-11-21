namespace XREngine.Animation;

/// <summary>
/// Factory registered by <c>XREngine.Animation.MotionBricks</c> to construct runtime
/// locomotion drivers when an <see cref="AnimStateMachine"/> initializes a
/// <see cref="MotionBricksLocomotionMotion"/>.
/// </summary>
public interface IMotionBricksLocomotionDriverFactory
{
    IMotionBricksLocomotionDriver CreateDriver(MotionBricksLocomotionMotion motion);
}
