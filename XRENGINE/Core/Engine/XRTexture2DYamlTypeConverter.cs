using System;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class XRTexture2DYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
        => type == typeof(XRTexture2D);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing {nameof(XRTexture2D)}: '{scalar.Value}'.");
        }

        XRTexture2DYamlEnvelope? envelope = rootDeserializer(typeof(XRTexture2DYamlEnvelope)) as XRTexture2DYamlEnvelope;
        if (envelope?.Payload is null || envelope.Payload.Length == 0)
            return new XRTexture2D();

        byte[] payload = envelope.Payload.GetBytes();
        XRTexture2D? texture = RuntimeCookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload) as XRTexture2D;
        return texture ?? new XRTexture2D();
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not XRTexture2D texture)
            throw new YamlException($"Expected {nameof(XRTexture2D)} but got '{value.GetType()}'.");

        byte[] payloadBytes = RuntimeCookedBinarySerializer.Serialize(texture);
        XRTexture2DYamlEnvelope envelope = new()
        {
            AssetType = texture.GetType().FullName ?? texture.GetType().Name,
            Format = "CookedBinary",
            Version = 1,
            Payload = new DataSource(payloadBytes) { PreferCompressedYaml = true }
        };

        serializer(envelope, typeof(XRTexture2DYamlEnvelope));
    }

    private sealed class XRTexture2DYamlEnvelope
    {
        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}
