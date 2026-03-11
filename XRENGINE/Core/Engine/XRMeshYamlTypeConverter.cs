using System;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class XRMeshYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
        => type == typeof(XRMesh);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            throw new YamlException($"Unexpected scalar while deserializing {nameof(XRMesh)}: '{scalar.Value}'.");
        }

        XRMeshYamlEnvelope? envelope = rootDeserializer(typeof(XRMeshYamlEnvelope)) as XRMeshYamlEnvelope;
        if (envelope?.Payload is null || envelope.Payload.Length == 0)
            return new XRMesh();

        byte[] payload = envelope.Payload.GetBytes();
        XRMesh? mesh = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => CookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh);

        return mesh ?? new XRMesh();
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not XRMesh mesh)
            throw new YamlException($"Expected {nameof(XRMesh)} but got '{value.GetType()}'.");

        byte[] payloadBytes = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(mesh));
        XRMeshYamlEnvelope envelope = new()
        {
            AssetType = mesh.GetType().FullName ?? mesh.GetType().Name,
            Format = "CookedBinary",
            Version = 1,
            Payload = new DataSource(payloadBytes) { PreferCompressedYaml = true }
        };

        serializer(envelope, typeof(XRMeshYamlEnvelope));
    }

    private sealed class XRMeshYamlEnvelope
    {
        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}