using MemoryPack;
using System;
using XREngine.Core;

namespace XREngine.Core.Files
{
    [MemoryPackable]
    internal readonly partial record struct XRAssetMemoryPackEnvelope(string TypeName, byte[] Payload);

    /// <summary>
    /// Provides a MemoryPack-friendly envelope for any <see cref="XRAsset"/> without requiring every derived type to be generated.
    /// </summary>
    internal static class XRAssetMemoryPackAdapter
    {
        public static byte[] Serialize(XRAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);

            string typeName = asset.GetType().AssemblyQualifiedName
                ?? asset.GetType().FullName
                ?? asset.GetType().Name;

            byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(asset));
            XRAssetMemoryPackEnvelope envelope = new(typeName, payload);
            return MemoryPackSerializer.Serialize(envelope);
        }

        public static XRAsset? Deserialize(ReadOnlySpan<byte> data, Type? expectedType)
        {
            XRAssetMemoryPackEnvelope? envelope = MemoryPackSerializer.Deserialize<XRAssetMemoryPackEnvelope>(data);
            if (envelope is null)
                return null;

            Type? resolved = ResolveType(envelope.Value.TypeName) ?? expectedType;
            if (resolved is null)
                throw new InvalidOperationException($"Unable to resolve asset type '{envelope.Value.TypeName}'.");

            if (expectedType is not null && !expectedType.IsAssignableFrom(resolved))
                throw new InvalidOperationException($"MemoryPack asset type '{resolved}' does not match expected type '{expectedType}'.");

            return CookedBinarySerializer.Deserialize(resolved, envelope.Value.Payload ?? Array.Empty<byte>()) as XRAsset;
        }

        private static Type? ResolveType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            typeName = XRTypeRedirectRegistry.RewriteTypeName(typeName);

            Type? type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type is not null)
                return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type is not null)
                    return type;
            }

            return null;
        }
    }
}
