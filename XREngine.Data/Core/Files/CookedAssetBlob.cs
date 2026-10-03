using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using XREngine.Core;

namespace XREngine.Core.Files
{
    public enum CookedAssetFormat : byte
    {
        BinaryV1 = 1,
        RuntimeBinaryV1 = 2,
        /// <summary>Generic cooked payload with identity-based graph references.</summary>
        BinaryV2 = 3,
    }

    [MemoryPackable]
    public partial struct CookedAssetBlob(string typeName, CookedAssetFormat format, byte[] payload)
    {
        public string TypeName { get; set; } = typeName;

        public CookedAssetFormat Format { get; set; } = format;

        public byte[] Payload { get; set; } = payload;
    }

    public static class CookedAssetReader
    {
        internal const string ReflectionWarningMessage = "Cooked cooked asset loading relies on reflection and cannot be statically analyzed for trimming or AOT";

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static T? LoadAsset<T>(byte[] cookedData)
        {
            object? value = LoadAsset(cookedData, typeof(T));
            return value is T typed ? typed : default;
        }

        /// <summary>Loads a cooked asset and optionally requires a graph-capable generic cache.</summary>
        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static object? LoadAsset(byte[] cookedData, Type? expectedType = null, bool requireReferenceGraph = false)
        {
            ArgumentNullException.ThrowIfNull(cookedData);

            CookedMemoryPackEnvelope.Validate(cookedData, hasFormat: true);
            var blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(cookedData);
            RequireReferenceGraphWhenRequested(blob, requireReferenceGraph);
            return blob.Format switch
            {
                CookedAssetFormat.BinaryV1 => DeserializeBinary(blob, expectedType),
                CookedAssetFormat.BinaryV2 => DeserializeBinary(blob, expectedType),
                CookedAssetFormat.RuntimeBinaryV1 => DeserializeRuntimeBinary(blob, expectedType),
                _ => throw new NotSupportedException($"Unsupported cooked asset format '{blob.Format}'.")
            };
        }

        /// <summary>
        /// Loads a cooked asset from a <see cref="ReadOnlySpan{T}"/> without requiring
        /// a <c>byte[]</c> allocation for the outer envelope.  The decompressed bytes
        /// from the archive can be passed directly here. When requested, older generic
        /// caches that cannot prove authored graph identity require recooking.
        /// </summary>
        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static object? LoadAsset(ReadOnlySpan<byte> cookedData, Type? expectedType = null, bool requireReferenceGraph = false)
        {
            if (cookedData.IsEmpty)
                throw new ArgumentException("Cooked data is empty.", nameof(cookedData));

            CookedMemoryPackEnvelope.Validate(cookedData, hasFormat: true);
            var blob = MemoryPackSerializer.Deserialize<CookedAssetBlob>(cookedData);
            RequireReferenceGraphWhenRequested(blob, requireReferenceGraph);
            return blob.Format switch
            {
                CookedAssetFormat.BinaryV1 => DeserializeBinary(blob, expectedType),
                CookedAssetFormat.BinaryV2 => DeserializeBinary(blob, expectedType),
                CookedAssetFormat.RuntimeBinaryV1 => DeserializeRuntimeBinary(blob, expectedType),
                _ => throw new NotSupportedException($"Unsupported cooked asset format '{blob.Format}'.")
            };
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static object? DeserializeBinary(CookedAssetBlob blob, Type? expectedType)
        {
            Type resolvedType = ResolveAssetType(blob.TypeName, expectedType);

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
            {
                throw new NotSupportedException(
                    $"Cooked asset type '{resolvedType}' was published with generic '{blob.Format}', which is not supported in published AOT runtime builds. Register the asset type with {nameof(PublishedCookedAssetRegistry)} and republish content so it uses '{CookedAssetFormat.RuntimeBinaryV1}'.");
            }

            if (blob.Format == CookedAssetFormat.BinaryV2 &&
                (blob.Payload is not { Length: > 0 } || blob.Payload[0] != (byte)CookedBinaryTypeMarker.ReferenceDefinition))
                throw new InvalidDataException("CookedBinary.ReferenceGraphMissing: generic cooked asset has an invalid reference-graph payload; recook the source asset.");

            return CookedBinarySerializer.Deserialize(resolvedType, blob.Payload);
        }

        private static void RequireReferenceGraphWhenRequested(CookedAssetBlob blob, bool required)
        {
            if (required && blob.Format == CookedAssetFormat.BinaryV1)
                throw new InvalidDataException("CookedBinary.ReferenceGraphMissing: this older derived cache cannot preserve shared authored object references; recook the source world or scene.");
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static object? DeserializeRuntimeBinary(CookedAssetBlob blob, Type? expectedType)
        {
            Type resolvedType = ResolveAssetType(blob.TypeName, expectedType);

            if (XRRuntimeEnvironment.IsAotRuntimeBuild && !AotRuntimeMetadataStore.IsPublishedRuntimeAssetType(resolvedType))
            {
                throw new NotSupportedException(
                    $"Cooked asset type '{resolvedType}' is not registered in published AOT runtime metadata for format '{CookedAssetFormat.RuntimeBinaryV1}'.");
            }

            if (!PublishedCookedAssetRegistry.TryDeserialize(resolvedType, blob.Payload, out object? asset))
                throw new NotSupportedException($"No published cooked asset serializer is registered for '{resolvedType}'.");

            return asset;
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static Type ResolveAssetType(string? typeName, Type? expectedType)
        {
            Type resolvedType = CookedAssetTypeReference.Resolve(typeName, expectedType)
                ?? throw new InvalidOperationException($"Unable to resolve cooked asset type '{typeName}'.");

            if (expectedType is not null && !expectedType.IsAssignableFrom(resolvedType))
                throw new InvalidOperationException($"Cooked asset type '{resolvedType}' does not match expected type '{expectedType}'.");

            return resolvedType;
        }
    }
}
