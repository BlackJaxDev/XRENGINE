using System.Reflection;
using System.Buffers.Binary;
using NUnit.Framework;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Editor.Publishing;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.UnitTests.Rendering;

[TestFixture, NonParallelizable]
public sealed class BrowserSkyTextureCookingTests
{
    private IDisposable _wrappers = null!;
    private IDisposable _materialTarget = null!;
    private readonly List<DataSource> _pixels = [];

    [SetUp]
    public void SetUp()
    {
        _wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        _materialTarget = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (DataSource pixels in _pixels)
            pixels.Dispose();
        _pixels.Clear();
        _materialTarget.Dispose();
        _wrappers.Dispose();
    }

    private Mipmap2D Mip(uint width, uint height, EPixelType type = EPixelType.UnsignedByte)
    {
        int bytes = type == EPixelType.Float ? 16 : type == EPixelType.HalfFloat ? 8 : 4;
        DataSource pixels = DataSource.FromArray(Enumerable.Range(0, checked((int)(width * height) * bytes)).Select(i => (byte)i).ToArray());
        _pixels.Add(pixels);
        return new Mipmap2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, type, false) { Data = pixels };
    }

    private XRTexture Create(bool cube, ESizedInternalFormat format = ESizedInternalFormat.Rgba8,
        EPixelType type = EPixelType.UnsignedByte, int levels = 3)
    {
        if (!cube)
            return new XRTexture2D
            {
                SizedInternalFormat = format, AutoGenerateMipmaps = false,
                Mipmaps = Enumerable.Range(0, levels).Select(level => Mip(Math.Max(1u, 4u >> level), Math.Max(1u, 2u >> level), type)).ToArray(),
            };
        return new XRTextureCube(Enumerable.Range(0, levels).Select(level =>
        {
            uint size = Math.Max(1u, 4u >> level);
            return new CubeMipmap(Mip(size, size, type), Mip(size, size, type), Mip(size, size, type),
                Mip(size, size, type), Mip(size, size, type), Mip(size, size, type));
        }).ToArray()) { SizedInternalFormat = format, AutoGenerateMipmaps = false };
    }

    private static Mipmap2D[] Faces(XRTexture source, int level = 0)
        => source is XRTextureCube cube ? cube.Mipmaps[level].Sides : [((XRTexture2D)source).Mipmaps[level]];

    private static void Validate(XRTexture texture) => BrowserSkyTextureCookProfile.Validate(new SkyboxComponent
    {
        Mode = ESkyboxMode.Texture, Texture = texture,
        Projection = texture is XRTextureCube ? ESkyboxProjection.Cubemap : ESkyboxProjection.Equirectangular,
    });

    [TestCase(false, ESizedInternalFormat.Rgba8, EPixelType.UnsignedByte)]
    [TestCase(true, ESizedInternalFormat.Rgba8, EPixelType.UnsignedByte)]
    [TestCase(false, ESizedInternalFormat.Srgb8Alpha8, EPixelType.UnsignedByte)]
    [TestCase(true, ESizedInternalFormat.Srgb8Alpha8, EPixelType.UnsignedByte)]
    [TestCase(false, ESizedInternalFormat.Rgba16f, EPixelType.HalfFloat)]
    [TestCase(true, ESizedInternalFormat.Rgba16f, EPixelType.HalfFloat)]
    [TestCase(true, ESizedInternalFormat.Rgba16f, EPixelType.Float)]
    public void AdmittedEncodingKeepsAuthoredBytes(bool cube, ESizedInternalFormat format, EPixelType type)
    {
        using XRTexture source = Create(cube, format, type);
        DataSource pixels = Faces(source)[0].Data!;
        byte[] before = pixels.GetBytes();
        Assert.DoesNotThrow(() => Validate(source));
        Assert.That(Faces(source)[0].Data, Is.SameAs(pixels));
        Assert.That(pixels.GetBytes(), Is.EqualTo(before));
    }

    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    public void PartialAuthoredChainsRemainSupported(bool cube, int levels)
    {
        using XRTexture source = Create(cube, levels: levels);
        Assert.DoesNotThrow(() => Validate(source));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CookedRoundTripRetainsEveryMipAndFace(bool cube)
    {
        using XRTexture source = Create(cube);
        Validate(source);
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(source));
        byte[] envelope = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(source.GetType()), CookedAssetFormat.BinaryV2, payload));
        using XRTexture clone = CookedAssetReader.LoadAsset<XRTexture>(envelope)!;
        Assert.DoesNotThrow(() => Validate(clone));
        for (int level = 0; level < 3; level++)
        {
            Mipmap2D[] expected = Faces(source, level);
            Mipmap2D[] actual = Faces(clone, level);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int face = 0; face < expected.Length; face++)
            {
                _pixels.Add(actual[face].Data!);
                Assert.That(actual[face].Data!.GetBytes(), Is.EqualTo(expected[face].Data!.GetBytes()));
            }
        }
    }

    [Test]
    public void RejectsMalformedMipStorage([Values(false, true)] bool cube, [Values("missing", "short", "long", "disposed", "shape", "format", "type")] string damage)
    {
        using XRTexture source = Create(cube);
        Mipmap2D mip = Faces(source, 1)[cube ? 5 : 0];
        switch (damage)
        {
            case "missing": mip.Data = null; break;
            case "short": mip.Data!.Length--; break;
            case "long": mip.Data!.Length++; break;
            case "disposed": mip.Data!.Dispose(); break;
            case "shape": mip.Width++; break;
            case "format": mip.PixelFormat = EPixelFormat.Rgb; break;
            case "type": mip.PixelType = EPixelType.Float; break;
        }
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Validate(source))!;
        Assert.That(error.Message, Does.Contain("BrowserCook.SkyTextureStorageUnsupported"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsRepeatedTerminalMip(bool cube)
    {
        using XRTexture source = Create(cube, levels: 4);
        Assert.Throws<NotSupportedException>(() => Validate(source));
    }

    [Test]
    public void RejectsMissingCubeFace()
    {
        using XRTexture source = Create(true);
        Faces(source, 1)[5] = null!;
        Assert.Throws<NotSupportedException>(() => Validate(source));
    }

    [Test]
    public void FloatUploadEncodingIsSpecificToLayeredTextures()
    {
        using XRTexture source = Create(false, ESizedInternalFormat.Rgba16f, EPixelType.Float);
        Assert.Throws<NotSupportedException>(() => Validate(source));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsGpuWrittenSources(bool cube)
    {
        using XRTexture source = Create(cube);
        typeof(XRTexture).GetMethod("MarkGpuWritable", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(source, null);
        Assert.Throws<NotSupportedException>(() => Validate(source));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectsExternalMemorySources(bool cube)
    {
        using XRTexture source = Create(cube);
        source.OpenGlExternalMemoryLabel = "External producer";
        Assert.Throws<NotSupportedException>(() => Validate(source));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StorageUsageRequiresLinearFormat(bool cube)
    {
        using XRTexture srgb = Create(cube, ESizedInternalFormat.Srgb8Alpha8);
        srgb.RequiresStorageUsage = true;
        Assert.Throws<NotSupportedException>(() => Validate(srgb));
        using XRTexture linear = Create(cube);
        linear.RequiresStorageUsage = true;
        Assert.DoesNotThrow(() => Validate(linear));
    }


    [Test]
    public void PublishedFactoriesDecodeTheCanonicalMipPayload()
    {
        Mipmap2D source = Mip(2, 2, EPixelType.HalfFloat);
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(source));
        EXRRuntimeBuildKind previous = XRRuntimeEnvironment.BuildKind;
        using IDisposable serializers = RenderingSerializationRegistration.Install();
        try
        {
            XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.PublishedAot);
            AotRuntimeMetadataStore.ResetForTestsOrReconfiguration();
            Mipmap2D clone = (Mipmap2D)CookedBinarySerializer.Deserialize(typeof(Mipmap2D), payload)!;
            _pixels.Add(clone.Data!);
            Assert.That(clone.Data!.GetBytes(), Is.EqualTo(source.Data!.GetBytes()));
            Assert.That(clone.PixelType, Is.EqualTo(EPixelType.HalfFloat));
        }
        finally
        {
            XRRuntimeEnvironment.ConfigureBuildKind(previous);
            AotRuntimeMetadataStore.ResetForTestsOrReconfiguration();
        }
    }

    [TestCase(-1)]
    [TestCase(0)]
    public void MipPayloadPreservesNullAndEmptyStorage(int length)
    {
        Mipmap2D source = new();
        if (length == 0)
        {
            source.Data = new DataSource(Array.Empty<byte>());
            _pixels.Add(source.Data);
        }
        byte[] payload = RuntimeCookedBinarySerializer.Serialize(source);
        Mipmap2D clone = (Mipmap2D)RuntimeCookedBinarySerializer.Deserialize(typeof(Mipmap2D), payload)!;
        Assert.That(clone.Data is null, Is.EqualTo(length < 0));
        if (clone.Data is { } data)
        {
            _pixels.Add(data);
            Assert.That(data.Length, Is.Zero);
        }
    }

    [TestCase("version")]
    [TestCase("negative")]
    [TestCase("oversized")]
    [TestCase("truncated")]
    public void MipPayloadRejectsInvalidHeadersBeforeMutation(string damage)
    {
        IRuntimeCookedBinarySerializable source = Mip(2, 2);
        byte[] raw = new byte[source.CalculateCookedBinarySize()];
        source.WriteCookedBinary(new RuntimeCookedBinaryWriter(raw));
        if (damage == "version") BinaryPrimitives.WriteInt32LittleEndian(raw, 2);
        if (damage == "negative") BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(24), -2);
        if (damage == "oversized") BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(24), int.MaxValue);
        if (damage == "truncated") Array.Resize(ref raw, raw.Length - 1);
        Mipmap2D destination = Mip(1, 1);
        DataSource before = destination.Data!;
        Assert.Throws<InvalidDataException>(() => ((IRuntimeCookedBinarySerializable)destination).ReadCookedBinary(new RuntimeCookedBinaryReader(raw)));
        Assert.That(destination.Data, Is.SameAs(before));
        Assert.That(destination.Width, Is.EqualTo(1));
    }

    [TestCase(ESkyboxMode.Gradient)]
    [TestCase(ESkyboxMode.SolidColor)]
    [TestCase(ESkyboxMode.DynamicProcedural)]
    public void NonTextureModesDoNotRequirePixels(ESkyboxMode mode)
        => Assert.DoesNotThrow(() => BrowserSkyTextureCookProfile.Validate(new SkyboxComponent { Mode = mode }));
}
