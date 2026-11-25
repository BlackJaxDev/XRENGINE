using System;

namespace XREngine.Rendering.UI;

/// <summary>
/// Timing and curve parameters for animating a group of UI style channels.
/// </summary>
public readonly struct UITransitionSpec : IEquatable<UITransitionSpec>
{
    /// <summary>
    /// Duration in seconds for timed curves. Ignored when <see cref="Curve"/> is <see cref="EUITransitionCurve.Spring"/>.
    /// </summary>
    public float DurationSeconds { get; init; }

    /// <summary>
    /// Easing or spring curve applied to the transition.
    /// </summary>
    public EUITransitionCurve Curve { get; init; }

    /// <summary>
    /// Spring stiffness coefficient when <see cref="Curve"/> is <see cref="EUITransitionCurve.Spring"/>.
    /// </summary>
    public float SpringStiffness { get; init; }

    /// <summary>
    /// Spring damping coefficient when <see cref="Curve"/> is <see cref="EUITransitionCurve.Spring"/>.
    /// </summary>
    public float SpringDamping { get; init; }

    /// <summary>
    /// Gets a zero-duration immediate transition.
    /// </summary>
    public static UITransitionSpec Instant => new()
    {
        DurationSeconds = 0.0f,
        Curve = EUITransitionCurve.Linear,
    };

    /// <summary>
    /// Creates an Apple-style ease-out transition using cubic-bezier(0.22, 1.0, 0.36, 1.0).
    /// </summary>
    public static UITransitionSpec AppleEaseOut(float durationSeconds)
        => new()
        {
            DurationSeconds = MathF.Max(0.0f, durationSeconds),
            Curve = EUITransitionCurve.AppleEaseOut,
        };

    /// <summary>
    /// Creates a cubic ease-out transition with the given duration in seconds.
    /// </summary>
    public static UITransitionSpec EaseOut(float durationSeconds)
        => new()
        {
            DurationSeconds = MathF.Max(0.0f, durationSeconds),
            Curve = EUITransitionCurve.EaseOutCubic,
        };

    /// <summary>
    /// Creates a damped spring transition with configurable stiffness and damping.
    /// </summary>
    public static UITransitionSpec Spring(float stiffness = 400.0f, float damping = 32.0f)
        => new()
        {
            DurationSeconds = 0.0f,
            Curve = EUITransitionCurve.Spring,
            SpringStiffness = MathF.Max(1.0f, stiffness),
            SpringDamping = MathF.Max(1.0f, damping),
        };

    /// <summary>
    /// Evaluates a timed easing curve for normalized time <paramref name="t"/> in [0, 1].
    /// </summary>
    public float EvaluateTimed(float t)
    {
        float u = Math.Clamp(t, 0.0f, 1.0f);
        return Curve switch
        {
            EUITransitionCurve.Linear => u,
            EUITransitionCurve.EaseOutCubic => 1.0f - MathF.Pow(1.0f - u, 3.0f),
            EUITransitionCurve.EaseInOutCubic => u < 0.5f
                ? 4.0f * u * u * u
                : 1.0f - MathF.Pow(-2.0f * u + 2.0f, 3.0f) * 0.5f,
            EUITransitionCurve.AppleEaseOut => EvaluateCubicBezierY(u, 0.22f, 1.0f, 0.36f, 1.0f),
            _ => u,
        };
    }

    private static float EvaluateCubicBezierY(float x, float x1, float y1, float x2, float y2)
    {
        if (x <= 0.0f)
            return 0.0f;
        if (x >= 1.0f)
            return 1.0f;

        // Solve bezierX(s) = x using Newton-Raphson iterations with safe clamping.
        float s = x;
        for (int i = 0; i < 5; i++)
        {
            float bx = SampleBezier(s, x1, x2) - x;
            float dx = SampleBezierDerivative(s, x1, x2);
            if (MathF.Abs(dx) < 1e-5f)
                break;
            s = Math.Clamp(s - (bx / dx), 0.0f, 1.0f);
        }

        return SampleBezier(s, y1, y2);
    }

    private static float SampleBezier(float t, float p1, float p2)
    {
        float inv = 1.0f - t;
        return (3.0f * inv * inv * t * p1) + (3.0f * inv * t * t * p2) + (t * t * t);
    }

    private static float SampleBezierDerivative(float t, float p1, float p2)
    {
        float inv = 1.0f - t;
        return (3.0f * inv * inv * p1) + (6.0f * inv * t * (p2 - p1)) + (3.0f * t * t * (1.0f - p2));
    }

    public bool Equals(UITransitionSpec other)
        => DurationSeconds.Equals(other.DurationSeconds) &&
           Curve == other.Curve &&
           SpringStiffness.Equals(other.SpringStiffness) &&
           SpringDamping.Equals(other.SpringDamping);

    public override bool Equals(object? obj)
        => obj is UITransitionSpec other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(DurationSeconds, (int)Curve, SpringStiffness, SpringDamping);

    public static bool operator ==(UITransitionSpec left, UITransitionSpec right) => left.Equals(right);

    public static bool operator !=(UITransitionSpec left, UITransitionSpec right) => !left.Equals(right);
}
