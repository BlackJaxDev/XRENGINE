using System;
using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;
using ICookedBinarySerializable = XREngine.Core.Files.IRuntimeCookedBinarySerializable;

namespace XREngine.Rendering.Models.Gaussian.Cache;

public sealed partial class GaussianCacheChunk : ICookedBinarySerializable
{
#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    void ICookedBinarySerializable.WriteCookedBinary(CookedBinaryWriter writer)
    {
        long start = writer.Position;
        writer.WriteBaseObject<XRAsset>(this);
        writer.Write(_index);
        writer.Write(_data.Length);
        writer.WriteBytes(_data);

        long expected = CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset)) + PayloadSize();
        if (writer.Position - start != expected)
            throw new InvalidOperationException(
                $"Cooked gaussian cache chunk wrote {writer.Position - start} bytes but calculated {expected}.");
    }

#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    void ICookedBinarySerializable.ReadCookedBinary(CookedBinaryReader reader)
    {
        reader.ReadBaseObject<XRAsset>(this);
        Index = reader.ReadInt32();
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.Remaining)
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.MalformedCode,
                $"Gaussian cache chunk data length {length} is larger than the remaining data.");
        Data = reader.ReadBytes(length);
    }

#if !XRE_PUBLISHED
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
#endif
    long ICookedBinarySerializable.CalculateCookedBinarySize()
        => CookedBinarySerializer.CalculateBaseObjectSize(this, typeof(XRAsset)) + PayloadSize();

    private long PayloadSize()
        => sizeof(int) + sizeof(int) + _data.Length;
}
