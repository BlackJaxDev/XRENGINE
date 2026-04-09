using ImageMagick;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using XREngine.Data;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;
using CookedBinaryReader = XREngine.Core.Files.RuntimeCookedBinaryReader;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;
using CookedBinaryWriter = XREngine.Core.Files.RuntimeCookedBinaryWriter;
using ICookedBinarySerializable = XREngine.Core.Files.IRuntimeCookedBinarySerializable;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    private const int StreamableMipSectionMagic = 0x58525453; // XRTS
    private const int StreamableMipSectionVersion = 1;
    private const int StreamableMipDescriptorSize =
        sizeof(uint) + sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(long) + sizeof(int);

    private readonly record struct TextureMipmapReadRequest(bool LoadAll, uint MaxResidentDimension, bool IncludeMipChain)
    {
        public static TextureMipmapReadRequest Full => new(true, 0u, true);
        public static TextureMipmapReadRequest Resident(uint maxResidentDimension, bool includeMipChain)
            => new(false, maxResidentDimension, includeMipChain);
    }

    private readonly record struct StreamableMipmapDescriptor(
        uint Width,
        uint Height,
        EPixelInternalFormat InternalFormat,
        EPixelFormat PixelFormat,
        EPixelType PixelType,
        long DataOffset,
        int DataLength);

    internal static byte[] CreateTextureStreamingPayload(string sourceFilePath, MagickImage image)
    {
        XRTexture2D texture = new()
        {
            Name = Path.GetFileNameWithoutExtension(sourceFilePath),
            FilePath = sourceFilePath,
            MagFilter = ETexMagFilter.Linear,
            MinFilter = ETexMinFilter.LinearMipmapLinear,
            UWrap = ETexWrapMode.Repeat,
            VWrap = ETexWrapMode.Repeat,
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
            SizedInternalFormat = ESizedInternalFormat.Rgba8,
            Mipmaps = GetMipmapsFromImage(image)
        };

        long size = ((ICookedBinarySerializable)texture).CalculateCookedBinarySize();
        if (size > int.MaxValue)
            throw new InvalidOperationException($"Texture streaming payload exceeds maximum supported size ({size} bytes).");

        byte[] payload = new byte[(int)size];
        unsafe
        {
            fixed (byte* ptr = payload)
            {
                using CookedBinaryWriter writer = new(ptr, payload.Length);
                ((ICookedBinarySerializable)texture).WriteCookedBinary(writer);
            }
        }

        return payload;
    }

    internal static bool TryReadResidentDataFromTextureStreamingPayload(
        ReadOnlySpan<byte> payload,
        uint maxResidentDimension,
        bool includeMipChain,
        out TextureStreamingResidentData residentData)
    {
        residentData = default;
        if (payload.IsEmpty)
            return false;

        unsafe
        {
            fixed (byte* ptr = payload)
            {
                using CookedBinaryReader reader = new(ptr, payload.Length);
                XRTexture2D scratch = new();
                scratch.ReadTextureAssetBase(reader);
                scratch.ReadTextureStreamingSettings(reader);
                scratch.GrabPass = ReadGrabPass(reader, scratch);

                Mipmap2D[] mipmaps = ReadMipmaps(
                    reader,
                    TextureMipmapReadRequest.Resident(maxResidentDimension, includeMipChain),
                    out uint sourceWidth,
                    out uint sourceHeight);
                uint residentMaxDimension = mipmaps.Length > 0
                    ? Math.Max(mipmaps[0].Width, mipmaps[0].Height)
                    : 0u;

                residentData = new TextureStreamingResidentData(
                    mipmaps,
                    sourceWidth,
                    sourceHeight,
                    residentMaxDimension);
                return true;
            }
        }
    }

    internal static bool TryReadResidentDataFromTextureAssetFileBytes(
        ReadOnlySpan<byte> assetFileBytes,
        uint maxResidentDimension,
        bool includeMipChain,
        out TextureStreamingResidentData residentData)
    {
        residentData = default;
        if (assetFileBytes.IsEmpty)
            return false;

        try
        {
            if (TryReadResidentDataFromTextureStreamingPayload(assetFileBytes, maxResidentDimension, includeMipChain, out residentData))
                return true;
        }
        catch
        {
        }

        string assetYaml;
        try
        {
            assetYaml = Encoding.UTF8.GetString(assetFileBytes);
        }
        catch
        {
            return false;
        }

        if (!TryExtractTextureStreamingPayloadFromYamlAsset(assetYaml, out byte[]? payload))
            return false;

        return TryReadResidentDataFromTextureStreamingPayload(payload, maxResidentDimension, includeMipChain, out residentData);
    }

    private static bool TryExtractTextureStreamingPayloadFromYamlAsset(string assetYaml, out byte[]? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(assetYaml))
            return false;

        try
        {
            TextureYamlEnvelope? envelope = AssetManager.Deserializer.Deserialize<TextureYamlEnvelope>(new StringReader(assetYaml));
            if (!string.Equals(envelope?.Format, "CookedBinary", StringComparison.Ordinal)
                || envelope.Payload is null
                || envelope.Payload.Length == 0)
            {
                return false;
            }

            payload = envelope.Payload.GetBytes();
            return payload.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void ReadTextureStreamingSettings(CookedBinaryReader reader)
    {
        SamplerName = reader.ReadValue<string>();
        FrameBufferAttachment = reader.ReadValue<EFrameBufferAttachment?>() ?? FrameBufferAttachment;
        MinLOD = ReadStructOrDefault(reader, MinLOD);
        MaxLOD = ReadStructOrDefault(reader, MaxLOD);
        LargestMipmapLevel = ReadStructOrDefault(reader, LargestMipmapLevel);
        SmallestAllowedMipmapLevel = ReadStructOrDefault(reader, SmallestAllowedMipmapLevel);
        AutoGenerateMipmaps = ReadStructOrDefault(reader, AutoGenerateMipmaps);
        AlphaAsTransparency = ReadStructOrDefault(reader, AlphaAsTransparency);
        InternalCompression = ReadStructOrDefault(reader, InternalCompression);

        MagFilter = ReadStructOrDefault(reader, MagFilter);
        MinFilter = ReadStructOrDefault(reader, MinFilter);
        UWrap = ReadStructOrDefault(reader, UWrap);
        VWrap = ReadStructOrDefault(reader, VWrap);
        Rectangle = ReadStructOrDefault(reader, Rectangle);
        Resizable = ReadStructOrDefault(reader, Resizable);
        MultiSampleCount = ReadStructOrDefault(reader, MultiSampleCount);
        FixedSampleLocations = ReadStructOrDefault(reader, FixedSampleLocations);
        ExclusiveSharing = ReadStructOrDefault(reader, ExclusiveSharing);
        LodBias = ReadStructOrDefault(reader, LodBias);
        SizedInternalFormat = ReadStructOrDefault(reader, SizedInternalFormat);
    }

    [RequiresUnreferencedCode("Calls XREngine.Core.Files.RuntimeCookedBinaryWriter.WriteValue(Object)")]
    [RequiresDynamicCode("Calls XREngine.Core.Files.RuntimeCookedBinaryWriter.WriteValue(Object)")]
    private static void WriteStreamableMipmaps(CookedBinaryWriter writer, Mipmap2D[]? mipmaps)
    {
        writer.Write(StreamableMipSectionMagic);
        writer.Write(StreamableMipSectionVersion);

        int mipCount = mipmaps?.Length ?? 0;
        writer.Write(mipCount);

        int previewBaseMipIndex = ResolvePreviewBaseMipIndex(mipmaps);
        writer.Write(previewBaseMipIndex);
        if (mipCount <= 0 || mipmaps is null)
            return;

        long sectionStart = writer.Position - sizeof(int) * 4;
        long descriptorTableStart = writer.Position;
        long dataStart = descriptorTableStart + mipCount * (long)StreamableMipDescriptorSize;
        long runningDataOffset = dataStart - sectionStart;

        for (int i = 0; i < mipCount; i++)
        {
            Mipmap2D mip = mipmaps[i];
            byte[] bytes = mip.Data?.GetBytes() ?? [];
            writer.Write(mip.Width);
            writer.Write(mip.Height);
            writer.Write((int)mip.InternalFormat);
            writer.Write((int)mip.PixelFormat);
            writer.Write((int)mip.PixelType);
            writer.Write(runningDataOffset);
            writer.Write(bytes.Length);
            runningDataOffset += bytes.Length;
        }

        for (int i = 0; i < mipCount; i++)
        {
            byte[] bytes = mipmaps[i].Data?.GetBytes() ?? [];
            writer.WriteBytes(bytes);
        }
    }

    private static Mipmap2D[] ReadMipmaps(
        CookedBinaryReader reader,
        TextureMipmapReadRequest request,
        out uint sourceWidth,
        out uint sourceHeight)
    {
        long sectionStart = reader.Position;
        if (reader.Remaining >= sizeof(int) * 4)
        {
            int magic = reader.ReadInt32();
            if (magic == StreamableMipSectionMagic)
                return ReadStreamableMipmaps(reader, sectionStart, request, out sourceWidth, out sourceHeight);

            reader.Position = sectionStart;
        }

        Mipmap2D[] legacy = ReadMipmapsLegacy(reader);
        sourceWidth = legacy.Length > 0 ? legacy[0].Width : 0u;
        sourceHeight = legacy.Length > 0 ? legacy[0].Height : 0u;
        return request.LoadAll
            ? legacy
            : SelectResidentMipmaps(legacy, request.MaxResidentDimension, request.IncludeMipChain);
    }

    private static Mipmap2D[] ReadStreamableMipmaps(
        CookedBinaryReader reader,
        long sectionStart,
        TextureMipmapReadRequest request,
        out uint sourceWidth,
        out uint sourceHeight)
    {
        int version = reader.ReadInt32();
        if (version != StreamableMipSectionVersion)
            throw new InvalidOperationException($"Unsupported texture streaming mip section version '{version}'.");

        int mipCount = reader.ReadInt32();
        int previewBaseMipIndex = reader.ReadInt32();
        if (mipCount <= 0)
        {
            sourceWidth = 0u;
            sourceHeight = 0u;
            return [];
        }

        StreamableMipmapDescriptor[] descriptors = new StreamableMipmapDescriptor[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            uint width = reader.ReadUInt32();
            uint height = reader.ReadUInt32();
            EPixelInternalFormat internalFormat = (EPixelInternalFormat)reader.ReadInt32();
            EPixelFormat pixelFormat = (EPixelFormat)reader.ReadInt32();
            EPixelType pixelType = (EPixelType)reader.ReadInt32();
            long dataOffset = reader.ReadInt64();
            int dataLength = reader.ReadInt32();
            descriptors[i] = new StreamableMipmapDescriptor(width, height, internalFormat, pixelFormat, pixelType, dataOffset, dataLength);
        }

        sourceWidth = descriptors[0].Width;
        sourceHeight = descriptors[0].Height;

        int baseMipIndex;
        int endExclusive;
        if (request.LoadAll)
        {
            baseMipIndex = 0;
            endExclusive = descriptors.Length;
        }
        else
        {
            baseMipIndex = ResolveResidentBaseMipIndex(descriptors, request.MaxResidentDimension, previewBaseMipIndex);
            endExclusive = request.IncludeMipChain ? descriptors.Length : baseMipIndex + 1;
        }

        if (endExclusive <= baseMipIndex)
            return [];

        Mipmap2D[] mipmaps = new Mipmap2D[endExclusive - baseMipIndex];
        for (int index = baseMipIndex; index < endExclusive; index++)
        {
            StreamableMipmapDescriptor descriptor = descriptors[index];
            reader.Position = sectionStart + descriptor.DataOffset;
            byte[] bytes = reader.ReadBytes(descriptor.DataLength);
            mipmaps[index - baseMipIndex] = new Mipmap2D
            {
                Width = descriptor.Width,
                Height = descriptor.Height,
                InternalFormat = descriptor.InternalFormat,
                PixelFormat = descriptor.PixelFormat,
                PixelType = descriptor.PixelType,
                Data = bytes.Length == 0 ? null : new DataSource(bytes)
            };
        }

        StreamableMipmapDescriptor lastDescriptor = descriptors[^1];
        reader.Position = sectionStart + lastDescriptor.DataOffset + lastDescriptor.DataLength;
        return mipmaps;
    }

    [RequiresUnreferencedCode("Calls XREngine.Core.Files.RuntimeCookedBinaryReader.ReadValue<T>()")]
    [RequiresDynamicCode("Calls XREngine.Core.Files.RuntimeCookedBinaryReader.ReadValue<T>()")]
    private static Mipmap2D[] ReadMipmapsLegacy(CookedBinaryReader reader)
    {
        int mipCount = ReadStructOrDefault(reader, 0);
        if (mipCount <= 0)
            return [];

        Mipmap2D[] mipmaps = new Mipmap2D[mipCount];
        for (int i = 0; i < mipCount; i++)
        {
            uint width = ReadStructOrDefault(reader, 0u);
            uint height = ReadStructOrDefault(reader, 0u);
            EPixelInternalFormat internalFormat = ReadStructOrDefault(reader, EPixelInternalFormat.Rgba8);
            EPixelFormat pixelFormat = ReadStructOrDefault(reader, EPixelFormat.Rgba);
            EPixelType pixelType = ReadStructOrDefault(reader, EPixelType.UnsignedByte);
            byte[]? bytes = reader.ReadValue<byte[]>();

            mipmaps[i] = new Mipmap2D
            {
                Width = width,
                Height = height,
                InternalFormat = internalFormat,
                PixelFormat = pixelFormat,
                PixelType = pixelType,
                Data = bytes is null ? null : new DataSource(bytes)
            };
        }

        return mipmaps;
    }

    [RequiresUnreferencedCode("Calls XREngine.Core.Files.RuntimeCookedBinarySerializer.CalculateSize(Object)")]
    [RequiresDynamicCode("Calls XREngine.Core.Files.RuntimeCookedBinarySerializer.CalculateSize(Object)")]
    private static long CalculateStreamableMipmapSize(Mipmap2D[]? mipmaps)
    {
        long size = sizeof(int) * 4;
        if (mipmaps is null || mipmaps.Length == 0)
            return size;

        size += mipmaps.Length * (long)StreamableMipDescriptorSize;
        for (int i = 0; i < mipmaps.Length; i++)
        {
            byte[] bytes = mipmaps[i].Data?.GetBytes() ?? [];
            size += bytes.Length;
        }

        return size;
    }

    private static int ResolvePreviewBaseMipIndex(Mipmap2D[]? mipmaps)
    {
        if (mipmaps is null || mipmaps.Length == 0)
            return 0;

        uint previewSize = GetPreviewResidentSize(Math.Max(mipmaps[0].Width, mipmaps[0].Height));
        return ResolveResidentBaseMipIndex(mipmaps, previewSize);
    }

    private static int ResolveResidentBaseMipIndex(
        StreamableMipmapDescriptor[] descriptors,
        uint maxResidentDimension,
        int previewBaseMipIndex)
    {
        if (descriptors.Length == 0)
            return 0;

        if (maxResidentDimension == 0)
            return Math.Clamp(previewBaseMipIndex, 0, descriptors.Length - 1);

        uint targetDimension = Math.Max(1u, maxResidentDimension);
        for (int index = 0; index < descriptors.Length; index++)
        {
            if (Math.Max(descriptors[index].Width, descriptors[index].Height) <= targetDimension)
                return index;
        }

        return descriptors.Length - 1;
    }

    private sealed class TextureYamlEnvelope
    {
        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}
