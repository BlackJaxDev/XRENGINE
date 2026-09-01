namespace XREngine.Fbx;

/// <summary>
/// Converts declared FBX source units into the units represented by an imported model.
/// </summary>
public static class FbxModelUnitScale
{
    private const double CentimetersPerMeter = 100.0d;

    /// <summary>
    /// Resolves imported model units per meter from FBX <c>UnitScaleFactor</c>, which
    /// is expressed as centimeters per source unit, and the effective import scale.
    /// </summary>
    public static bool TryResolveModelUnitsPerMeter(
        FbxAxisSystem axisSystem,
        float scaleConversion,
        out float modelUnitsPerMeter)
    {
        ArgumentNullException.ThrowIfNull(axisSystem);

        double centimetersPerSourceUnit = axisSystem.UnitScaleFactor;
        if (!double.IsFinite(centimetersPerSourceUnit)
            || centimetersPerSourceUnit <= 0.0d
            || !float.IsFinite(scaleConversion)
            || scaleConversion <= 0.0f)
        {
            modelUnitsPerMeter = 0.0f;
            return false;
        }

        double resolved = CentimetersPerMeter / centimetersPerSourceUnit * scaleConversion;
        if (!double.IsFinite(resolved) || resolved <= 0.0d || resolved > float.MaxValue)
        {
            modelUnitsPerMeter = 0.0f;
            return false;
        }

        modelUnitsPerMeter = (float)resolved;
        return true;
    }
}
