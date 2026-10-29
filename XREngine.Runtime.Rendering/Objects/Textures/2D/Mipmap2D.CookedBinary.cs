using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class Mipmap2D : IRuntimeCookedBinarySerializable
{
    private const int CookedMipVersion = 1;
    private const int CookedMipHeaderBytes = 7 * sizeof(int);

    /// <summary>Writes CPU pixels explicitly because reflection excludes both pixel accessors.</summary>
    void IRuntimeCookedBinarySerializable.WriteCookedBinary(RuntimeCookedBinaryWriter writer)
    {
        writer.Write(CookedMipVersion);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((int)InternalFormat);
        writer.Write((int)PixelFormat);
        writer.Write((int)PixelType);
        writer.Write(Data is null ? -1 : checked((int)Data.Length));
        if (Data is { } pixels)
            writer.Write(pixels.GetBytes());
    }

    long IRuntimeCookedBinarySerializable.CalculateCookedBinarySize()
        => CookedMipHeaderBytes + (Data is null ? 0L : checked((int)Data.Length));

    /// <summary>Restores an owned pixel buffer without restoring upload or renderer state.</summary>
    void IRuntimeCookedBinarySerializable.ReadCookedBinary(RuntimeCookedBinaryReader reader)
    {
        int version = reader.ReadInt32();
        if (version != CookedMipVersion)
            throw new InvalidDataException($"Cooked mip version {version} is not supported.");
        uint width = reader.ReadUInt32();
        uint height = reader.ReadUInt32();
        EPixelInternalFormat internalFormat = (EPixelInternalFormat)reader.ReadInt32();
        EPixelFormat pixelFormat = (EPixelFormat)reader.ReadInt32();
        EPixelType pixelType = (EPixelType)reader.ReadInt32();
        int length = reader.ReadInt32();
        if (length < -1 || length > reader.Remaining)
            throw new InvalidDataException("Cooked mip pixel length is invalid.");
        byte[]? bytes = length < 0 ? null : reader.ReadBytes(length);
        Width = width;
        Height = height;
        InternalFormat = internalFormat;
        PixelFormat = pixelFormat;
        PixelType = pixelType;
        Data = bytes is null ? null : new DataSource(bytes);
    }
}
