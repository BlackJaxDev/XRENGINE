using System.Buffers.Binary;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.UnitTests.Core;

namespace XREngine.UnitTests.Editor;

[TestFixture, NonParallelizable]
public sealed class NativeTextureFileImportTests
{
    private IRuntimeImageCodec? _previousCodec;
    private IRuntimeRenderingHostServices? _previousHost;
    private NativeTextureImportTestCodec _codec = null!;
    private NativeAssetRecoveryTestScope _scope = null!;
    private byte[] _png = null!;

    [SetUp]
    public void SetUp()
    {
        _previousCodec = RuntimeImageCodecs.Current;
        _previousHost = RuntimeRenderingHostServices.Current;
        RuntimeImageCodecs.Current = _codec = new();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
        _scope = new();
        using RuntimeImage pixels = new(2, 1, RuntimePixelFormat.Rgba8, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 });
        _png = _codec.EncodePng(pixels);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        RuntimeImageCodecs.Current = _previousCodec;
        RuntimeRenderingHostServices.Current = _previousHost!;
    }

    [TestCase(8193u, 1u)]
    [TestCase(4096u, 4096u)]
    [TestCase(0u, 1u)]
    public void InvalidPixelBoundsRejectBeforeDecode(uint width, uint height)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_png.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(_png.AsSpan(20), height);
        Assert.Throws<InvalidDataException>(() => Import());
        Assert.That(_codec.DecodeCalls, Is.Zero);
        Assert.That(Directory.GetFiles(_scope.AssetsRoot), Is.Empty);
    }

    [Test]
    public void OversizedSourceRejectsBeforeDecode()
    {
        Assert.Throws<InvalidDataException>(() => NativeTextureFileImport.Decode(
            new byte[NativeTextureFileImport.MaximumSourceBytes + 1], "large", _scope.AssetsRoot, () => true));
        Assert.That(_codec.DecodeCalls, Is.Zero);
    }

    [Test]
    public void UnsupportedFormatHasNoDecodeFallback()
    {
        Assert.Throws<NotSupportedException>(() => NativeTextureFileImport.Decode("GIF89a"u8.ToArray(), "gif", _scope.AssetsRoot, () => true));
        Assert.That(_codec.DecodeCalls, Is.Zero);
    }

    [Test]
    public void TruncatedPngRejectsBeforeDecode()
    {
        _png = _png[..^1];
        Assert.Throws<InvalidDataException>(() => Import());
        Assert.That(_codec.DecodeCalls, Is.Zero);
    }

    [Test]
    public void StaticJpegPassesHeaderAdmissionAndDecodesOneFrame()
    {
        using RuntimeImage pixels = new(2, 1, RuntimePixelFormat.Rgb8, new byte[] { 255, 0, 0, 0, 255, 0 });
        byte[] jpeg = _codec.Encode(pixels, RuntimeImageFileFormat.Jpeg, 90, true);
        XRTexture2D texture = NativeTextureFileImport.Decode(jpeg, "jpeg", _scope.AssetsRoot, () => true);
        try
        {
            Assert.That(_codec.DecodeCalls, Is.EqualTo(1));
            Assert.That(texture.Mipmaps[0].Width, Is.EqualTo(2));
            Assert.That(texture.Mipmaps[0].Height, Is.EqualTo(1));
        }
        finally { texture.Destroy(true); }
    }

    [TestCase(new byte[] { 255, 216, 255, 192, 0, 1, 255, 217 })]
    [TestCase(new byte[] { 255, 216, 255, 192, 255, 255, 255, 217 })]
    [TestCase(new byte[] { 255, 216, 255, 192, 0, 11, 8, 0, 1, 0, 1, 1, 1, 17, 0, 255, 217 })]
    public void MalformedJpegSegmentsRejectBeforeDecode(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => NativeTextureFileImport.Decode(bytes, "jpeg", _scope.AssetsRoot, () => true));
        Assert.That(_codec.DecodeCalls, Is.Zero);
    }

    [Test]
    public void RejectedOwnerDoesNotReadSourceOrDecode()
    {
        Assert.Throws<InvalidOperationException>(() => NativeTextureFileImport.Read(
            Path.Combine(_scope.ProjectRoot, "missing.png"), _scope.AssetsRoot, () => false));
        Assert.That(_codec.DecodeCalls, Is.Zero);
    }

    [Test]
    public void OwnerChangedDuringDecodeDoesNotCreateAnAsset()
    {
        bool ownerCurrent = true;
        _codec.BeforeDecode = () => ownerCurrent = false;
        Assert.Throws<InvalidOperationException>(() => NativeTextureFileImport.Decode(_png, "drop", _scope.AssetsRoot, () => ownerCurrent));
        Assert.That(_codec.DecodeCalls, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(_scope.AssetsRoot), Is.Empty);
    }

    [Test]
    public void MaterialSourceChangeDuringDecodeRejectsTheOriginalAdmission()
    {
        var original = new XRMaterial();
        var replacement = new XRMaterial();
        var material = new XRMaterial { SourceAsset = original };
        try
        {
            _codec.BeforeDecode = () => material.SourceAsset = replacement;
            Assert.Throws<InvalidOperationException>(() => NativeTextureFileImport.Decode(_png, "drop", _scope.AssetsRoot,
                () => ReferenceEquals(material.SourceAsset, original)));
            Assert.That(material.SourceAsset, Is.SameAs(replacement));
            Assert.That(Directory.GetFiles(_scope.AssetsRoot), Is.Empty);
        }
        finally { material.Destroy(true); replacement.Destroy(true); original.Destroy(true); }
    }

    [Test]
    public void RuntimeMaterialWithoutAnAuthoredDocumentIsRejected()
    {
        var material = new XRMaterial();
        try { Assert.That(EditorFlyingCameraPawnComponent.HasAuthoredTextureDropSource(material, _scope.AssetsRoot), Is.False); }
        finally { material.Destroy(true); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AuthoredDocumentMustRemainInsideTheProject(bool inside)
    {
        var material = new XRMaterial { FilePath = Path.Combine(inside ? _scope.AssetsRoot : _scope.ProjectRoot, "material.asset") };
        try
        {
            material.SerializeTo(material.FilePath, AssetManager.Serializer);
            Assert.That(EditorFlyingCameraPawnComponent.HasAuthoredTextureDropSource(material, _scope.AssetsRoot), Is.EqualTo(inside));
        }
        finally { material.Destroy(true); }
    }

    [Test]
    public void EmbeddedMaterialMustBelongToItsDeclaredDocument()
    {
        var source = new XRMaterial { FilePath = Path.Combine(_scope.AssetsRoot, "owner.asset") };
        var material = new XRMaterial { SourceAsset = source };
        try
        {
            source.SerializeTo(source.FilePath, AssetManager.Serializer);
            Assert.That(EditorFlyingCameraPawnComponent.HasAuthoredTextureDropSource(material, _scope.AssetsRoot), Is.False);
        }
        finally { material.Destroy(true); source.Destroy(true); }
    }

    [Test]
    public void SerializedImportRetainsPixelsWithoutTheSourcePath()
    {
        string source = Path.Combine(_scope.ProjectRoot, "source.png");
        File.WriteAllBytes(source, _png);
        XRTexture2D texture = NativeTextureFileImport.Read(source, _scope.AssetsRoot, () => true);
        XRTexture2D? restored = null;
        try
        {
            Assert.That(texture.OriginalPath, Is.Null.Or.Empty);
            Assert.That(texture.FilePath, Is.EqualTo(Path.Combine(_scope.AssetsRoot, "source.asset")));
            byte[] pixels = texture.Mipmaps[0].DataBytes!;
            byte[] payload = CookedBinarySerializer.Serialize(texture);
            File.WriteAllBytes(source, "changed after import"u8.ToArray());
            restored = (XRTexture2D)CookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload)!;
            Assert.That(restored.Mipmaps, Has.Length.EqualTo(1));
            Assert.That(restored.Mipmaps[0].Width, Is.EqualTo(2));
            Assert.That(restored.Mipmaps[0].Height, Is.EqualTo(1));
            Assert.That(restored.Mipmaps[0].DataBytes, Is.EqualTo(pixels));
            Assert.That(restored.OriginalPath, Is.Null.Or.Empty);
            Assert.That(Directory.GetFiles(_scope.AssetsRoot), Is.Empty);
        }
        finally { restored?.Destroy(true); texture.Destroy(true); }
    }

    private XRTexture2D Import() => NativeTextureFileImport.Decode(_png, "drop", _scope.AssetsRoot, () => true);
}
