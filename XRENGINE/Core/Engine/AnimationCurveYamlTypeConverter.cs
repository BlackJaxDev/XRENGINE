using System;
using System.Collections.Generic;
using System.Reflection;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Serializes animation curves as an explicit mapping instead of relying on their
/// <see cref="IEnumerable{FloatKeyframe}"/> implementation, which YamlDotNet otherwise treats as
/// an unconstructable collection during deserialization.
/// </summary>
[YamlTypeConverter]
public sealed class AnimationCurveYamlTypeConverter : IYamlTypeConverter
{
    private static readonly PropertyInfo? AssetIdProperty = typeof(XRObjectBase).GetProperty(
        nameof(XRObjectBase.ID),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public bool Accepts(Type type)
        => type == typeof(AnimationCurve);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (XRAssetDeserializer.IsNullScalar(scalar))
                return null;

            throw new YamlException(
                scalar.Start,
                scalar.End,
                $"Unexpected scalar while deserializing {nameof(AnimationCurve)}: '{scalar.Value}'.");
        }

        // Assets written before the explicit curve model encoded AnimationCurve
        // directly as a sequence because it implements IEnumerable<FloatKeyframe>.
        if (parser.Accept<SequenceStart>(out _))
        {
            List<FloatKeyframe> legacyKeyframes =
                rootDeserializer(typeof(List<FloatKeyframe>)) as List<FloatKeyframe> ?? [];
            return CreateCurve(null, legacyKeyframes);
        }

        AnimationCurveYamlModel? model =
            rootDeserializer(typeof(AnimationCurveYamlModel)) as AnimationCurveYamlModel;
        return CreateCurve(model, model?.Keyframes ?? []);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not AnimationCurve curve)
            throw new YamlException($"Expected {nameof(AnimationCurve)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, curve))
            return;

        List<FloatKeyframe> keyframes = new(curve.Keyframes.Count);
        foreach (FloatKeyframe keyframe in (IEnumerable<FloatKeyframe>)curve.Keyframes)
            keyframes.Add(keyframe);

        serializer(
            new AnimationCurveYamlModel
            {
                ID = curve.ID,
                Name = curve.Name,
                OriginalPath = curve.OriginalPath,
                OriginalLastWriteTimeUtc = curve.OriginalLastWriteTimeUtc,
                LengthInSeconds = curve.LengthInSeconds,
                Speed = curve.Speed,
                Looped = curve.Looped,
                Keyframes = keyframes
            },
            typeof(AnimationCurveYamlModel));
    }

    private static AnimationCurve CreateCurve(
        AnimationCurveYamlModel? model,
        IReadOnlyCollection<FloatKeyframe> keyframes)
    {
        float keyframeLength = 0.0f;
        foreach (FloatKeyframe keyframe in keyframes)
            keyframeLength = MathF.Max(keyframeLength, keyframe.Second);

        var curve = new AnimationCurve
        {
            Name = model?.Name,
            OriginalPath = model?.OriginalPath,
            OriginalLastWriteTimeUtc = model?.OriginalLastWriteTimeUtc,
            Speed = model?.Speed ?? 1.0f,
            Looped = model?.Looped ?? false,
            LengthInSeconds = MathF.Max(model?.LengthInSeconds ?? 0.0f, keyframeLength)
        };

        if (model is { ID: var id } && id != Guid.Empty)
            AssetIdProperty?.SetValue(curve, id);

        curve.Keyframes.Add(keyframes);
        return curve;
    }
}
