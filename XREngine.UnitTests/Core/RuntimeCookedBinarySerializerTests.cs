using System;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene.Physics;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class RuntimeCookedBinarySerializerTests
{
    [Test]
    [NonParallelizable]
    public void ObjectFallback_CachesUnsupportedMemoryPackTypeAfterFirstProbe()
    {
        int memoryPackExceptionCount = 0;
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception is MemoryPackSerializationException)
                Interlocked.Increment(ref memoryPackExceptionCount);
        };

        AppDomain.CurrentDomain.FirstChanceException += handler;
        byte[] firstPayload;
        byte[] secondPayload;
        try
        {
            firstPayload = CookedBinarySerializer.Serialize(
                new UnsupportedMemoryPackPayload { Value = 41 });
            secondPayload = CookedBinarySerializer.Serialize(
                new UnsupportedMemoryPackPayload { Value = 42 });
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        memoryPackExceptionCount.ShouldBe(1);
        var firstClone = CookedBinarySerializer.Deserialize(
            typeof(UnsupportedMemoryPackPayload),
            firstPayload).ShouldBeOfType<UnsupportedMemoryPackPayload>();
        var secondClone = CookedBinarySerializer.Deserialize(
            typeof(UnsupportedMemoryPackPayload),
            secondPayload).ShouldBeOfType<UnsupportedMemoryPackPayload>();
        firstClone.Value.ShouldBe(41);
        secondClone.Value.ShouldBe(42);
    }

    [Test]
    public void ObjectFallback_ReflectionOnlyValueTypePreservesPublicFields()
    {
        var source = new IPhysicsGeometry.Capsule(radius: 0.75f, halfHeight: 1.25f);

        byte[] payload = CookedBinarySerializer.Serialize(source);
        var clone = CookedBinarySerializer
            .Deserialize(typeof(IPhysicsGeometry.Capsule), payload)
            .ShouldBeOfType<IPhysicsGeometry.Capsule>();

        clone.Radius.ShouldBe(source.Radius);
        clone.HalfHeight.ShouldBe(source.HalfHeight);
    }

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

    private sealed class UnsupportedMemoryPackPayload
    {
        public int Value { get; set; }
    }
}
