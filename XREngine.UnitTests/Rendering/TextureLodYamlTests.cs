using NUnit.Framework;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks authored texture limits against their nonzero construction defaults.</summary>
[TestFixture]
public sealed class TextureLodYamlTests
{
    [TestCase(-1000, 1000, 1000)]
    [TestCase(0, 0, 0)]
    [TestCase(0, 1000, 1000)]
    [TestCase(-1000, 0, 1000)]
    [TestCase(-4, 7, 0)]
    [TestCase(1, 2, 3)]
    public void CubeYamlPreservesAuthoredLimits(int minimum, int maximum, int smallest)
    {
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using XRTextureCube source = new(1, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
        {
            MinLOD = minimum,
            MaxLOD = maximum,
            SmallestAllowedMipmapLevel = smallest,
        };
        for (int face = 0; face < 6; face++)
            source.Mipmaps[0].Sides[face].DataBytes = [(byte)(face * 30), 20, 40, 255];
        var serializer = new SerializerBuilder().EnsureRoundtrip()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .WithTypeConverter(new DataSourceYamlTypeConverter()).Build();
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties()
            .WithTypeConverter(new DataSourceYamlTypeConverter()).Build();
        XRTextureCube? restored = null;
        try
        {
            restored = deserializer.Deserialize<XRTextureCube>(serializer.Serialize(source));
            Assert.That(restored.MinLOD, Is.EqualTo(minimum));
            Assert.That(restored.MaxLOD, Is.EqualTo(maximum));
            Assert.That(restored.SmallestAllowedMipmapLevel, Is.EqualTo(smallest));
            for (int face = 0; face < 6; face++)
                Assert.That(restored.Mipmaps[0].Sides[face].Data!.GetBytes(),
                    Is.EqualTo(source.Mipmaps[0].Sides[face].Data!.GetBytes()));
        }
        finally
        {
            if (restored is not null)
            {
                foreach (Mipmap2D face in restored.Mipmaps[0].Sides) face.Data?.Dispose();
                restored.Destroy(true);
            }
            foreach (Mipmap2D face in source.Mipmaps[0].Sides) face.Data?.Dispose();
        }
    }
}
