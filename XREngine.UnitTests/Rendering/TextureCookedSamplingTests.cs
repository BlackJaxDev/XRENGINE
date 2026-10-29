using System.Buffers.Binary;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class TextureCookedSamplingTests
{
    // Captured from the production writer at f59d9bbae, before sampling extensions existed.
    private static byte[] ReadLegacyFixture()
    {
        using Stream stream = typeof(TextureCookedSamplingTests).Assembly.GetManifestResourceStream("TextureCooking.Legacy")!;
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static XRTexture2D ReadRaw(byte[] bytes)
    {
        XRTexture2D texture = new();
        RuntimeCookedBinaryReader reader = new(bytes);
        ((IRuntimeCookedBinarySerializable)texture).ReadCookedBinary(reader);
        Assert.That(reader.Remaining, Is.Zero);
        return texture;
    }

    private static byte[] WriteRaw(XRTexture2D texture)
    {
        IRuntimeCookedBinarySerializable serializable = texture;
        byte[] bytes = new byte[serializable.CalculateCookedBinarySize()];
        RuntimeCookedBinaryWriter writer = new(bytes);
        serializable.WriteCookedBinary(writer);
        Assert.That(writer.Position, Is.EqualTo(bytes.Length));
        return bytes;
    }

    [Test]
    public void LegacyPayloadKeepsPixelsSamplerAndDefaultEncoding()
    {
        byte[] legacy = ReadLegacyFixture();
        using XRTexture2D texture = ReadRaw(legacy);
        Assert.That(texture.Name, Is.EqualTo("Legacy sampling fixture"));
        Assert.That(texture.MaxAnisotropy, Is.EqualTo(1));
        Assert.That(texture.ImportedColorSpace, Is.EqualTo(ETextureColorSpace.Linear));
        Assert.That(texture.ImportedUsage, Is.EqualTo(ETextureImportUsage.Data));
        Assert.That(texture.EnableComparison, Is.False);
        Assert.That(texture.UWrap, Is.EqualTo(ETexWrapMode.ClampToEdge));
        Assert.That(texture.VWrap, Is.EqualTo(ETexWrapMode.MirroredRepeat));
        Assert.That(texture.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.That(WriteRaw(texture), Is.EqualTo(legacy), "Default settings retain the legacy byte layout.");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public void RawRoundTripPreservesEachExtendedSetting(int setting)
    {
        using XRTexture2D source = ReadRaw(ReadLegacyFixture());
        if (setting is 0 or 6)
            source.ImportedColorSpace = ETextureColorSpace.Srgb;
        if (setting is 1 or 6)
            source.ImportedUsage = ETextureImportUsage.Normal;
        if (setting is 2 or 6)
            source.ImportedNormalMapFlipGreen = true;
        if (setting is 3 or 6)
            source.MaxAnisotropy = 8;
        if (setting is 4 or 6)
            source.EnableComparison = true;
        if (setting is 5 or 6)
            source.CompareFunc = ETextureCompareFunc.Greater;
        byte[] bytes = WriteRaw(source);
        using XRTexture2D clone = ReadRaw(bytes);
        AssertSampling(source, clone);
        Assert.That(clone.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(source.Mipmaps[0].Data!.GetBytes()));
        Assert.That(XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(bytes, 2, true, out var resident), Is.True);
        Assert.That(resident.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(source.Mipmaps[0].Data!.GetBytes()));
        foreach (Mipmap2D mip in resident.Mipmaps)
            mip.Data?.Dispose();
        Assert.That(XRTexture2D.TryReadTextureStreamingManifestFromTextureStreamingPayload(bytes, out var manifest), Is.True);
        Assert.That(manifest.SourceWidth, Is.EqualTo(2));
        TextureSourceMipLayout layout = manifest.Mips[0];
        Assert.That(bytes.AsSpan((int)layout.OffsetBytes, (int)layout.LengthBytes).ToArray(), Is.EqualTo(source.Mipmaps[0].Data!.GetBytes()));
    }

    [Test]
    public void NestedTexturesKeepCustomObjectBoundaries()
    {
        using XRTexture2D first = ReadRaw(ReadLegacyFixture());
        using XRTexture2D second = ReadRaw(ReadLegacyFixture());
        first.MaxAnisotropy = 16;
        first.ImportedColorSpace = ETextureColorSpace.Srgb;
        second.Name = "Second texture";
        byte[] payload = RuntimeCookedBinarySerializer.Serialize(new[] { first, second });
        XRTexture2D[] clones = (XRTexture2D[])RuntimeCookedBinarySerializer.Deserialize(typeof(XRTexture2D[]), payload)!;
        try
        {
            Assert.That(clones.Length, Is.EqualTo(2));
            AssertSampling(first, clones[0]);
            AssertSampling(second, clones[1]);
            Assert.That(clones[1].Name, Is.EqualTo(second.Name));
        }
        finally
        {
            foreach (XRTexture2D clone in clones)
                ((IDisposable)clone).Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CanonicalTextureDecodeOwnsOnlyTheReturnedTexture(bool sharedReferences)
    {
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using XRTexture2D source = ReadRaw(ReadLegacyFixture());
        source.MaxAnisotropy = 4;
        object graph = sharedReferences ? new[] { source, source } : source;
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(graph));
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        object decoded = CookedBinarySerializer.Deserialize(graph.GetType(), payload)!;
        using ObjectCacheOwnership ownership = publication.CompleteWithOwnership();
        XRTexture2D clone = sharedReferences ? ((XRTexture2D[])decoded)[0] : (XRTexture2D)decoded;
        try
        {
            Assert.That(ownership.Objects, Has.Count.EqualTo(1), "Decoding must not retain an unused constructor probe.");
            Assert.That(ownership.Objects[0], Is.SameAs(clone));
            Assert.That(clone, Is.Not.SameAs(source));
            if (sharedReferences)
                Assert.That(((XRTexture2D[])decoded)[1], Is.SameAs(clone));
            AssertSampling(source, clone);
            Assert.That(clone.Mipmaps[0].Data!.GetBytes(), Is.EqualTo(source.Mipmaps[0].Data!.GetBytes()));
        }
        finally
        {
            foreach (Mipmap2D mip in clone.Mipmaps)
                mip.Data?.Dispose();
        }
    }

    [Test]
    public void YamlEnvelopePreservesExtendedSampling()
    {
        using XRTexture2D source = ReadRaw(ReadLegacyFixture());
        source.MaxAnisotropy = 4;
        source.ImportedColorSpace = ETextureColorSpace.Srgb;
        var serializer = new SerializerBuilder().WithTypeConverter(new XRTexture2DYamlTypeConverter()).WithTypeConverter(new DataSourceYamlTypeConverter()).Build();
        var deserializer = new DeserializerBuilder().WithTypeConverter(new XRTexture2DYamlTypeConverter()).WithTypeConverter(new DataSourceYamlTypeConverter()).Build();
        using XRTexture2D clone = deserializer.Deserialize<XRTexture2D>(serializer.Serialize(source));
        AssertSampling(source, clone);
    }

    [Test]
    public void UnknownSamplingVersionAndTruncationRejectBeforeMipReads()
    {
        using XRTexture2D source = ReadRaw(ReadLegacyFixture());
        source.MaxAnisotropy = 4;
        byte[] payload = WriteRaw(source);
        RuntimeCookedBinaryReader reader = new(payload);
        using XRTexture2D scratch = new();
        reader.ReadBaseObject<XRAsset>(scratch);
        int header = (int)reader.Position;
        byte[] unknown = (byte[])payload.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(unknown.AsSpan(header + sizeof(int)), 99);
        Assert.Throws<NotSupportedException>(() => ReadRaw(unknown));
        Assert.That(XRTexture2D.TryDeserializeTextureStreamingPayload(unknown, out _), Is.False);
        Assert.That(XRTexture2D.TryReadTextureStreamingManifestFromTextureStreamingPayload(unknown, out _), Is.False);
        Assert.That(XRTexture2D.TryReadResidentDataFromTextureStreamingPayload(unknown, 2, true, out _), Is.False);
        for (int length = header; length < header + 26; length++)
            Assert.That(XRTexture2D.TryDeserializeTextureStreamingPayload(payload.AsSpan(0, length), out _), Is.False);
    }

    private static void AssertSampling(XRTexture2D expected, XRTexture2D actual)
    {
        Assert.That(actual.ImportedColorSpace, Is.EqualTo(expected.ImportedColorSpace));
        Assert.That(actual.ImportedUsage, Is.EqualTo(expected.ImportedUsage));
        Assert.That(actual.ImportedNormalMapFlipGreen, Is.EqualTo(expected.ImportedNormalMapFlipGreen));
        Assert.That(actual.MaxAnisotropy, Is.EqualTo(expected.MaxAnisotropy));
        Assert.That(actual.EnableComparison, Is.EqualTo(expected.EnableComparison));
        Assert.That(actual.CompareFunc, Is.EqualTo(expected.CompareFunc));
        Assert.That(actual.MinFilter, Is.EqualTo(expected.MinFilter));
        Assert.That(actual.MagFilter, Is.EqualTo(expected.MagFilter));
        Assert.That(actual.UWrap, Is.EqualTo(expected.UWrap));
        Assert.That(actual.VWrap, Is.EqualTo(expected.VWrap));
    }
}
