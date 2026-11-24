namespace XREngine.Cutscenes;

/// <summary>Describes how a curve finds a value between two keys.</summary>
public enum ECutsceneCurveInterpolation
{
    // Linear is the first member so that it is the default value of the enum.
    // YAML leaves out default values, so the default of a curve must match this value.

    /// <summary>The curve blends between the two keys in a straight line.</summary>
    Linear,
    /// <summary>The curve keeps the value of the earlier key until the next key.</summary>
    Step,
}
