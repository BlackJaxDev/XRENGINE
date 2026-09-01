namespace XREngine.Components.Animation;

/// <summary>
/// Authors the public humanoid mass hierarchy as explicit, versioned body-center
/// points and half-segments. Runtime playback consumes only the persisted result.
/// </summary>
internal static class HumanoidAvatarBodyDefinitionFactory
{
    private const float TotalMass = 82.5f;
    private const string LegacyApproximationModelId = "XRE.SkeletalMassApproximation.v1";
    public const string DefaultModelId = "XRE.PublicHumanoidMassHierarchy.v1";

    /// <summary>
    /// Whether a definition is an engine-generated preset which must be rebuilt
    /// when the preset algorithm changes. Custom authoring uses a distinct model ID.
    /// </summary>
    public static bool IsGeneratedModelId(string? modelId)
        => string.Equals(modelId, DefaultModelId, StringComparison.Ordinal)
        || string.Equals(modelId, LegacyApproximationModelId, StringComparison.Ordinal);

    public static HumanoidAvatarBodyDefinition? CreateDefault(HumanoidAvatarBoneBinding[] bindings)
    {
        ReadOnlySpan<EHumanoidAvatarBoneRole> required =
        [
            EHumanoidAvatarBoneRole.Hips, EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.Head,
            EHumanoidAvatarBoneRole.LeftUpperArm, EHumanoidAvatarBoneRole.LeftLowerArm, EHumanoidAvatarBoneRole.LeftHand,
            EHumanoidAvatarBoneRole.RightUpperArm, EHumanoidAvatarBoneRole.RightLowerArm, EHumanoidAvatarBoneRole.RightHand,
            EHumanoidAvatarBoneRole.LeftUpperLeg, EHumanoidAvatarBoneRole.LeftLowerLeg, EHumanoidAvatarBoneRole.LeftFoot,
            EHumanoidAvatarBoneRole.RightUpperLeg, EHumanoidAvatarBoneRole.RightLowerLeg, EHumanoidAvatarBoneRole.RightFoot,
        ];
        for (int i = 0; i < required.Length; i++)
            if (!HasRole(bindings, required[i]))
                return null;

        bool hasChest = HasRole(bindings, EHumanoidAvatarBoneRole.Chest);
        bool hasUpperChest = HasRole(bindings, EHumanoidAvatarBoneRole.UpperChest);
        bool hasNeck = HasRole(bindings, EHumanoidAvatarBoneRole.Neck);
        bool hasLeftShoulder = HasRole(bindings, EHumanoidAvatarBoneRole.LeftShoulder);
        bool hasRightShoulder = HasRole(bindings, EHumanoidAvatarBoneRole.RightShoulder);
        EHumanoidAvatarBoneRole highestTorso = hasUpperChest
            ? EHumanoidAvatarBoneRole.UpperChest
            : hasChest ? EHumanoidAvatarBoneRole.Chest : EHumanoidAvatarBoneRole.Spine;
        EHumanoidAvatarBoneRole neckLandmark = hasNeck
            ? EHumanoidAvatarBoneRole.Neck
            : EHumanoidAvatarBoneRole.Head;
        EHumanoidAvatarBoneRole leftShoulderLandmark = hasLeftShoulder
            ? EHumanoidAvatarBoneRole.LeftShoulder
            : EHumanoidAvatarBoneRole.LeftUpperArm;
        EHumanoidAvatarBoneRole rightShoulderLandmark = hasRightShoulder
            ? EHumanoidAvatarBoneRole.RightShoulder
            : EHumanoidAvatarBoneRole.RightUpperArm;

        var segments = new List<HumanoidAvatarBodySegment>(24);

        // The branch masses occupy their branch geometry rather than a single
        // arbitrary parent-child edge. These equal point contributions express
        // the pelvic triangle and thoracic four-point center without runtime scans.
        AddPointMass(segments, EHumanoidAvatarBoneRole.LeftUpperLeg, 4.0f);
        AddPointMass(segments, EHumanoidAvatarBoneRole.RightUpperLeg, 4.0f);
        AddPointMass(segments, EHumanoidAvatarBoneRole.Spine, 4.0f);

        float highestTorsoMass = 12.0f;
        if (!hasUpperChest)
            highestTorsoMass += 12.0f;
        if (!hasChest)
            highestTorsoMass += 2.5f;
        if (!hasNeck)
            highestTorsoMass += 1.0f;
        if (!hasLeftShoulder)
            highestTorsoMass += 0.5f;
        if (!hasRightShoulder)
            highestTorsoMass += 0.5f;
        float thoracicPointMass = highestTorsoMass * 0.25f;
        AddPointMass(segments, highestTorso, thoracicPointMass);
        AddPointMass(segments, neckLandmark, thoracicPointMass);
        AddPointMass(segments, leftShoulderLandmark, thoracicPointMass);
        AddPointMass(segments, rightShoulderLandmark, thoracicPointMass);

        if (hasChest)
            AddHalfSegment(segments, EHumanoidAvatarBoneRole.Spine, EHumanoidAvatarBoneRole.Chest, 2.5f);
        if (hasUpperChest)
            AddHalfSegment(segments, EHumanoidAvatarBoneRole.Chest, EHumanoidAvatarBoneRole.UpperChest, 12.0f);
        if (hasNeck)
            AddHalfSegment(segments, EHumanoidAvatarBoneRole.Neck, EHumanoidAvatarBoneRole.Head, 1.0f);
        AddPointMass(segments, EHumanoidAvatarBoneRole.Head, 4.0f);

        AddSide(segments, bindings, left: true, hasLeftShoulder);
        AddSide(segments, bindings, left: false, hasRightShoulder);

        return new HumanoidAvatarBodyDefinition
        {
            ModelId = DefaultModelId,
            Segments = segments.ToArray(),
            LeftHip = Point(EHumanoidAvatarBoneRole.LeftUpperLeg),
            RightHip = Point(EHumanoidAvatarBoneRole.RightUpperLeg),
            LeftShoulder = Point(EHumanoidAvatarBoneRole.LeftUpperArm),
            RightShoulder = Point(EHumanoidAvatarBoneRole.RightUpperArm),
            HipOrientationWeight = 0.5f,
            ShoulderOrientationWeight = 0.5f,
        };
    }

    /// <summary>
    /// Evaluates an authored definition on the finalized neutral hierarchy. This
    /// authoring-time path shares the persisted segment equation used at runtime.
    /// </summary>
    public static bool TryCalculateNeutralCenter(
        HumanoidAvatarBodyDefinition definition,
        HumanoidAvatarBoneBinding[] bindings,
        out System.Numerics.Vector3 center)
    {
        center = System.Numerics.Vector3.Zero;
        float massSum = 0.0f;
        HumanoidAvatarBodySegment[] segments = definition.Segments ?? [];
        for (int i = 0; i < segments.Length; i++)
        {
            HumanoidAvatarBodySegment? segment = segments[i];
            if (segment?.Start is null
                || segment.End is null
                || !TryTransformPoint(segment.Start, bindings, out System.Numerics.Vector3 start)
                || !TryTransformPoint(segment.End, bindings, out System.Numerics.Vector3 end)
                || !float.IsFinite(segment.CenterFraction)
                || !float.IsFinite(segment.MassFraction)
                || segment.MassFraction <= 0.0f)
            {
                center = System.Numerics.Vector3.Zero;
                return false;
            }

            center += System.Numerics.Vector3.Lerp(start, end, segment.CenterFraction) * segment.MassFraction;
            massSum += segment.MassFraction;
        }

        if (!float.IsFinite(massSum) || massSum <= 1e-6f)
            return false;
        center /= massSum;
        return float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z);
    }

    private static bool TryTransformPoint(
        HumanoidAvatarBodyPoint point,
        HumanoidAvatarBoneBinding[] bindings,
        out System.Numerics.Vector3 transformed)
    {
        HumanoidAvatarBoneBinding? binding = Find(bindings, point.Role);
        transformed = binding is null
            ? System.Numerics.Vector3.Zero
            : System.Numerics.Vector3.Transform(point.LocalPosition, binding.NeutralWorldTransform);
        return binding is not null
            && float.IsFinite(transformed.X)
            && float.IsFinite(transformed.Y)
            && float.IsFinite(transformed.Z);
    }

    private static void AddSide(
        List<HumanoidAvatarBodySegment> segments,
        HumanoidAvatarBoneBinding[] bindings,
        bool left,
        bool hasShoulder)
    {
        EHumanoidAvatarBoneRole shoulder = left ? EHumanoidAvatarBoneRole.LeftShoulder : EHumanoidAvatarBoneRole.RightShoulder;
        EHumanoidAvatarBoneRole upperArm = left ? EHumanoidAvatarBoneRole.LeftUpperArm : EHumanoidAvatarBoneRole.RightUpperArm;
        EHumanoidAvatarBoneRole lowerArm = left ? EHumanoidAvatarBoneRole.LeftLowerArm : EHumanoidAvatarBoneRole.RightLowerArm;
        EHumanoidAvatarBoneRole hand = left ? EHumanoidAvatarBoneRole.LeftHand : EHumanoidAvatarBoneRole.RightHand;
        EHumanoidAvatarBoneRole thigh = left ? EHumanoidAvatarBoneRole.LeftUpperLeg : EHumanoidAvatarBoneRole.RightUpperLeg;
        EHumanoidAvatarBoneRole shin = left ? EHumanoidAvatarBoneRole.LeftLowerLeg : EHumanoidAvatarBoneRole.RightLowerLeg;
        EHumanoidAvatarBoneRole foot = left ? EHumanoidAvatarBoneRole.LeftFoot : EHumanoidAvatarBoneRole.RightFoot;
        EHumanoidAvatarBoneRole toes = left ? EHumanoidAvatarBoneRole.LeftToes : EHumanoidAvatarBoneRole.RightToes;

        if (hasShoulder)
            AddHalfSegment(segments, shoulder, upperArm, 0.5f);
        AddHalfSegment(segments, upperArm, lowerArm, 2.0f);
        // The public reference uses the left upper-arm/hand chord for this 1.5
        // mass contribution; the right side follows its lower-arm/hand edge.
        AddHalfSegment(segments, left ? upperArm : lowerArm, hand, 1.5f);
        AddPointMass(segments, hand, 0.5f);
        AddHalfSegment(segments, thigh, shin, 10.0f);
        AddHalfSegment(segments, shin, foot, 4.0f);
        if (HasRole(bindings, toes))
        {
            AddHalfSegment(segments, foot, toes, 0.8f);
            AddPointMass(segments, toes, 0.2f);
        }
        else
        {
            AddPointMass(segments, foot, 1.0f);
        }
    }

    private static void AddHalfSegment(
        List<HumanoidAvatarBodySegment> segments,
        EHumanoidAvatarBoneRole start,
        EHumanoidAvatarBoneRole end,
        float mass)
        => segments.Add(new HumanoidAvatarBodySegment
        {
            Start = Point(start),
            End = Point(end),
            CenterFraction = 0.5f,
            MassFraction = mass / TotalMass,
        });

    private static void AddPointMass(
        List<HumanoidAvatarBodySegment> segments,
        EHumanoidAvatarBoneRole role,
        float mass)
        => segments.Add(new HumanoidAvatarBodySegment
        {
            Start = Point(role),
            End = Point(role),
            CenterFraction = 0.0f,
            MassFraction = mass / TotalMass,
        });

    private static HumanoidAvatarBodyPoint Point(EHumanoidAvatarBoneRole role)
        => new() { Role = role };

    private static bool HasRole(HumanoidAvatarBoneBinding[] bindings, EHumanoidAvatarBoneRole role)
        => Find(bindings, role) is { StructuralSha256.Length: > 0 };

    private static HumanoidAvatarBoneBinding? Find(HumanoidAvatarBoneBinding[] bindings, EHumanoidAvatarBoneRole role)
    {
        for (int i = 0; i < bindings.Length; i++)
            if (bindings[i].Role == role)
                return bindings[i];
        return null;
    }
}
