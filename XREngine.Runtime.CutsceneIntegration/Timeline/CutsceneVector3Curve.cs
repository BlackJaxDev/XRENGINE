using System.Numerics;

namespace XREngine.Cutscenes;

/// <summary>
/// A sorted list of Vector3 keys. Sampling uses only the key arrays and the time, so it keeps no history
/// and does not allocate.
/// </summary>
public class CutsceneVector3Curve
{
    /// <summary>How the curve finds a value between two keys.</summary>
    public ECutsceneCurveInterpolation Interpolation { get; set; } = ECutsceneCurveInterpolation.Linear;

    /// <summary>The key times in increasing order.</summary>
    public CutsceneTime[] Times { get; set; } = [];

    /// <summary>The key values. Value <c>i</c> belongs to time <c>i</c>.</summary>
    public Vector3[] Values { get; set; } = [];

    /// <summary>The value that sampling returns when the curve has no keys.</summary>
    public Vector3 DefaultValue { get; set; } = Vector3.Zero;

    /// <summary>Gets the value of the curve at a time.</summary>
    /// <param name="localTime">The time to sample. Times outside the keys give the nearest key value.</param>
    public Vector3 Sample(CutsceneTime localTime)
    {
        CutsceneTime[] times = Times;
        Vector3[] values = Values;
        int keyCount = Math.Min(times.Length, values.Length);
        if (keyCount == 0)
            return DefaultValue;

        CutsceneCurveKeys.Locate(times, keyCount, localTime, Interpolation == ECutsceneCurveInterpolation.Linear, out int index, out float alpha);
        if (alpha <= 0f)
            return values[index];

        return Vector3.Lerp(values[index], values[index + 1], alpha);
    }
}
