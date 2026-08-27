namespace XREngine.Animation;

/// <summary>
/// Preallocated parallel data path for Unity humanoid Body/root leaf samples.
/// Capacity is established when the graph is initialized; evaluation never grows it.
/// </summary>
internal sealed class UnityHumanoidMotionContributionBuffer
{
    private UnityHumanoidMotionContribution[] _items = [];

    public int Count { get; private set; }
    public int Capacity => _items.Length;
    public bool Overflowed { get; private set; }
    public ReadOnlySpan<UnityHumanoidMotionContribution> Items => _items.AsSpan(0, Count);

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
            return;

        Array.Resize(ref _items, capacity);
    }

    public void Clear()
    {
        Count = 0;
        Overflowed = false;
    }

    public bool TryAdd(in UnityHumanoidMotionContribution contribution)
    {
        if (Count >= _items.Length)
        {
            Overflowed = true;
            return false;
        }

        _items[Count++] = contribution;
        return true;
    }

    public void CopyFrom(UnityHumanoidMotionContributionBuffer source)
    {
        Clear();
        AppendScaled(source, 1.0f);
    }

    public void BlendFrom(
        UnityHumanoidMotionContributionBuffer? first,
        float firstWeight,
        UnityHumanoidMotionContributionBuffer? second,
        float secondWeight)
    {
        Clear();
        AppendScaled(first, firstWeight);
        AppendScaled(second, secondWeight);
    }

    public void AppendScaled(
        UnityHumanoidMotionContributionBuffer? source,
        float scale,
        EUnityHumanoidMotionContributionType? contributionType = null)
    {
        if (source is null || source.Count == 0 || !float.IsFinite(scale) || scale <= 0.0f)
        {
            if (source?.Overflowed == true && float.IsFinite(scale) && scale > 0.0f)
                Overflowed = true;
            return;
        }

        if (source.Overflowed)
            Overflowed = true;

        ReadOnlySpan<UnityHumanoidMotionContribution> sourceItems = source.Items;
        for (int i = 0; i < sourceItems.Length; i++)
        {
            UnityHumanoidMotionContribution item = sourceItems[i];
            float weight = item.Weight * scale;
            if (!float.IsFinite(weight) || weight <= 0.0f)
                continue;

            if (Count >= _items.Length)
            {
                Overflowed = true;
                return;
            }

            _items[Count++] = item.WithWeightAndType(
                weight,
                contributionType ?? item.ContributionType);
        }
    }

    public void AttenuateOverride(float scale)
    {
        scale = float.IsFinite(scale) ? Math.Clamp(scale, 0.0f, 1.0f) : 0.0f;
        for (int i = 0; i < Count; i++)
        {
            UnityHumanoidMotionContribution item = _items[i];
            if (item.ContributionType == EUnityHumanoidMotionContributionType.Override)
                _items[i] = item.WithWeightAndType(item.Weight * scale, item.ContributionType);
        }
    }
}
