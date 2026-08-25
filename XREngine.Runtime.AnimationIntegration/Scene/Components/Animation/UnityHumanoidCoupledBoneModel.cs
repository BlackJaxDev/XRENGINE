using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Allocation-free approximation of Unity's coupled humanoid-muscle solution for
/// one bone. Exact measured single-muscle endpoints form the baseline; calibrated
/// polynomial coefficients supply the remaining rotation and stretch residuals.
/// </summary>
public sealed class UnityHumanoidCoupledBoneModel
{
    public string BoneName { get; set; } = string.Empty;
    public EHumanoidValue[] Muscles { get; set; } = [];
    public int MaximumPolynomialDegree { get; set; } = 3;
    public Quaternion[] NegativeEndpointRotations { get; set; } = [];
    public Quaternion[] PositiveEndpointRotations { get; set; } = [];
    public Vector3[] NegativeEndpointPositionDeltas { get; set; } = [];
    public Vector3[] PositiveEndpointPositionDeltas { get; set; } = [];
    public Vector3[] RotationResidualCoefficients { get; set; } = [];
    public Vector3[] PositionResidualCoefficients { get; set; } = [];
    public float[] ProjectedRootYCoefficients { get; set; } = [];
    public float ProjectedRootYZeroOffset { get; set; }
    public float MeanAngularErrorDegrees { get; set; }
    public float MaxAngularErrorDegrees { get; set; }
    public float MeanPositionError { get; set; }
    public float MaxPositionError { get; set; }

    public int ExpectedFeatureCount => CalculateFeatureCount(Muscles.Length, MaximumPolynomialDegree);

    public bool IsValid
        => Muscles.Length > 0
        && Muscles.Length <= UnityHumanoidMuscleMap.OrderedMuscleEntries.Count
        && MaximumPolynomialDegree >= 3
        && NegativeEndpointRotations.Length == Muscles.Length
        && PositiveEndpointRotations.Length == Muscles.Length
        && NegativeEndpointPositionDeltas.Length == Muscles.Length
        && PositiveEndpointPositionDeltas.Length == Muscles.Length
        && RotationResidualCoefficients.Length == ExpectedFeatureCount
        && PositionResidualCoefficients.Length == ExpectedFeatureCount
        && (ProjectedRootYCoefficients.Length == 0 || ProjectedRootYCoefficients.Length == ExpectedFeatureCount);

    /// <summary>
    /// Evaluates the calibrated bind-neutral-relative rotation and engine-unit
    /// local-position delta for the final blended muscle vector.
    /// </summary>
    public void Evaluate(
        ReadOnlySpan<float> muscles,
        float muscleInputScale,
        float engineUnitsPerUnityMeter,
        out Quaternion rotation,
        out Vector3 positionDelta)
    {
        if (!IsValid)
        {
            rotation = Quaternion.Identity;
            positionDelta = Vector3.Zero;
            return;
        }

        Span<float> values = stackalloc float[Muscles.Length];
        Quaternion endpointRotation = Quaternion.Identity;
        Vector3 endpointPosition = Vector3.Zero;
        for (int i = 0; i < Muscles.Length; i++)
        {
            int muscleIndex = (int)Muscles[i];
            float amount = (uint)muscleIndex < (uint)muscles.Length
                ? muscles[muscleIndex] * muscleInputScale
                : 0.0f;
            if (!float.IsFinite(amount))
                amount = 0.0f;
            values[i] = amount;

            if (MathF.Abs(amount) <= 1.0e-7f)
                continue;

            Quaternion endpoint = amount >= 0.0f
                ? PositiveEndpointRotations[i]
                : NegativeEndpointRotations[i];
            endpointRotation = Quaternion.Normalize(
                endpointRotation * UnityHumanoidMuscleResponse.ScaleShortestRotation(endpoint, MathF.Abs(amount)));
            endpointPosition += (amount >= 0.0f
                ? PositiveEndpointPositionDeltas[i]
                : NegativeEndpointPositionDeltas[i]) * MathF.Abs(amount);
        }

        Vector3 rotationResidual = EvaluateFeatures(values, RotationResidualCoefficients, MaximumPolynomialDegree);
        Vector3 positionResidual = EvaluateFeatures(values, PositionResidualCoefficients, MaximumPolynomialDegree);
        rotation = Quaternion.Normalize(endpointRotation * FromRotationVector(rotationResidual));
        positionDelta = (endpointPosition + positionResidual) * engineUnitsPerUnityMeter;
    }

    public bool TryEvaluateProjectedRootY(
        ReadOnlySpan<float> muscles,
        float muscleInputScale,
        float engineUnitsPerUnityMeter,
        out float projectedY)
    {
        if (!IsValid || ProjectedRootYCoefficients.Length != ExpectedFeatureCount)
        {
            projectedY = 0.0f;
            return false;
        }

        Span<float> values = stackalloc float[Muscles.Length];
        for (int i = 0; i < Muscles.Length; i++)
        {
            int muscleIndex = (int)Muscles[i];
            float amount = (uint)muscleIndex < (uint)muscles.Length
                ? muscles[muscleIndex] * muscleInputScale
                : 0.0f;
            values[i] = float.IsFinite(amount) ? amount : 0.0f;
        }

        projectedY = (EvaluateScalarFeatures(values, ProjectedRootYCoefficients, MaximumPolynomialDegree)
            - ProjectedRootYZeroOffset)
            * engineUnitsPerUnityMeter;
        return float.IsFinite(projectedY);
    }

    internal static int CalculateFeatureCount(int muscleCount, int maximumPolynomialDegree)
    {
        int count = muscleCount * 3 + muscleCount * (muscleCount - 1) / 2;
        for (int degree = 3; degree <= maximumPolynomialDegree; degree++)
            count += CombinationWithRepetitionCount(muscleCount, degree);
        return count;
    }

    private static Vector3 EvaluateFeatures(
        ReadOnlySpan<float> values,
        ReadOnlySpan<Vector3> coefficients,
        int maximumPolynomialDegree)
    {
        int cursor = 0;
        Vector3 result = Vector3.Zero;
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * values[i];
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * (values[i] * values[i]);
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * (values[i] * MathF.Abs(values[i]));
        for (int i = 0; i < values.Length; i++)
        {
            float left = values[i];
            for (int j = i + 1; j < values.Length; j++)
                result += coefficients[cursor++] * (left * values[j]);
        }

        for (int degree = 3; degree <= maximumPolynomialDegree; degree++)
            EvaluateMonomials(values, coefficients, degree, 0, 0, 1.0f, ref cursor, ref result);

        return result;
    }

    private static float EvaluateScalarFeatures(
        ReadOnlySpan<float> values,
        ReadOnlySpan<float> coefficients,
        int maximumPolynomialDegree)
    {
        int cursor = 0;
        float result = 0.0f;
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * values[i];
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * (values[i] * values[i]);
        for (int i = 0; i < values.Length; i++)
            result += coefficients[cursor++] * (values[i] * MathF.Abs(values[i]));
        for (int i = 0; i < values.Length; i++)
        {
            float left = values[i];
            for (int j = i + 1; j < values.Length; j++)
                result += coefficients[cursor++] * (left * values[j]);
        }

        for (int degree = 3; degree <= maximumPolynomialDegree; degree++)
            EvaluateScalarMonomials(values, coefficients, degree, 0, 0, 1.0f, ref cursor, ref result);
        return result;
    }

    private static void EvaluateScalarMonomials(
        ReadOnlySpan<float> values,
        ReadOnlySpan<float> coefficients,
        int degree,
        int depth,
        int startIndex,
        float product,
        ref int cursor,
        ref float result)
    {
        if (depth == degree)
        {
            result += coefficients[cursor++] * product;
            return;
        }

        for (int i = startIndex; i < values.Length; i++)
            EvaluateScalarMonomials(values, coefficients, degree, depth + 1, i, product * values[i], ref cursor, ref result);
    }

    private static void EvaluateMonomials(
        ReadOnlySpan<float> values,
        ReadOnlySpan<Vector3> coefficients,
        int degree,
        int depth,
        int startIndex,
        float product,
        ref int cursor,
        ref Vector3 result)
    {
        if (depth == degree)
        {
            result += coefficients[cursor++] * product;
            return;
        }

        for (int i = startIndex; i < values.Length; i++)
            EvaluateMonomials(values, coefficients, degree, depth + 1, i, product * values[i], ref cursor, ref result);
    }

    private static int CombinationWithRepetitionCount(int valueCount, int selectionCount)
    {
        long numerator = 1;
        long denominator = 1;
        for (int i = 1; i <= selectionCount; i++)
        {
            numerator *= valueCount + i - 1;
            denominator *= i;
        }
        return (int)(numerator / denominator);
    }

    private static Quaternion FromRotationVector(Vector3 value)
    {
        float angle = value.Length();
        if (!float.IsFinite(angle) || angle <= 1.0e-7f)
            return Quaternion.Identity;

        Vector3 axis = value / angle;
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }
}
