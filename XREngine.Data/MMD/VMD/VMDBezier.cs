using System.Numerics;

namespace XREngine.Data.MMD
{
    /// <summary>
    /// Represents a cubic Bezier curve used in VMD (MikuMikuDance) animations
    /// with control points for interpolation between keyframes.
    /// </summary>
    public class VMDBezier(Vector2 cp1, Vector2 cp2)
    {
        /// <summary>
        /// Gets the first control point of the Bezier curve.
        /// </summary>
        public Vector2 StartControlPoint { get; private set; } = cp1;

        /// <summary>
        /// Gets the second control point of the Bezier curve.
        /// </summary>
        public Vector2 EndControlPoint { get; private set; } = cp2;

        /// <summary>
        /// Evaluates the X component of the Bezier curve at the specified parameter t.
        /// Uses the cubic Bezier formula: B(t) = (1-t)³P₀ + 3t(1-t)²P₁ + 3t²(1-t)P₂ + t³P₃
        /// </summary>
        /// <param name="t">The parameter value between 0 and 1</param>
        /// <returns>The X coordinate at parameter t</returns>
        public float EvalX(float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float it = 1.0f - t;
            float it2 = it * it;
            float it3 = it2 * it;
            float[] x = [0, StartControlPoint.X, EndControlPoint.X, 1];
            return t3 * x[3] + 3 * t2 * it * x[2] + 3 * t * it2 * x[1] + it3 * x[0];
        }

        /// <summary>
        /// Evaluates the Y component of the Bezier curve at the specified parameter t.
        /// Uses the cubic Bezier formula: B(t) = (1-t)³P₀ + 3t(1-t)²P₁ + 3t²(1-t)P₂ + t³P₃
        /// </summary>
        /// <param name="t">The parameter value between 0 and 1</param>
        /// <returns>The Y coordinate at parameter t</returns>
        public float EvalY(float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float it = 1.0f - t;
            float it2 = it * it;
            float it3 = it2 * it;
            float[] y = [0, StartControlPoint.Y, EndControlPoint.Y, 1];
            return t3 * y[3] + 3 * t2 * it * y[2] + 3 * t * it2 * y[1] + it3 * y[0];
        }

        /// <summary>
        /// Evaluates both X and Y components of the Bezier curve at the specified parameter t.
        /// </summary>
        /// <param name="t">The parameter value between 0 and 1</param>
        /// <returns>A Vector2 containing both X and Y coordinates at parameter t</returns>
        public Vector2 Eval(float t)
            => new(EvalX(t), EvalY(t));

        /// <summary>
        /// Returns a string representation of the Bezier curve's control points.
        /// </summary>
        /// <returns>A string in the format "&lt;VMDBezier XY0:(x,y), XY1:(x,y)&gt;"</returns>
        public override string ToString()
            => $"<VMDBezier XY0:{StartControlPoint}, XY1:{EndControlPoint}>";

        /// <summary>
        /// Uses binary search to find the parameter t where the X component equals the specified time value.
        /// This is used for finding the interpolation parameter given a normalized time value.
        /// </summary>
        /// <param name="time">The target X value to find the parameter for</param>
        /// <returns>The parameter t where EvalX(t) equals the target time value</returns>
        public float FindBezierX(float time)
        {
            const float e = 0.00001f;
            float start = 0.0f;
            float stop = 1.0f;
            float t = 0.5f;
            float x = EvalX(t);
            while (MathF.Abs(time - x) > e)
            {
                if (time < x)
                    stop = t;
                else
                    start = t;

                t = (stop + start) * 0.5f;
                x = EvalX(t);
            }
            return t;
        }
    }
}
