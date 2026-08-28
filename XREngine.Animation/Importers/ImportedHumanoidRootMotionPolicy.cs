namespace XREngine.Animation.Importers;

/// <summary>
/// Immutable, validated execution policy compiled from Unity's serialized clip settings.
/// The serialized DTO remains lossless; evaluators consume this semantic form.
/// </summary>
public readonly record struct ImportedHumanoidRootMotionPolicy(
    float StartTime,
    float StopTime,
    float OrientationOffsetY,
    float Level,
    float CycleOffset,
    bool LoopTime,
    bool LoopPose,
    bool BakeOrientationIntoPose,
    bool BakePositionYIntoPose,
    bool BakePositionXZIntoPose,
    EImportedHumanoidRootOrientationBasis OrientationBasis,
    EImportedHumanoidRootPositionYBasis PositionYBasis,
    EImportedHumanoidRootPositionXZBasis PositionXZBasis,
    bool Mirror)
{
    /// <summary>
    /// Compiles one serialized settings object, rejecting malformed numeric state instead of
    /// allowing a partially applied policy to enter the evaluator.
    /// </summary>
    public static bool TryCreate(
        ImportedHumanoidClipRootMotionSettings settings,
        out ImportedHumanoidRootMotionPolicy policy,
        out string diagnostic)
    {
        if (!float.IsFinite(settings.StartTime)
            || !float.IsFinite(settings.StopTime)
            || !float.IsFinite(settings.OrientationOffsetY)
            || !float.IsFinite(settings.Level)
            || !float.IsFinite(settings.CycleOffset))
        {
            policy = default;
            diagnostic = "Unity humanoid root-motion settings contain a non-finite numeric value.";
            return false;
        }

        if (settings.StartTime < 0.0f || settings.StopTime < settings.StartTime)
        {
            policy = default;
            diagnostic =
                $"Unity humanoid source interval [{settings.StartTime:R}, {settings.StopTime:R}] is invalid.";
            return false;
        }

        if (settings.KeepOriginalPositionY && settings.HeightFromFeet)
        {
            policy = default;
            diagnostic =
                "Unity humanoid root-motion settings select both Original and Feet as the Root Transform Position (Y) basis.";
            return false;
        }

        EImportedHumanoidRootPositionYBasis positionYBasis = settings.KeepOriginalPositionY
            ? EImportedHumanoidRootPositionYBasis.Original
            : settings.HeightFromFeet
                ? EImportedHumanoidRootPositionYBasis.Feet
                : EImportedHumanoidRootPositionYBasis.CenterOfMass;

        policy = new ImportedHumanoidRootMotionPolicy(
            settings.StartTime,
            settings.StopTime,
            settings.OrientationOffsetY,
            settings.Level,
            settings.CycleOffset,
            settings.LoopTime,
            settings.LoopPose,
            settings.BakeOrientationIntoPose,
            settings.BakePositionYIntoPose,
            settings.BakePositionXZIntoPose,
            settings.KeepOriginalOrientation
                ? EImportedHumanoidRootOrientationBasis.Original
                : EImportedHumanoidRootOrientationBasis.Body,
            positionYBasis,
            settings.KeepOriginalPositionXZ
                ? EImportedHumanoidRootPositionXZBasis.Original
                : EImportedHumanoidRootPositionXZBasis.CenterOfMass,
            settings.Mirror);
        diagnostic = string.Empty;
        return true;
    }

    /// <summary>
    /// Unity treats cycle offset as a periodic phase. This returns its stable [0,1) form,
    /// including negative and multi-cycle serialized values.
    /// </summary>
    public float NormalizedCycleOffset
    {
        get
        {
            float phase = CycleOffset - MathF.Floor(CycleOffset);
            return phase >= 1.0f ? 0.0f : phase;
        }
    }
}
