using System;
using System.Text;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RuntimeCookedBinarySerializerTests
{
    [Test]
    public void Deserialize_UnwrapsCookedYamlEnvelope_WithUtf8Bom()
    {
        XRTexture2D texture = new()
        {
            Name = "YamlTexture",
            FilePath = "Assets/YamlTexture.asset"
        };

        string yaml = AssetManager.Serializer.Serialize(texture);
        byte[] payload = WithUtf8Bom(Encoding.UTF8.GetBytes(yaml));

        XRTexture2D? clone = RuntimeCookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload) as XRTexture2D;

        clone.ShouldNotBeNull();
        clone!.Name.ShouldBe(texture.Name);
        clone.FilePath.ShouldBe(texture.FilePath);
    }

    [Test]
    public void Deserialize_ConvertsCompatibleArrayElementTypes()
    {
        byte[] payload = RuntimeCookedBinarySerializer.Serialize(new[] { 1, 2, 3 });

        long[]? values = RuntimeCookedBinarySerializer.Deserialize(typeof(long[]), payload) as long[];

        values.ShouldNotBeNull();
        values.ShouldBe([1L, 2L, 3L]);
    }

    [Test]
    public void Deserialize_TextPayloadWithoutEnvelope_ThrowsHelpfulException()
    {
        byte[] payload = WithUtf8Bom(Encoding.UTF8.GetBytes("Format: PlainText\nValue: nope\n"));

        NotSupportedException ex = Should.Throw<NotSupportedException>(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload));

        ex.Message.ShouldContain("UTF-8 text/YAML");
    }

    private static byte[] WithUtf8Bom(byte[] bytes)
    {
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] payload = new byte[bom.Length + bytes.Length];
        Array.Copy(bom, 0, payload, 0, bom.Length);
        Array.Copy(bytes, 0, payload, bom.Length, bytes.Length);
        return payload;
    }
}