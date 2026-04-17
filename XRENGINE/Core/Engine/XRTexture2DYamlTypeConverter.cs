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
        if (envelope?.ID is Guid referenceId
            && referenceId != Guid.Empty
            && (envelope.Payload is null || envelope.Payload.Length == 0))
            return ResolveExternalReference(referenceId);

        if (envelope?.Payload is null || envelope.Payload.Length == 0)
        {
            // Old cache files written before the CookedBinary texture serializer produce an
            // envelope with no payload because the inline XRTexture2D YAML fields don't match
            // the envelope schema. Throw so that TryLoadCachedAsset deletes the stale cache
            // and falls through to a fresh import.
            throw new YamlException(
                $"{nameof(XRTexture2D)} CookedBinary envelope has no payload. " +
                "This typically indicates a stale cache written before texture-streaming support. " +
                "The asset will be reimported automatically.");
        }

        byte[] payload = envelope.Payload.GetBytes();
        XRTexture2D? texture = RuntimeCookedBinarySerializer.Deserialize(typeof(XRTexture2D), payload) as XRTexture2D;
        return texture ?? new XRTexture2D();
    }

    private static XRTexture2D? ResolveExternalReference(Guid id)
    {
        if (Engine.Assets.TryGetAssetByID(id, out XRAsset? loadedAsset) && loadedAsset is XRTexture2D loadedTexture)
            return loadedTexture;

        if (!Engine.Assets.TryResolveAssetPathById(id, out string? assetPath) || string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return null;

        return Engine.Assets.LoadImmediate(assetPath, typeof(XRTexture2D)) as XRTexture2D;
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

        if (TryWriteAsReference.TryEmitReference(emitter, texture))
            return;

        byte[] payloadBytes = RuntimeCookedBinarySerializer.Serialize(texture);
        XRTexture2DYamlEnvelope envelope = new()
        {
            ID = texture.ID,
            AssetType = texture.GetType().FullName ?? texture.GetType().Name,
            Format = "CookedBinary",
            Version = 1,
            Payload = new DataSource(payloadBytes) { PreferCompressedYaml = true }
        };

        serializer(envelope, typeof(XRTexture2DYamlEnvelope));
    }

    private sealed class XRTexture2DYamlEnvelope
    {
        public Guid? ID { get; set; }

        [YamlMember(Alias = "__assetType", Order = -100)]
        public string? AssetType { get; set; }

        public string Format { get; set; } = "CookedBinary";

        public int Version { get; set; } = 1;

        public DataSource? Payload { get; set; }
    }
}
