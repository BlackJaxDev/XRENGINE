using System;
using System.IO;
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
        if (envelope?.ID is Guid referenceId
            && referenceId != Guid.Empty
            && (envelope.Payload is null || envelope.Payload.Length == 0))
            return ResolveExternalReference(referenceId);

        if (envelope?.Payload is null || envelope.Payload.Length == 0)
            return new XRMesh();

        byte[] payload = envelope.Payload.GetBytes();
        XRMesh? mesh = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), payload) as XRMesh);

        return mesh ?? new XRMesh();
    }

    private static XRMesh? ResolveExternalReference(Guid id)
    {
        if (Engine.Assets.TryGetAssetByID(id, out XRAsset? loadedAsset) && loadedAsset is XRMesh loadedMesh)
            return loadedMesh;

        if (!Engine.Assets.TryResolveAssetPathById(id, out string? assetPath) || string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return null;

        return Engine.Assets.LoadImmediate(assetPath, typeof(XRMesh)) as XRMesh;
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

        if (TryWriteAsReference.TryEmitReference(emitter, mesh))
            return;

        byte[] payloadBytes = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => RuntimeCookedBinarySerializer.Serialize(mesh));
        XRMeshYamlEnvelope envelope = new()
        {
            ID = mesh.ID,
            AssetType = mesh.GetType().FullName ?? mesh.GetType().Name,
            Format = "CookedBinary",
            Version = 1,
            Payload = new DataSource(payloadBytes) { PreferCompressedYaml = true }
        };

        serializer(envelope, typeof(XRMeshYamlEnvelope));
    }

    private sealed class XRMeshYamlEnvelope
    {
        public Guid? ID { get; set; }

        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}