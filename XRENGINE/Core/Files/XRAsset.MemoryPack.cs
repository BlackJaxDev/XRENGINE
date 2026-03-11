using MemoryPack;
using System;
using XREngine.Core;
using XREngine.Data.Core;

namespace XREngine.Core.Files
{
    [MemoryPackable]
    internal readonly partial record struct XRAssetMemoryPackEnvelope(string TypeReference, byte[] Payload);

    /// <summary>
    /// Provides a MemoryPack-friendly envelope for any <see cref="XRAsset"/> without requiring every derived type to be generated.
    /// </summary>
    internal static class XRAssetMemoryPackAdapter
    {
        public static byte[] Serialize(XRAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);

            string typeReference = CookedAssetTypeReference.Encode(asset.GetType());
            byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(asset));
            XRAssetMemoryPackEnvelope envelope = new(typeReference, payload);
            return MemoryPackSerializer.Serialize(envelope);
        }

        public static XRAsset? Deserialize(ReadOnlySpan<byte> data, Type? expectedType)
        {
            XRAssetMemoryPackEnvelope? envelope = MemoryPackSerializer.Deserialize<XRAssetMemoryPackEnvelope>(data);
            if (envelope is null)
                return null;

            Type? resolved = CookedAssetTypeReference.Resolve(envelope.Value.TypeReference, expectedType);
            if (resolved is null)
                throw new InvalidOperationException($"Unable to resolve asset type '{envelope.Value.TypeReference}'.");

            if (expectedType is not null && !expectedType.IsAssignableFrom(resolved))
                throw new InvalidOperationException($"MemoryPack asset type '{resolved}' does not match expected type '{expectedType}'.");

            return CookedBinarySerializer.Deserialize(resolved, envelope.Value.Payload ?? Array.Empty<byte>()) as XRAsset;
        }
    }
}
