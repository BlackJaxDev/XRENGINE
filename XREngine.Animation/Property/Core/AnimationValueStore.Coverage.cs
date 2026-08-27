using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Animation;

public sealed partial class AnimationValueStore
{
    private float[] _floatCoverage = [];
    private float[] _vector2Coverage = [];
    private float[] _vector3Coverage = [];
    private float[] _vector4Coverage = [];
    private float[] _quaternionCoverage = [];
    private float[] _boolCoverage = [];
    private float[] _discreteCoverage = [];
    private bool[] _quaternionFloatMask = [];

    /// <summary>
    /// Returns how strongly this store authors a slot. Zero means the slot is
    /// absent and one means it is fully authored. Intermediate values retain a
    /// sparse blend's partial influence; values above one preserve explicitly
    /// non-normalized direct-tree influence until final layer composition.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetCoverage(in AnimSlot slot)
        => slot.Type switch
        {
            EAnimValueType.Float => _floatCoverage[slot.TypeIndex],
            EAnimValueType.Vector2 => _vector2Coverage[slot.TypeIndex],
            EAnimValueType.Vector3 => _vector3Coverage[slot.TypeIndex],
            EAnimValueType.Vector4 => _vector4Coverage[slot.TypeIndex],
            EAnimValueType.Quaternion => _quaternionCoverage[slot.TypeIndex],
            EAnimValueType.Bool => _boolCoverage[slot.TypeIndex],
            EAnimValueType.Discrete => _discreteCoverage[slot.TypeIndex],
            _ => 0.0f,
        };

    private void ResizeCoverage(AnimationSlotLayout layout)
    {
        _floatCoverage = layout.FloatCount > 0 ? new float[layout.FloatCount] : [];
        _vector2Coverage = layout.Vector2Count > 0 ? new float[layout.Vector2Count] : [];
        _vector3Coverage = layout.Vector3Count > 0 ? new float[layout.Vector3Count] : [];
        _vector4Coverage = layout.Vector4Count > 0 ? new float[layout.Vector4Count] : [];
        _quaternionCoverage = layout.QuaternionCount > 0 ? new float[layout.QuaternionCount] : [];
        _boolCoverage = layout.BoolCount > 0 ? new float[layout.BoolCount] : [];
        _discreteCoverage = layout.DiscreteCount > 0 ? new float[layout.DiscreteCount] : [];
        _quaternionFloatMask = layout.FloatCount > 0 ? new bool[layout.FloatCount] : [];

        for (int i = 0; i < _quaternionFloatGroups.Length; i++)
        {
            AnimationQuaternionFloatSlotGroup group = _quaternionFloatGroups[i];
            _quaternionFloatMask[group.XIndex] = true;
            _quaternionFloatMask[group.YIndex] = true;
            _quaternionFloatMask[group.ZIndex] = true;
            _quaternionFloatMask[group.WIndex] = true;
        }
    }

    private void CopyCoverageFrom(AnimationValueStore source)
    {
        source._floatCoverage.AsSpan().CopyTo(_floatCoverage);
        source._vector2Coverage.AsSpan().CopyTo(_vector2Coverage);
        source._vector3Coverage.AsSpan().CopyTo(_vector3Coverage);
        source._vector4Coverage.AsSpan().CopyTo(_vector4Coverage);
        source._quaternionCoverage.AsSpan().CopyTo(_quaternionCoverage);
        source._boolCoverage.AsSpan().CopyTo(_boolCoverage);
        source._discreteCoverage.AsSpan().CopyTo(_discreteCoverage);
    }

    private void ClearCoverage()
    {
        _floatCoverage.AsSpan().Clear();
        _vector2Coverage.AsSpan().Clear();
        _vector3Coverage.AsSpan().Clear();
        _vector4Coverage.AsSpan().Clear();
        _quaternionCoverage.AsSpan().Clear();
        _boolCoverage.AsSpan().Clear();
        _discreteCoverage.AsSpan().Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetQuaternionFloatGroupCoverage(AnimationQuaternionFloatSlotGroup group)
        => MathF.Min(
            MathF.Min(_floatCoverage[group.XIndex], _floatCoverage[group.YIndex]),
            MathF.Min(_floatCoverage[group.ZIndex], _floatCoverage[group.WIndex]));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetQuaternionFloatGroupCoverage(AnimationQuaternionFloatSlotGroup group, float coverage)
    {
        _floatCoverage[group.XIndex] = coverage;
        _floatCoverage[group.YIndex] = coverage;
        _floatCoverage[group.ZIndex] = coverage;
        _floatCoverage[group.WIndex] = coverage;
    }

    private static void BlendTwoPresenceAware(
        AnimationValueStore a,
        AnimationValueStore b,
        float t,
        AnimationValueStore result)
    {
        float clampedT = float.IsFinite(t) ? Math.Clamp(t, 0.0f, 1.0f) : 0.0f;
        BlendFixedPresenceAware(a, 1.0f - clampedT, b, clampedT, null, 0.0f, null, 0.0f, 2, result);
    }

    private static void BlendThreePresenceAware(
        AnimationValueStore a,
        AnimationValueStore b,
        AnimationValueStore c,
        float w1,
        float w2,
        float w3,
        AnimationValueStore result)
        => BlendFixedPresenceAware(a, w1, b, w2, c, w3, null, 0.0f, 3, result);

    private static void BlendFourPresenceAware(
        AnimationValueStore a,
        AnimationValueStore b,
        AnimationValueStore c,
        AnimationValueStore d,
        float w1,
        float w2,
        float w3,
        float w4,
        AnimationValueStore result)
        => BlendFixedPresenceAware(a, w1, b, w2, c, w3, d, w4, 4, result);

    private static void BlendFixedPresenceAware(
        AnimationValueStore? a,
        float w1,
        AnimationValueStore? b,
        float w2,
        AnimationValueStore? c,
        float w3,
        AnimationValueStore? d,
        float w4,
        int count,
        AnimationValueStore result)
    {
        result.Clear();
        w1 = SanitizeWeight(a, w1);
        w2 = SanitizeWeight(b, w2);
        w3 = count > 2 ? SanitizeWeight(c, w3) : 0.0f;
        w4 = count > 3 ? SanitizeWeight(d, w4) : 0.0f;

        for (int i = 0; i < result._floats.Length; i++)
        {
            float aw = w1 * (a?._floatCoverage[i] ?? 0.0f);
            float bw = w2 * (b?._floatCoverage[i] ?? 0.0f);
            float cw = w3 * (c?._floatCoverage[i] ?? 0.0f);
            float dw = w4 * (d?._floatCoverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            if (total > float.Epsilon)
                result._floats[i] = ((a?._floats[i] ?? 0.0f) * aw
                    + (b?._floats[i] ?? 0.0f) * bw
                    + (c?._floats[i] ?? 0.0f) * cw
                    + (d?._floats[i] ?? 0.0f) * dw) / total;
            result._floatCoverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors2.Length; i++)
        {
            float aw = w1 * (a?._vector2Coverage[i] ?? 0.0f);
            float bw = w2 * (b?._vector2Coverage[i] ?? 0.0f);
            float cw = w3 * (c?._vector2Coverage[i] ?? 0.0f);
            float dw = w4 * (d?._vector2Coverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            if (total > float.Epsilon)
                result._vectors2[i] = ((a?._vectors2[i] ?? Vector2.Zero) * aw
                    + (b?._vectors2[i] ?? Vector2.Zero) * bw
                    + (c?._vectors2[i] ?? Vector2.Zero) * cw
                    + (d?._vectors2[i] ?? Vector2.Zero) * dw) / total;
            result._vector2Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors3.Length; i++)
        {
            float aw = w1 * (a?._vector3Coverage[i] ?? 0.0f);
            float bw = w2 * (b?._vector3Coverage[i] ?? 0.0f);
            float cw = w3 * (c?._vector3Coverage[i] ?? 0.0f);
            float dw = w4 * (d?._vector3Coverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            if (total > float.Epsilon)
                result._vectors3[i] = ((a?._vectors3[i] ?? Vector3.Zero) * aw
                    + (b?._vectors3[i] ?? Vector3.Zero) * bw
                    + (c?._vectors3[i] ?? Vector3.Zero) * cw
                    + (d?._vectors3[i] ?? Vector3.Zero) * dw) / total;
            result._vector3Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors4.Length; i++)
        {
            float aw = w1 * (a?._vector4Coverage[i] ?? 0.0f);
            float bw = w2 * (b?._vector4Coverage[i] ?? 0.0f);
            float cw = w3 * (c?._vector4Coverage[i] ?? 0.0f);
            float dw = w4 * (d?._vector4Coverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            if (total > float.Epsilon)
                result._vectors4[i] = ((a?._vectors4[i] ?? Vector4.Zero) * aw
                    + (b?._vectors4[i] ?? Vector4.Zero) * bw
                    + (c?._vectors4[i] ?? Vector4.Zero) * cw
                    + (d?._vectors4[i] ?? Vector4.Zero) * dw) / total;
            result._vector4Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._quaternions.Length; i++)
        {
            float aw = w1 * (a?._quaternionCoverage[i] ?? 0.0f);
            float bw = w2 * (b?._quaternionCoverage[i] ?? 0.0f);
            float cw = w3 * (c?._quaternionCoverage[i] ?? 0.0f);
            float dw = w4 * (d?._quaternionCoverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            result._quaternions[i] = BlendQuaternionFixed(
                a?._quaternions[i] ?? Quaternion.Identity, aw,
                b?._quaternions[i] ?? Quaternion.Identity, bw,
                c?._quaternions[i] ?? Quaternion.Identity, cw,
                d?._quaternions[i] ?? Quaternion.Identity, dw);
            result._quaternionCoverage[i] = PreserveCoverage(total);
        }

        for (int groupIndex = 0; groupIndex < result._quaternionFloatGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = result._quaternionFloatGroups[groupIndex];
            float aw = w1 * (a?.GetQuaternionFloatGroupCoverage(group) ?? 0.0f);
            float bw = w2 * (b?.GetQuaternionFloatGroupCoverage(group) ?? 0.0f);
            float cw = w3 * (c?.GetQuaternionFloatGroupCoverage(group) ?? 0.0f);
            float dw = w4 * (d?.GetQuaternionFloatGroupCoverage(group) ?? 0.0f);
            float total = aw + bw + cw + dw;
            Quaternion value = BlendQuaternionFixed(
                a is null ? Quaternion.Identity : ReadNormalizedQuaternion(a._floats, group), aw,
                b is null ? Quaternion.Identity : ReadNormalizedQuaternion(b._floats, group), bw,
                c is null ? Quaternion.Identity : ReadNormalizedQuaternion(c._floats, group), cw,
                d is null ? Quaternion.Identity : ReadNormalizedQuaternion(d._floats, group), dw);
            WriteQuaternion(result._floats, group, value);
            result.SetQuaternionFloatGroupCoverage(group, PreserveCoverage(total));
        }

        for (int i = 0; i < result._bools.Length; i++)
        {
            float aw = w1 * (a?._boolCoverage[i] ?? 0.0f);
            float bw = w2 * (b?._boolCoverage[i] ?? 0.0f);
            float cw = w3 * (c?._boolCoverage[i] ?? 0.0f);
            float dw = w4 * (d?._boolCoverage[i] ?? 0.0f);
            float total = aw + bw + cw + dw;
            float best = MathF.Max(MathF.Max(aw, bw), MathF.Max(cw, dw));
            result._bools[i] = best > 0.0f && ((aw == best && a!._bools[i])
                || (bw == best && b!._bools[i])
                || (cw == best && c!._bools[i])
                || (dw == best && d!._bools[i]));
            result._boolCoverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._discrete.Length; i++)
        {
            float aw = w1 * (a?._discreteCoverage[i] ?? 0.0f);
            float bw = w2 * (b?._discreteCoverage[i] ?? 0.0f);
            float cw = w3 * (c?._discreteCoverage[i] ?? 0.0f);
            float dw = w4 * (d?._discreteCoverage[i] ?? 0.0f);
            float best = MathF.Max(MathF.Max(aw, bw), MathF.Max(cw, dw));
            result._discrete[i] = best <= 0.0f ? null
                : aw == best ? a!._discrete[i]
                : bw == best ? b!._discrete[i]
                : cw == best ? c!._discrete[i]
                : d!._discrete[i];
            result._discreteCoverage[i] = PreserveCoverage(aw + bw + cw + dw);
        }
    }

    private static void BlendManyPresenceAware(
        AnimationValueStore?[] sources,
        float[] weights,
        int count,
        bool normalizeWeights,
        AnimationValueStore result)
    {
        int sourceCount = Math.Min(count, Math.Min(sources.Length, weights.Length));
        float totalGraphWeight = 0.0f;
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            totalGraphWeight += SanitizeWeight(sources[sourceIndex], weights[sourceIndex]);

        result.Clear();
        if (totalGraphWeight <= float.Epsilon)
            return;

        float graphNormalizer = normalizeWeights ? 1.0f / totalGraphWeight : 1.0f;
        for (int i = 0; i < result._floats.Length; i++)
        {
            float total = 0.0f;
            float value = 0.0f;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._floatCoverage[i];
                total += effective;
                value += source._floats[i] * effective;
            }
            if (total > float.Epsilon)
                result._floats[i] = value / total;
            result._floatCoverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors2.Length; i++)
        {
            float total = 0.0f;
            Vector2 value = Vector2.Zero;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._vector2Coverage[i];
                total += effective;
                value += source._vectors2[i] * effective;
            }
            if (total > float.Epsilon)
                result._vectors2[i] = value / total;
            result._vector2Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors3.Length; i++)
        {
            float total = 0.0f;
            Vector3 value = Vector3.Zero;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._vector3Coverage[i];
                total += effective;
                value += source._vectors3[i] * effective;
            }
            if (total > float.Epsilon)
                result._vectors3[i] = value / total;
            result._vector3Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._vectors4.Length; i++)
        {
            float total = 0.0f;
            Vector4 value = Vector4.Zero;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._vector4Coverage[i];
                total += effective;
                value += source._vectors4[i] * effective;
            }
            if (total > float.Epsilon)
                result._vectors4[i] = value / total;
            result._vector4Coverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._quaternions.Length; i++)
        {
            float total = 0.0f;
            Quaternion reference = Quaternion.Identity;
            float referenceWeight = float.NegativeInfinity;
            bool hasReference = false;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._quaternionCoverage[i];
                if (effective <= 0.0f)
                    continue;
                ConsiderQuaternionReference(
                    ref reference,
                    ref referenceWeight,
                    ref hasReference,
                    source._quaternions[i],
                    effective);
                total += effective;
            }

            Vector4 value = Vector4.Zero;
            if (hasReference)
            {
                for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
                {
                    AnimationValueStore? source = sources[sourceIndex];
                    if (source is null)
                        continue;
                    float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._quaternionCoverage[i];
                    AccumulateQuaternion(ref value, reference, source._quaternions[i], effective);
                }
            }
            result._quaternions[i] = NormalizeQuaternionVector(value);
            result._quaternionCoverage[i] = PreserveCoverage(total);
        }

        for (int groupIndex = 0; groupIndex < result._quaternionFloatGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = result._quaternionFloatGroups[groupIndex];
            float total = 0.0f;
            Quaternion reference = Quaternion.Identity;
            float referenceWeight = float.NegativeInfinity;
            bool hasReference = false;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source.GetQuaternionFloatGroupCoverage(group);
                if (effective <= 0.0f)
                    continue;
                ConsiderQuaternionReference(
                    ref reference,
                    ref referenceWeight,
                    ref hasReference,
                    ReadNormalizedQuaternion(source._floats, group),
                    effective);
                total += effective;
            }

            Vector4 value = Vector4.Zero;
            if (hasReference)
            {
                for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
                {
                    AnimationValueStore? source = sources[sourceIndex];
                    if (source is null)
                        continue;
                    float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source.GetQuaternionFloatGroupCoverage(group);
                    AccumulateQuaternion(
                        ref value,
                        reference,
                        ReadNormalizedQuaternion(source._floats, group),
                        effective);
                }
            }
            WriteQuaternion(result._floats, group, NormalizeQuaternionVector(value));
            result.SetQuaternionFloatGroupCoverage(group, PreserveCoverage(total));
        }

        for (int i = 0; i < result._bools.Length; i++)
        {
            float total = 0.0f;
            float best = 0.0f;
            bool value = false;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._boolCoverage[i];
                total += effective;
                if (effective > best || (effective == best && source._bools[i]))
                {
                    best = effective;
                    value = source._bools[i];
                }
            }
            result._bools[i] = value;
            result._boolCoverage[i] = PreserveCoverage(total);
        }

        for (int i = 0; i < result._discrete.Length; i++)
        {
            float total = 0.0f;
            float best = 0.0f;
            object? value = null;
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                AnimationValueStore? source = sources[sourceIndex];
                if (source is null)
                    continue;
                float effective = SanitizeWeight(source, weights[sourceIndex]) * graphNormalizer * source._discreteCoverage[i];
                total += effective;
                if (effective <= best)
                    continue;
                best = effective;
                value = source._discrete[i];
            }
            result._discrete[i] = value;
            result._discreteCoverage[i] = PreserveCoverage(total);
        }
    }

    /// <summary>
    /// Composes an override layer using Unity-style per-property weighting.
    /// A missing source slot leaves the destination unchanged.
    /// </summary>
    public void OverrideFrom(AnimationValueStore source, float weight)
    {
        float layerWeight = SanitizeUnitWeight(weight);
        if (layerWeight <= 0.0f)
            return;

        for (int i = 0; i < _floats.Length; i++)
        {
            if (_quaternionFloatMask[i])
                continue;
            BlendOverride(ref _floats[i], ref _floatCoverage[i], source._floats[i], source._floatCoverage[i] * layerWeight);
        }
        for (int i = 0; i < _vectors2.Length; i++)
            BlendOverride(ref _vectors2[i], ref _vector2Coverage[i], source._vectors2[i], source._vector2Coverage[i] * layerWeight);
        for (int i = 0; i < _vectors3.Length; i++)
            BlendOverride(ref _vectors3[i], ref _vector3Coverage[i], source._vectors3[i], source._vector3Coverage[i] * layerWeight);
        for (int i = 0; i < _vectors4.Length; i++)
            BlendOverride(ref _vectors4[i], ref _vector4Coverage[i], source._vectors4[i], source._vector4Coverage[i] * layerWeight);
        for (int i = 0; i < _quaternions.Length; i++)
            BlendOverrideQuaternion(ref _quaternions[i], ref _quaternionCoverage[i], source._quaternions[i], source._quaternionCoverage[i] * layerWeight);

        for (int groupIndex = 0; groupIndex < _quaternionFloatGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = _quaternionFloatGroups[groupIndex];
            Quaternion destination = ReadNormalizedQuaternion(_floats, group);
            float destinationCoverage = GetQuaternionFloatGroupCoverage(group);
            BlendOverrideQuaternion(
                ref destination,
                ref destinationCoverage,
                ReadNormalizedQuaternion(source._floats, group),
                source.GetQuaternionFloatGroupCoverage(group) * layerWeight);
            WriteQuaternion(_floats, group, destination);
            SetQuaternionFloatGroupCoverage(group, destinationCoverage);
        }

        for (int i = 0; i < _bools.Length; i++)
            BlendOverrideDiscrete(ref _bools[i], ref _boolCoverage[i], source._bools[i], source._boolCoverage[i] * layerWeight);
        for (int i = 0; i < _discrete.Length; i++)
            BlendOverrideDiscrete(ref _discrete[i], ref _discreteCoverage[i], source._discrete[i], source._discreteCoverage[i] * layerWeight);
    }

    /// <summary>
    /// Adds a reference-relative pose using per-property layer weight. Quaternion
    /// deltas are applied from identity and discrete slots are intentionally ignored.
    /// </summary>
    public void AddFrom(AnimationValueStore source, float weight)
    {
        float layerWeight = SanitizeUnitWeight(weight);
        if (layerWeight <= 0.0f)
            return;

        for (int i = 0; i < _floats.Length; i++)
        {
            if (_quaternionFloatMask[i])
                continue;
            float alpha = source._floatCoverage[i] * layerWeight;
            _floats[i] += source._floats[i] * alpha;
            _floatCoverage[i] = MathF.Max(_floatCoverage[i], Saturate(alpha));
        }
        for (int i = 0; i < _vectors2.Length; i++)
        {
            float alpha = source._vector2Coverage[i] * layerWeight;
            _vectors2[i] += source._vectors2[i] * alpha;
            _vector2Coverage[i] = MathF.Max(_vector2Coverage[i], Saturate(alpha));
        }
        for (int i = 0; i < _vectors3.Length; i++)
        {
            float alpha = source._vector3Coverage[i] * layerWeight;
            _vectors3[i] += source._vectors3[i] * alpha;
            _vector3Coverage[i] = MathF.Max(_vector3Coverage[i], Saturate(alpha));
        }
        for (int i = 0; i < _vectors4.Length; i++)
        {
            float alpha = source._vector4Coverage[i] * layerWeight;
            _vectors4[i] += source._vectors4[i] * alpha;
            _vector4Coverage[i] = MathF.Max(_vector4Coverage[i], Saturate(alpha));
        }
        for (int i = 0; i < _quaternions.Length; i++)
        {
            float alpha = source._quaternionCoverage[i] * layerWeight;
            Quaternion delta = Quaternion.Slerp(Quaternion.Identity, NormalizeOrIdentity(source._quaternions[i]), alpha);
            _quaternions[i] = NormalizeOrIdentity(_quaternions[i] * delta);
            _quaternionCoverage[i] = MathF.Max(_quaternionCoverage[i], Saturate(alpha));
        }
        for (int groupIndex = 0; groupIndex < _quaternionFloatGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = _quaternionFloatGroups[groupIndex];
            float alpha = source.GetQuaternionFloatGroupCoverage(group) * layerWeight;
            Quaternion destination = ReadNormalizedQuaternion(_floats, group);
            Quaternion delta = Quaternion.Slerp(
                Quaternion.Identity,
                ReadNormalizedQuaternion(source._floats, group),
                alpha);
            WriteQuaternion(_floats, group, NormalizeOrIdentity(destination * delta));
            SetQuaternionFloatGroupCoverage(
                group,
                MathF.Max(GetQuaternionFloatGroupCoverage(group), Saturate(alpha)));
        }
    }

    /// <summary>
    /// Converts this absolute pose into a delta from <paramref name="reference"/>.
    /// Only slots authored by both poses participate.
    /// </summary>
    public void MakeAdditiveRelativeTo(AnimationValueStore reference)
    {
        for (int i = 0; i < _floats.Length; i++)
        {
            if (_quaternionFloatMask[i])
                continue;
            _floats[i] -= reference._floats[i];
            _floatCoverage[i] = MathF.Min(_floatCoverage[i], reference._floatCoverage[i]);
        }
        for (int i = 0; i < _vectors2.Length; i++)
        {
            _vectors2[i] -= reference._vectors2[i];
            _vector2Coverage[i] = MathF.Min(_vector2Coverage[i], reference._vector2Coverage[i]);
        }
        for (int i = 0; i < _vectors3.Length; i++)
        {
            _vectors3[i] -= reference._vectors3[i];
            _vector3Coverage[i] = MathF.Min(_vector3Coverage[i], reference._vector3Coverage[i]);
        }
        for (int i = 0; i < _vectors4.Length; i++)
        {
            _vectors4[i] -= reference._vectors4[i];
            _vector4Coverage[i] = MathF.Min(_vector4Coverage[i], reference._vector4Coverage[i]);
        }
        for (int i = 0; i < _quaternions.Length; i++)
        {
            Quaternion referenceValue = NormalizeOrIdentity(reference._quaternions[i]);
            _quaternions[i] = NormalizeOrIdentity(Quaternion.Inverse(referenceValue) * NormalizeOrIdentity(_quaternions[i]));
            _quaternionCoverage[i] = MathF.Min(_quaternionCoverage[i], reference._quaternionCoverage[i]);
        }
        for (int groupIndex = 0; groupIndex < _quaternionFloatGroups.Length; groupIndex++)
        {
            AnimationQuaternionFloatSlotGroup group = _quaternionFloatGroups[groupIndex];
            Quaternion referenceValue = ReadNormalizedQuaternion(reference._floats, group);
            Quaternion value = ReadNormalizedQuaternion(_floats, group);
            WriteQuaternion(_floats, group, NormalizeOrIdentity(Quaternion.Inverse(referenceValue) * value));
            SetQuaternionFloatGroupCoverage(
                group,
                MathF.Min(GetQuaternionFloatGroupCoverage(group), reference.GetQuaternionFloatGroupCoverage(group)));
        }

        _boolCoverage.AsSpan().Clear();
        _discreteCoverage.AsSpan().Clear();
    }

    private static void BlendOverride(ref float destination, ref float destinationCoverage, float source, float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        float total = destinationWeight + sourceAlpha;
        if (total > float.Epsilon)
            destination = (destination * destinationWeight + source * sourceAlpha) / total;
        destinationCoverage = Saturate(total);
    }

    private static void BlendOverride(ref Vector2 destination, ref float destinationCoverage, Vector2 source, float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        float total = destinationWeight + sourceAlpha;
        if (total > float.Epsilon)
            destination = (destination * destinationWeight + source * sourceAlpha) / total;
        destinationCoverage = Saturate(total);
    }

    private static void BlendOverride(ref Vector3 destination, ref float destinationCoverage, Vector3 source, float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        float total = destinationWeight + sourceAlpha;
        if (total > float.Epsilon)
            destination = (destination * destinationWeight + source * sourceAlpha) / total;
        destinationCoverage = Saturate(total);
    }

    private static void BlendOverride(ref Vector4 destination, ref float destinationCoverage, Vector4 source, float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        float total = destinationWeight + sourceAlpha;
        if (total > float.Epsilon)
            destination = (destination * destinationWeight + source * sourceAlpha) / total;
        destinationCoverage = Saturate(total);
    }

    private static void BlendOverrideQuaternion(
        ref Quaternion destination,
        ref float destinationCoverage,
        Quaternion source,
        float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        float total = destinationWeight + sourceAlpha;
        if (total > float.Epsilon)
            destination = Quaternion.Slerp(
                NormalizeOrIdentity(destination),
                NormalizeOrIdentity(source),
                sourceAlpha / total);
        destinationCoverage = Saturate(total);
    }

    private static void BlendOverrideDiscrete<T>(
        ref T destination,
        ref float destinationCoverage,
        T source,
        float sourceAlpha)
    {
        float destinationWeight = destinationCoverage * (1.0f - sourceAlpha);
        if (sourceAlpha >= destinationWeight && sourceAlpha > 0.0f)
            destination = source;
        destinationCoverage = Saturate(destinationWeight + sourceAlpha);
    }

    private static Quaternion BlendQuaternionFixed(
        Quaternion a,
        float aw,
        Quaternion b,
        float bw,
        Quaternion c,
        float cw,
        Quaternion d,
        float dw)
    {
        Quaternion reference = Quaternion.Identity;
        float referenceWeight = float.NegativeInfinity;
        bool hasReference = false;
        ConsiderQuaternionReference(ref reference, ref referenceWeight, ref hasReference, a, aw);
        ConsiderQuaternionReference(ref reference, ref referenceWeight, ref hasReference, b, bw);
        ConsiderQuaternionReference(ref reference, ref referenceWeight, ref hasReference, c, cw);
        ConsiderQuaternionReference(ref reference, ref referenceWeight, ref hasReference, d, dw);
        if (!hasReference)
            return Quaternion.Identity;

        Vector4 value = Vector4.Zero;
        AccumulateQuaternion(ref value, reference, a, aw);
        AccumulateQuaternion(ref value, reference, b, bw);
        AccumulateQuaternion(ref value, reference, c, cw);
        AccumulateQuaternion(ref value, reference, d, dw);
        return NormalizeQuaternionVector(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConsiderQuaternionReference(
        ref Quaternion reference,
        ref float referenceWeight,
        ref bool hasReference,
        Quaternion value,
        float weight)
    {
        if (weight <= 0.0f)
            return;

        Quaternion candidate = CanonicalizeQuaternion(NormalizeOrIdentity(value));
        if (!hasReference
            || weight > referenceWeight
            || (weight == referenceWeight && IsPreferredQuaternionReference(candidate, reference)))
        {
            reference = candidate;
            referenceWeight = weight;
            hasReference = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AccumulateQuaternion(
        ref Vector4 sum,
        Quaternion reference,
        Quaternion value,
        float weight)
    {
        if (weight <= 0.0f)
            return;

        Quaternion normalized = NormalizeOrIdentity(value);
        if (Quaternion.Dot(reference, normalized) < 0.0f)
            normalized = new Quaternion(-normalized.X, -normalized.Y, -normalized.Z, -normalized.W);
        sum += ToVector4(normalized) * weight;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPreferredQuaternionReference(Quaternion candidate, Quaternion current)
    {
        if (candidate.W != current.W)
            return candidate.W > current.W;
        if (candidate.Z != current.Z)
            return candidate.Z > current.Z;
        if (candidate.Y != current.Y)
            return candidate.Y > current.Y;
        return candidate.X > current.X;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Saturate(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float PreserveCoverage(float value)
        => float.IsFinite(value) ? MathF.Max(0.0f, value) : 0.0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SanitizeUnitWeight(float weight)
        => float.IsFinite(weight) ? Math.Clamp(weight, 0.0f, 1.0f) : 0.0f;
}
