using System;
using MemoryPack;
using XREngine.Animation;
using XREngine.Core.Files;

namespace XREngine;

internal static class MotionSerialization
{
    public static SerializedMotionModel? CreateModel(MotionBase? motion)
    {
        if (motion is null)
            return null;

        return new SerializedMotionModel
        {
            TypeName = motion.GetType().AssemblyQualifiedName,
            Payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(motion))
        };
    }

    public static MotionBase? CreateRuntimeMotion(SerializedMotionModel? model)
    {
        if (model?.Payload is null || model.Payload.Length == 0 || string.IsNullOrWhiteSpace(model.TypeName))
            return null;

        Type motionType = Type.GetType(model.TypeName, throwOnError: false)
            ?? throw new InvalidOperationException($"Failed to resolve motion type '{model.TypeName}'.");
        return CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => CookedBinarySerializer.Deserialize(motionType, model.Payload) as MotionBase);
    }
}

[MemoryPackable]
internal sealed partial class SerializedMotionModel
{
    public string? TypeName { get; set; }

    public byte[]? Payload { get; set; }
}