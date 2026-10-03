using System.Numerics;
using MemoryPack;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;

namespace XREngine;

/// <summary>Encodes the finite set of values supported by published animation payloads.</summary>
internal static class PublishedAnimationValueCodec
{
    private const byte Version = 1;
    private const int HeaderLength = 6;

    private enum ValueKind : byte
    {
        Null = 0,
        Boolean = 1,
        Byte = 2,
        SByte = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        Single = 10,
        Double = 11,
        Decimal = 12,
        Char = 13,
        String = 14,
        Vector2 = 15,
        Vector3 = 16,
        Vector4 = 17,
        Quaternion = 18,
        Matrix4x4 = 19,
        StringComparison = 20,
        LimbEndEffector = 21,
        SourceAssetReference = 22
    }

    public static byte[] Encode(object? value, string context)
        => value switch
        {
            null => Wrap(ValueKind.Null, []),
            bool typed => Wrap(ValueKind.Boolean, MemoryPackSerializer.Serialize(typed)),
            byte typed => Wrap(ValueKind.Byte, MemoryPackSerializer.Serialize(typed)),
            sbyte typed => Wrap(ValueKind.SByte, MemoryPackSerializer.Serialize(typed)),
            short typed => Wrap(ValueKind.Int16, MemoryPackSerializer.Serialize(typed)),
            ushort typed => Wrap(ValueKind.UInt16, MemoryPackSerializer.Serialize(typed)),
            int typed => Wrap(ValueKind.Int32, MemoryPackSerializer.Serialize(typed)),
            uint typed => Wrap(ValueKind.UInt32, MemoryPackSerializer.Serialize(typed)),
            long typed => Wrap(ValueKind.Int64, MemoryPackSerializer.Serialize(typed)),
            ulong typed => Wrap(ValueKind.UInt64, MemoryPackSerializer.Serialize(typed)),
            float typed => Wrap(ValueKind.Single, MemoryPackSerializer.Serialize(typed)),
            double typed => Wrap(ValueKind.Double, MemoryPackSerializer.Serialize(typed)),
            decimal typed => Wrap(ValueKind.Decimal, MemoryPackSerializer.Serialize(typed)),
            char typed => Wrap(ValueKind.Char, MemoryPackSerializer.Serialize(typed)),
            string typed => Wrap(ValueKind.String, MemoryPackSerializer.Serialize(typed)),
            Vector2 typed => Wrap(ValueKind.Vector2, MemoryPackSerializer.Serialize(typed)),
            Vector3 typed => Wrap(ValueKind.Vector3, MemoryPackSerializer.Serialize(typed)),
            Vector4 typed => Wrap(ValueKind.Vector4, MemoryPackSerializer.Serialize(typed)),
            Quaternion typed => Wrap(ValueKind.Quaternion, MemoryPackSerializer.Serialize(typed)),
            Matrix4x4 typed => Wrap(ValueKind.Matrix4x4, MemoryPackSerializer.Serialize(typed)),
            StringComparison typed => Wrap(ValueKind.StringComparison, MemoryPackSerializer.Serialize(typed)),
            ELimbEndEffector typed => Wrap(ValueKind.LimbEndEffector, MemoryPackSerializer.Serialize(typed)),
            SourceAssetReference typed => Wrap(ValueKind.SourceAssetReference, MemoryPackSerializer.Serialize(typed)),
            _ => throw new NotSupportedException(
                $"Published animation value at '{context}' has unsupported runtime type '{value.GetType().FullName}'.")
        };

    public static object? Decode(byte[]? encoded, string context)
    {
        if (encoded is null || encoded.Length < HeaderLength
            || encoded[0] != (byte)'X' || encoded[1] != (byte)'A'
            || encoded[2] != (byte)'V' || encoded[3] != (byte)'L')
            throw new InvalidDataException($"Published animation value at '{context}' has an invalid header.");

        if (encoded[4] != Version)
            throw new InvalidDataException($"Published animation value at '{context}' has unsupported version {encoded[4]}.");

        ReadOnlySpan<byte> payload = encoded.AsSpan(HeaderLength);
        return (ValueKind)encoded[5] switch
        {
            ValueKind.Null when payload.IsEmpty => null,
            ValueKind.Boolean => MemoryPackSerializer.Deserialize<bool>(payload),
            ValueKind.Byte => MemoryPackSerializer.Deserialize<byte>(payload),
            ValueKind.SByte => MemoryPackSerializer.Deserialize<sbyte>(payload),
            ValueKind.Int16 => MemoryPackSerializer.Deserialize<short>(payload),
            ValueKind.UInt16 => MemoryPackSerializer.Deserialize<ushort>(payload),
            ValueKind.Int32 => MemoryPackSerializer.Deserialize<int>(payload),
            ValueKind.UInt32 => MemoryPackSerializer.Deserialize<uint>(payload),
            ValueKind.Int64 => MemoryPackSerializer.Deserialize<long>(payload),
            ValueKind.UInt64 => MemoryPackSerializer.Deserialize<ulong>(payload),
            ValueKind.Single => MemoryPackSerializer.Deserialize<float>(payload),
            ValueKind.Double => MemoryPackSerializer.Deserialize<double>(payload),
            ValueKind.Decimal => MemoryPackSerializer.Deserialize<decimal>(payload),
            ValueKind.Char => MemoryPackSerializer.Deserialize<char>(payload),
            ValueKind.String => MemoryPackSerializer.Deserialize<string>(payload),
            ValueKind.Vector2 => MemoryPackSerializer.Deserialize<Vector2>(payload),
            ValueKind.Vector3 => MemoryPackSerializer.Deserialize<Vector3>(payload),
            ValueKind.Vector4 => MemoryPackSerializer.Deserialize<Vector4>(payload),
            ValueKind.Quaternion => MemoryPackSerializer.Deserialize<Quaternion>(payload),
            ValueKind.Matrix4x4 => MemoryPackSerializer.Deserialize<Matrix4x4>(payload),
            ValueKind.StringComparison => MemoryPackSerializer.Deserialize<StringComparison>(payload),
            ValueKind.LimbEndEffector => MemoryPackSerializer.Deserialize<ELimbEndEffector>(payload),
            ValueKind.SourceAssetReference => MemoryPackSerializer.Deserialize<SourceAssetReference>(payload),
            _ => throw new InvalidDataException(
                $"Published animation value at '{context}' has unsupported kind {encoded[5]} or corrupt payload.")
        };
    }

    private static byte[] Wrap(ValueKind kind, byte[] payload)
    {
        byte[] encoded = new byte[HeaderLength + payload.Length];
        encoded[0] = (byte)'X';
        encoded[1] = (byte)'A';
        encoded[2] = (byte)'V';
        encoded[3] = (byte)'L';
        encoded[4] = Version;
        encoded[5] = (byte)kind;
        payload.AsSpan().CopyTo(encoded.AsSpan(HeaderLength));
        return encoded;
    }
}
