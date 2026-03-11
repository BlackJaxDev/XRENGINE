using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine;

internal static class AnimationPropertySerialization
{
    public static SerializedPropertyAnimationModel? CreateModel(BasePropAnim? animation)
    {
        if (animation is null)
            return null;

        if (!TryGetKeyframesProperty(animation.GetType(), out PropertyInfo? keyframesProperty))
            return new SerializedPropertyAnimationModel
            {
                TypeName = animation.GetType().AssemblyQualifiedName,
                Payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(animation))
            };

        object? originalKeyframes = keyframesProperty.GetValue(animation);
        object emptyKeyframes = Activator.CreateInstance(keyframesProperty.PropertyType)
            ?? throw new InvalidOperationException($"Failed to create keyframe track for '{animation.GetType().FullName}'.");
        if (emptyKeyframes is BaseKeyframeTrack emptyTrack)
            emptyTrack.LengthInSeconds = animation.LengthInSeconds;

        try
        {
            keyframesProperty.SetValue(animation, emptyKeyframes);
            List<SerializedKeyframeModel> keyframes = [];
            if (originalKeyframes is IEnumerable enumerable)
            {
                foreach (object? keyframe in enumerable)
                {
                    if (keyframe is not Keyframe typedKeyframe)
                        continue;

                    keyframes.Add(new SerializedKeyframeModel
                    {
                        TypeName = typedKeyframe.GetType().AssemblyQualifiedName,
                        Payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(typedKeyframe))
                    });
                }
            }

            return new SerializedPropertyAnimationModel
            {
                TypeName = animation.GetType().AssemblyQualifiedName,
                Payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(animation)),
                Keyframes = keyframes
            };
        }
        finally
        {
            keyframesProperty.SetValue(animation, originalKeyframes);
        }
    }

    public static BasePropAnim? CreateRuntimeAnimation(SerializedPropertyAnimationModel? model)
    {
        if (model?.Payload is null || model.Payload.Length == 0 || string.IsNullOrWhiteSpace(model.TypeName))
            return null;

        Type animationType = ResolveType(model.TypeName)
            ?? throw new InvalidOperationException($"Failed to resolve animation type '{model.TypeName}'.");
        BasePropAnim animation = (BasePropAnim?)CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => CookedBinarySerializer.Deserialize(animationType, model.Payload))
            ?? throw new InvalidOperationException($"Failed to deserialize animation '{animationType.FullName}'.");
        if (!TryGetKeyframesProperty(animation.GetType(), out PropertyInfo? keyframesProperty))
            return animation;

        IList keyframes = keyframesProperty.GetValue(animation) as IList
            ?? Activator.CreateInstance(keyframesProperty.PropertyType) as IList
            ?? throw new InvalidOperationException($"Failed to create runtime keyframe track for '{animation.GetType().FullName}'.");
        if (keyframes is BaseKeyframeTrack keyframeTrack)
            keyframeTrack.LengthInSeconds = animation.LengthInSeconds;

        keyframes.Clear();
        if (model.Keyframes is not null)
        {
            foreach (SerializedKeyframeModel keyframeModel in model.Keyframes)
            {
                if (keyframeModel.Payload is null || keyframeModel.Payload.Length == 0)
                    continue;

                Type keyframeType = ResolveType(keyframeModel.TypeName)
                    ?? throw new InvalidOperationException($"Failed to resolve keyframe type '{keyframeModel.TypeName}'.");
                Keyframe keyframe = (Keyframe?)CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
                    () => CookedBinarySerializer.Deserialize(keyframeType, keyframeModel.Payload))
                    ?? throw new InvalidOperationException($"Failed to deserialize keyframe type '{keyframeType.FullName}'.");
                keyframes.Add(keyframe);
            }
        }

        keyframesProperty.SetValue(animation, keyframes);
        return animation;
    }

    private static bool TryGetKeyframesProperty(Type animationType, [NotNullWhen(true)] out PropertyInfo? keyframesProperty)
    {
        keyframesProperty = animationType.GetProperty("Keyframes", BindingFlags.Instance | BindingFlags.Public);
        return keyframesProperty is not null && typeof(IList).IsAssignableFrom(keyframesProperty.PropertyType);
    }

    private static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        return Type.GetType(typeName, throwOnError: false);
    }
}

internal sealed class SerializedPropertyAnimationModel
{
    public string? TypeName { get; set; }

    public byte[]? Payload { get; set; }

    public List<SerializedKeyframeModel> Keyframes { get; set; } = [];
}

internal sealed class SerializedKeyframeModel
{
    public string? TypeName { get; set; }

    public byte[]? Payload { get; set; }
}