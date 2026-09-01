using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Immutable, allocation-free evaluator for an authored avatar body frame in
/// model-root coordinates.
/// </summary>
internal sealed class CompiledHumanoidBodyDefinition
{
    internal const float MassSumTolerance = 1.0e-4f;
    private const float DegeneracyScale = 1.0e-4f;

    private readonly CompiledHumanoidBodySegment[] _segments;
    private readonly CompiledHumanoidBodyPoint _leftHip;
    private readonly CompiledHumanoidBodyPoint _rightHip;
    private readonly CompiledHumanoidBodyPoint _leftShoulder;
    private readonly CompiledHumanoidBodyPoint _rightShoulder;
    private readonly Matrix4x4 _inverseNeutralRawRotation;
    private readonly float _neutralLandmarkScale;

    private CompiledHumanoidBodyDefinition(
        int algorithmVersion,
        string modelId,
        CompiledHumanoidBodySegment[] segments,
        CompiledHumanoidBodyPoint leftHip,
        CompiledHumanoidBodyPoint rightHip,
        CompiledHumanoidBodyPoint leftShoulder,
        CompiledHumanoidBodyPoint rightShoulder,
        float hipOrientationWeight,
        float shoulderOrientationWeight,
        Matrix4x4 inverseNeutralRawRotation,
        float neutralLandmarkScale,
        Matrix4x4 neutralBodyFrame,
        Matrix4x4 inverseNeutralBodyFrame)
    {
        AlgorithmVersion = algorithmVersion;
        ModelId = modelId;
        _segments = segments;
        _leftHip = leftHip;
        _rightHip = rightHip;
        _leftShoulder = leftShoulder;
        _rightShoulder = rightShoulder;
        HipOrientationWeight = hipOrientationWeight;
        ShoulderOrientationWeight = shoulderOrientationWeight;
        _inverseNeutralRawRotation = inverseNeutralRawRotation;
        _neutralLandmarkScale = neutralLandmarkScale;
        NeutralBodyFrame = neutralBodyFrame;
        InverseNeutralBodyFrame = inverseNeutralBodyFrame;
    }

    public int AlgorithmVersion { get; }
    public string ModelId { get; }
    public float HipOrientationWeight { get; }
    public float ShoulderOrientationWeight { get; }
    public Matrix4x4 NeutralBodyFrame { get; }
    public Matrix4x4 InverseNeutralBodyFrame { get; }
    internal ReadOnlySpan<CompiledHumanoidBodySegment> Segments => _segments;

    /// <summary>
    /// Validates and compiles explicit body-center and landmark data. The input
    /// plan and neutral matrices are role-indexed in model-root coordinates.
    /// </summary>
    public static bool TryCompile(
        HumanoidAvatarBodyDefinition? source,
        ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans,
        ReadOnlySpan<Matrix4x4> neutralModelRootMatrices,
        out CompiledHumanoidBodyDefinition compiled,
        out string diagnostic)
    {
        compiled = null!;
        diagnostic = string.Empty;

        if (source is null)
            return Fail("Body definition is missing.", out diagnostic);
        if (source.AlgorithmVersion != HumanoidAvatarBodyDefinition.CurrentAlgorithmVersion)
            return Fail($"Body algorithm version {source.AlgorithmVersion} is unsupported; expected {HumanoidAvatarBodyDefinition.CurrentAlgorithmVersion}.", out diagnostic);
        if (string.IsNullOrWhiteSpace(source.ModelId))
            return Fail("Body model ID is missing.", out diagnostic);
        if (plans.Length == 0 || neutralModelRootMatrices.Length != plans.Length)
            return Fail("Body compilation requires equally sized non-empty role plans and neutral model-root matrices.", out diagnostic);
        if (source.Segments is null || source.Segments.Length == 0)
            return Fail("Body definition requires explicitly authored segments.", out diagnostic);
        if (!float.IsFinite(source.HipOrientationWeight) || source.HipOrientationWeight < 0.0f
            || !float.IsFinite(source.ShoulderOrientationWeight) || source.ShoulderOrientationWeight < 0.0f)
            return Fail("Body orientation weights must be finite, non-negative, and have a positive sum.", out diagnostic);

        float orientationWeightSum = source.HipOrientationWeight + source.ShoulderOrientationWeight;
        if (!float.IsFinite(orientationWeightSum) || orientationWeightSum <= 0.0f)
            return Fail("Body orientation weights must have a finite positive sum.", out diagnostic);
        float hipOrientationWeight = source.HipOrientationWeight / orientationWeightSum;
        float shoulderOrientationWeight = source.ShoulderOrientationWeight / orientationWeightSum;

        if (!TryCompilePoint(source.LeftHip, "left hip", plans, out CompiledHumanoidBodyPoint leftHip, out diagnostic)
            || !TryCompilePoint(source.RightHip, "right hip", plans, out CompiledHumanoidBodyPoint rightHip, out diagnostic)
            || !TryCompilePoint(source.LeftShoulder, "left shoulder", plans, out CompiledHumanoidBodyPoint leftShoulder, out diagnostic)
            || !TryCompilePoint(source.RightShoulder, "right shoulder", plans, out CompiledHumanoidBodyPoint rightShoulder, out diagnostic))
            return false;

        if (!IsDescendantOfHips(plans, leftHip.RoleIndex)
            || !IsDescendantOfHips(plans, rightHip.RoleIndex)
            || !IsDescendantOfHips(plans, leftShoulder.RoleIndex)
            || !IsDescendantOfHips(plans, rightShoulder.RoleIndex))
            return Fail("All body landmark roles must be mapped descendants of Hips.", out diagnostic);
        if (!IsFiniteInvertibleAffine(neutralModelRootMatrices[leftHip.RoleIndex])
            || !IsFiniteInvertibleAffine(neutralModelRootMatrices[rightHip.RoleIndex])
            || !IsFiniteInvertibleAffine(neutralModelRootMatrices[leftShoulder.RoleIndex])
            || !IsFiniteInvertibleAffine(neutralModelRootMatrices[rightShoulder.RoleIndex]))
            return Fail("A neutral body landmark matrix is not finite, invertible, and affine.", out diagnostic);

        var segments = new CompiledHumanoidBodySegment[source.Segments.Length];
        float massSum = 0.0f;
        for (int i = 0; i < source.Segments.Length; i++)
        {
            HumanoidAvatarBodySegment? segment = source.Segments[i];
            if (segment is null)
                return Fail($"Body segment {i} is missing.", out diagnostic);

            if (!TryCompilePoint(segment.Start, $"segment {i} start", plans, out CompiledHumanoidBodyPoint start, out diagnostic)
                || !TryCompilePoint(segment.End, $"segment {i} end", plans, out CompiledHumanoidBodyPoint end, out diagnostic))
                return false;
            if (!IsDescendantOfHips(plans, start.RoleIndex) || !IsDescendantOfHips(plans, end.RoleIndex))
                return Fail($"Body segment {i} endpoints must be mapped descendants of Hips.", out diagnostic);
            if (!IsFiniteInvertibleAffine(neutralModelRootMatrices[start.RoleIndex])
                || !IsFiniteInvertibleAffine(neutralModelRootMatrices[end.RoleIndex]))
                return Fail($"Body segment {i} references a non-invertible neutral model-root matrix.", out diagnostic);
            if (!float.IsFinite(segment.CenterFraction) || segment.CenterFraction is < 0.0f or > 1.0f)
                return Fail($"Body segment {i} has an invalid center fraction.", out diagnostic);
            if (!float.IsFinite(segment.MassFraction) || segment.MassFraction <= 0.0f)
                return Fail($"Body segment {i} has an invalid mass fraction.", out diagnostic);

            massSum += segment.MassFraction;
            if (!float.IsFinite(massSum))
                return Fail("Body segment mass sum is not finite.", out diagnostic);

            segments[i] = new CompiledHumanoidBodySegment(start, end, segment.CenterFraction, segment.MassFraction);
        }

        if (!float.IsFinite(massSum) || MathF.Abs(massSum - 1.0f) > MassSumTolerance)
            return Fail("Body segment mass fractions must sum to one.", out diagnostic);

        if (massSum != 1.0f)
            for (int i = 0; i < segments.Length; i++)
            {
                CompiledHumanoidBodySegment segment = segments[i];
                segments[i] = new CompiledHumanoidBodySegment(
                    segment.Start,
                    segment.End,
                    segment.CenterFraction,
                    segment.MassFraction / massSum);
            }

        if (!TryCalculateCenter(segments, neutralModelRootMatrices, out Vector3 neutralCenter))
            return Fail("Neutral body center is not finite.", out diagnostic);
        if (!TryCalculateRawRotation(
                leftHip,
                rightHip,
                leftShoulder,
                rightShoulder,
                hipOrientationWeight,
                shoulderOrientationWeight,
                neutralModelRootMatrices,
                0.0f,
                out Matrix4x4 neutralRawRotation,
                out float neutralLandmarkScale))
            return Fail("Neutral body landmarks cannot form a finite, non-degenerate orientation.", out diagnostic);
        if (!Matrix4x4.Invert(neutralRawRotation, out Matrix4x4 inverseNeutralRawRotation))
            return Fail("Neutral body orientation is not invertible.", out diagnostic);

        Matrix4x4 neutralBodyFrame = Matrix4x4.CreateTranslation(neutralCenter);
        if (!Matrix4x4.Invert(neutralBodyFrame, out Matrix4x4 inverseNeutralBodyFrame))
            return Fail("Neutral body frame is not invertible.", out diagnostic);

        compiled = new CompiledHumanoidBodyDefinition(
            source.AlgorithmVersion,
            source.ModelId,
            segments,
            leftHip,
            rightHip,
            leftShoulder,
            rightShoulder,
            hipOrientationWeight,
            shoulderOrientationWeight,
            inverseNeutralRawRotation,
            neutralLandmarkScale,
            neutralBodyFrame,
            inverseNeutralBodyFrame);
        return true;
    }

    /// <summary>Evaluates the current model-root body frame without allocating.</summary>
    public bool TryEvaluate(ReadOnlySpan<Matrix4x4> modelRootMatrices, out Matrix4x4 bodyFrame)
    {
        bodyFrame = Matrix4x4.Identity;
        if (modelRootMatrices.Length == 0)
            return false;

        for (int i = 0; i < _segments.Length; i++)
        {
            CompiledHumanoidBodySegment segment = _segments[i];
            if (!IsPointMatrixAffine(segment.Start, modelRootMatrices)
                || !IsPointMatrixAffine(segment.End, modelRootMatrices))
                return false;
        }

        if (!ArePointMatricesAffine(modelRootMatrices)
            || !TryCalculateCenter(_segments, modelRootMatrices, out Vector3 center)
            || !TryCalculateRawRotation(
                _leftHip,
                _rightHip,
                _leftShoulder,
                _rightShoulder,
                HipOrientationWeight,
                ShoulderOrientationWeight,
                modelRootMatrices,
                _neutralLandmarkScale,
                out Matrix4x4 rawRotation,
                out _))
            return false;

        Matrix4x4 rotation = _inverseNeutralRawRotation * rawRotation;
        bodyFrame = rotation * Matrix4x4.CreateTranslation(center);
        return HumanoidBodyFrameMath.IsRigid(bodyFrame) && Matrix4x4.Invert(bodyFrame, out _);
    }

    private static bool TryCompilePoint(
        HumanoidAvatarBodyPoint? source,
        string name,
        ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans,
        out CompiledHumanoidBodyPoint compiled,
        out string diagnostic)
    {
        compiled = default;
        diagnostic = string.Empty;
        if (source is null)
            return Fail($"Body {name} point is missing.", out diagnostic);
        if (!IsFinite(source.LocalPosition))
            return Fail($"Body {name} point has a non-finite local position.", out diagnostic);
        if (!TryGetMappedRoleIndex(source.Role, plans, out int roleIndex))
            return Fail($"Body {name} point references a missing or unmapped role.", out diagnostic);

        compiled = new CompiledHumanoidBodyPoint(roleIndex, source.LocalPosition);
        return true;
    }

    private static bool TryCalculateCenter(
        ReadOnlySpan<CompiledHumanoidBodySegment> segments,
        ReadOnlySpan<Matrix4x4> modelRootMatrices,
        out Vector3 center)
    {
        center = Vector3.Zero;
        for (int i = 0; i < segments.Length; i++)
        {
            CompiledHumanoidBodySegment segment = segments[i];
            if (!TryTransformPoint(segment.Start, modelRootMatrices, out Vector3 start)
                || !TryTransformPoint(segment.End, modelRootMatrices, out Vector3 end))
                return false;
            Vector3 contribution = Vector3.Lerp(start, end, segment.CenterFraction) * segment.MassFraction;
            if (!IsFinite(start) || !IsFinite(end) || !IsFinite(contribution))
                return false;

            center += contribution;
            if (!IsFinite(center))
                return false;
        }

        return true;
    }

    private static bool TryCalculateRawRotation(
        in CompiledHumanoidBodyPoint leftHip,
        in CompiledHumanoidBodyPoint rightHip,
        in CompiledHumanoidBodyPoint leftShoulder,
        in CompiledHumanoidBodyPoint rightShoulder,
        float hipWeight,
        float shoulderWeight,
        ReadOnlySpan<Matrix4x4> modelRootMatrices,
        float minimumScale,
        out Matrix4x4 rotation,
        out float landmarkScale)
    {
        rotation = Matrix4x4.Identity;
        landmarkScale = 0.0f;
        if (!TryTransformPoint(leftHip, modelRootMatrices, out Vector3 leftHipPosition)
            || !TryTransformPoint(rightHip, modelRootMatrices, out Vector3 rightHipPosition)
            || !TryTransformPoint(leftShoulder, modelRootMatrices, out Vector3 leftShoulderPosition)
            || !TryTransformPoint(rightShoulder, modelRootMatrices, out Vector3 rightShoulderPosition))
            return false;

        Vector3 hipSide = rightHipPosition - leftHipPosition;
        Vector3 shoulderSide = rightShoulderPosition - leftShoulderPosition;
        Vector3 upVector = ((leftShoulderPosition + rightShoulderPosition) - (leftHipPosition + rightHipPosition)) * 0.5f;
        float hipLength = hipSide.Length();
        float shoulderLength = shoulderSide.Length();
        float upLength = upVector.Length();
        landmarkScale = MathF.Max(upLength, MathF.Max(hipLength, shoulderLength));
        if (!float.IsFinite(landmarkScale) || landmarkScale <= 0.0f)
            return false;

        float threshold = MathF.Max(landmarkScale, minimumScale) * DegeneracyScale;
        if (!TryNormalize(upVector, threshold, out Vector3 up))
            return false;

        Vector3 sideVector = hipSide * hipWeight + shoulderSide * shoulderWeight;
        if (!IsFinite(sideVector) || sideVector.Length() <= threshold)
            return false;

        Vector3 forwardVector = Vector3.Cross(sideVector, up);
        if (!TryNormalize(forwardVector, sideVector.Length() * DegeneracyScale, out Vector3 forward))
            return false;
        if (!TryNormalize(Vector3.Cross(up, forward), DegeneracyScale, out Vector3 right))
            return false;

        rotation = new Matrix4x4(
            right.X, right.Y, right.Z, 0.0f,
            up.X, up.Y, up.Z, 0.0f,
            forward.X, forward.Y, forward.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
        return HumanoidBodyFrameMath.IsRigid(rotation);
    }

    private static bool TryTransformPoint(in CompiledHumanoidBodyPoint point, ReadOnlySpan<Matrix4x4> matrices, out Vector3 position)
    {
        position = Vector3.Zero;
        if ((uint)point.RoleIndex >= (uint)matrices.Length || !IsFiniteInvertibleAffine(matrices[point.RoleIndex]))
            return false;

        position = Vector3.Transform(point.LocalPosition, matrices[point.RoleIndex]);
        return IsFinite(position);
    }

    private bool ArePointMatricesAffine(ReadOnlySpan<Matrix4x4> matrices)
        => IsPointMatrixAffine(_leftHip, matrices)
        && IsPointMatrixAffine(_rightHip, matrices)
        && IsPointMatrixAffine(_leftShoulder, matrices)
        && IsPointMatrixAffine(_rightShoulder, matrices);

    private static bool IsPointMatrixAffine(in CompiledHumanoidBodyPoint point, ReadOnlySpan<Matrix4x4> matrices)
        => (uint)point.RoleIndex < (uint)matrices.Length && IsFiniteInvertibleAffine(matrices[point.RoleIndex]);

    private static bool TryGetMappedRoleIndex(EHumanoidAvatarBoneRole role, ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans, out int roleIndex)
    {
        roleIndex = (int)role;
        return (uint)roleIndex < (uint)plans.Length && IsMappedPlan(plans, roleIndex);
    }

    private static bool IsMappedPlan(ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans, int roleIndex)
        => (uint)roleIndex < (uint)plans.Length
        && plans[roleIndex].Role == (EHumanoidAvatarBoneRole)roleIndex
        && plans[roleIndex].Node is not null;

    private static bool IsDescendantOfHips(ReadOnlySpan<CompiledHumanoidBoneSolvePlan> plans, int roleIndex)
    {
        int hipsIndex = (int)EHumanoidAvatarBoneRole.Hips;
        for (int steps = 0; (uint)roleIndex < (uint)plans.Length && steps <= plans.Length; steps++)
        {
            if (!IsMappedPlan(plans, roleIndex))
                return false;
            if (roleIndex == hipsIndex)
                return true;

            roleIndex = plans[roleIndex].MappedAncestorPlanIndex;
        }

        return false;
    }

    private static bool TryNormalize(Vector3 value, float minimumLength, out Vector3 normalized)
    {
        normalized = Vector3.Zero;
        float length = value.Length();
        if (!float.IsFinite(length) || length <= minimumLength)
            return false;

        normalized = value / length;
        return IsFinite(normalized);
    }

    private static bool IsFiniteInvertibleAffine(Matrix4x4 value)
    {
        if (!HumanoidBodyFrameMath.IsFinite(value)
            || MathF.Abs(value.M14) > 1.0e-4f
            || MathF.Abs(value.M24) > 1.0e-4f
            || MathF.Abs(value.M34) > 1.0e-4f
            || MathF.Abs(value.M44 - 1.0f) > 1.0e-4f)
            return false;

        float determinant = value.GetDeterminant();
        return float.IsFinite(determinant) && determinant > 0.0f
            && Matrix4x4.Invert(value, out Matrix4x4 inverse)
            && HumanoidBodyFrameMath.IsFinite(inverse);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Fail(string message, out string diagnostic)
    {
        diagnostic = message;
        return false;
    }
}
