namespace XREngine;

public readonly record struct RvcLightReservoir(
    uint SelectedLightId,
    float SelectedWeight,
    float WeightSum,
    uint CandidateCount)
{
    public static RvcLightReservoir Empty => default;

    public RvcLightReservoir Add(uint lightId, float weight, float random01)
    {
        float clampedWeight = MathF.Max(0.0f, weight);
        float newSum = WeightSum + clampedWeight;
        uint newCount = CandidateCount + 1u;
        bool select = newSum > 0.0f && random01 <= clampedWeight / newSum;
        return new(
            select ? lightId : SelectedLightId,
            select ? clampedWeight : SelectedWeight,
            newSum,
            newCount);
    }

    public static RvcLightReservoir Combine(
        in RvcLightReservoir a,
        in RvcLightReservoir b,
        float random01)
    {
        if (a.CandidateCount == 0u)
            return b;
        if (b.CandidateCount == 0u)
            return a;

        float combinedWeight = a.WeightSum + b.WeightSum;
        bool selectB = combinedWeight > 0.0f && random01 <= b.WeightSum / combinedWeight;
        return new(
            selectB ? b.SelectedLightId : a.SelectedLightId,
            selectB ? b.SelectedWeight : a.SelectedWeight,
            combinedWeight,
            a.CandidateCount + b.CandidateCount);
    }
}
