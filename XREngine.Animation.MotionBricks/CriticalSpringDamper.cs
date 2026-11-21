namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Allocation-free critically-damped spring dampers matching NVIDIA MotionBricks'
/// <c>critical_spring_damper_exact</c> and <c>critical_spring_angle</c> smoothing.
/// </summary>
public static class CriticalSpringDamper
{
    private const float Ln2Times2 = 1.3862943611198906f; // 2 * ln(2)

    /// <summary>
    /// Advances a scalar value and its velocity toward <paramref name="goal"/> with a
    /// critically-damped spring parameterized by <paramref name="halflifeSeconds"/>.
    /// </summary>
    public static void StepScalar(
        ref float current,
        ref float velocity,
        float goal,
        float halflifeSeconds,
        float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0.0f)
            return;

        if (!float.IsFinite(halflifeSeconds) || halflifeSeconds <= 1.0e-5f)
        {
            current = goal;
            velocity = 0.0f;
            return;
        }

        float damping = Ln2Times2 / halflifeSeconds;
        float offset = current - goal;
        float j1 = velocity + offset * damping;
        float decay = MathF.Exp(-damping * deltaTime);

        current = decay * (offset + j1 * deltaTime) + goal;
        velocity = decay * (velocity - j1 * damping * deltaTime);
    }

    /// <summary>
    /// Advances a planar heading angle in radians toward <paramref name="goalRadians"/>
    /// along the shortest angular arc.
    /// </summary>
    public static void StepAngle(
        ref float currentRadians,
        ref float angularVelocity,
        float goalRadians,
        float halflifeSeconds,
        float deltaTime)
    {
        float delta = WrapAngleRadians(goalRadians - currentRadians);
        float unwrappedGoal = currentRadians + delta;
        StepScalar(ref currentRadians, ref angularVelocity, unwrappedGoal, halflifeSeconds, deltaTime);
        currentRadians = WrapAngleRadians(currentRadians);
    }

    /// <summary>
    /// Wraps an angle in radians into <c>(-PI, PI]</c>.
    /// </summary>
    public static float WrapAngleRadians(float radians)
    {
        if (!float.IsFinite(radians))
            return 0.0f;

        float wrapped = MathF.Atan2(MathF.Sin(radians), MathF.Cos(radians));
        return wrapped;
    }
}
