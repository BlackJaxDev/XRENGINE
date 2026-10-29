using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    // The first byte cannot be a legacy cooked value marker (0 through 25).
    private const int CookedSamplingMagic = 0x58525354;
    private const int CookedSamplingVersion = 1;
    private const int CookedSamplingSize = sizeof(int) * 5 + sizeof(float) + sizeof(byte) * 2;

    private bool HasExtendedCookedSampling
        => ImportedColorSpace != ETextureColorSpace.Linear
        || ImportedUsage != ETextureImportUsage.Data
        || ImportedNormalMapFlipGreen
        || MaxAnisotropy != 1.0f
        || EnableComparison
        || CompareFunc != ETextureCompareFunc.LessOrEqual;

    /// <summary>Preserves import interpretation and sampling state before the legacy settings.</summary>
    private void WriteCookedSampling(RuntimeCookedBinaryWriter writer)
    {
        if (!HasExtendedCookedSampling)
            return;

        writer.Write(CookedSamplingMagic);
        writer.Write(CookedSamplingVersion);
        writer.Write((int)ImportedColorSpace);
        writer.Write((int)ImportedUsage);
        writer.Write(ImportedNormalMapFlipGreen);
        writer.Write(MaxAnisotropy);
        writer.Write(EnableComparison);
        writer.Write((int)CompareFunc);
    }

    /// <summary>Reads the optional extension without consuming the following legacy settings.</summary>
    private void ReadCookedSampling(RuntimeCookedBinaryReader reader)
    {
        ImportedColorSpace = ETextureColorSpace.Linear;
        ImportedUsage = ETextureImportUsage.Data;
        ImportedNormalMapFlipGreen = false;
        MaxAnisotropy = 1.0f;
        EnableComparison = false;
        CompareFunc = ETextureCompareFunc.LessOrEqual;

        long start = reader.Position;
        byte marker = reader.ReadByte();
        reader.Position = start;
        if (marker != (byte)(CookedSamplingMagic & 0xFF))
            return;

        if (reader.ReadInt32() != CookedSamplingMagic)
            throw new NotSupportedException("TextureCooked.InvalidSamplingHeader: the cooked sampling header is invalid.");
        int version = reader.ReadInt32();
        if (version != CookedSamplingVersion)
            throw new NotSupportedException($"TextureCooked.UnsupportedSamplingVersion: version {version} is not supported.");

        ImportedColorSpace = (ETextureColorSpace)reader.ReadInt32();
        ImportedUsage = (ETextureImportUsage)reader.ReadInt32();
        ImportedNormalMapFlipGreen = reader.ReadBoolean();
        MaxAnisotropy = reader.ReadSingle();
        EnableComparison = reader.ReadBoolean();
        CompareFunc = (ETextureCompareFunc)reader.ReadInt32();
    }

    private long CalculateCookedSamplingSize() => HasExtendedCookedSampling ? CookedSamplingSize : 0;
}
