using System.Diagnostics.CodeAnalysis;
using XREngine.Core;
using XREngine.Data.Runtime.AotParity;

namespace XREngine.Core.Files
{
    public enum CookedAssetFormat : byte
    {
        BinaryV1 = 1,
        RuntimeBinaryV1 = 2,
        /// <summary>Generic cooked payload with identity-based graph references.</summary>
        BinaryV2 = 3,
    }

    /// <summary>
    /// Authoring-side value for one cooked asset: the encoded type reference, the payload format,
    /// and the payload bytes. Cooking writes it through <see cref="CookedAssetEnvelope"/>; the
    /// runtime never materializes this struct because it slices the payload from the envelope span.
    /// </summary>
    public readonly record struct CookedAssetBlob(string TypeReference, CookedAssetFormat Format, byte[] Payload);

    /// <summary>
    /// Authoring and development reader. It accepts both the registry-backed
    /// <see cref="CookedAssetFormat.RuntimeBinaryV1"/> payloads and the reflective
    /// <see cref="CookedAssetFormat.BinaryV1"/> payloads, so it carries reflection annotations.
    /// Published runtimes use <see cref="PublishedCookedAssetReader"/>, which has none.
    /// </summary>
    public static class CookedAssetReader
    {
        internal const string ReflectionWarningMessage = "Authoring cooked asset loading relies on reflection and cannot be statically analyzed for trimming or AOT";

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static T? LoadAsset<T>(ReadOnlySpan<byte> cookedData)
        {
            object? value = LoadAsset(cookedData, typeof(T));
            return value is T typed ? typed : default;
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static object? LoadAsset(ReadOnlySpan<byte> cookedData, Type? expectedType = null, bool requireReferenceGraph = false)
        {
            if (cookedData.IsEmpty)
                throw new ArgumentException("Cooked data is empty.", nameof(cookedData));

            CookedAssetEnvelopeHeader header = CookedAssetEnvelope.ParseHeader(cookedData);
            if (requireReferenceGraph && header.Format == CookedAssetFormat.BinaryV1)
                throw new InvalidDataException("CookedBinary.ReferenceGraphMissing: this older derived cache cannot preserve shared authored object references; recook the source world or scene.");

            return header.Format switch
            {
                CookedAssetFormat.BinaryV1 or CookedAssetFormat.BinaryV2 => DeserializeBinary(cookedData, header, expectedType),
                CookedAssetFormat.RuntimeBinaryV1 => PublishedCookedAssetReader.LoadAsset(cookedData, header, expectedType),
                _ => throw new NotSupportedException($"Unsupported cooked asset format '{header.Format}'."),
            };
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static object? DeserializeBinary(ReadOnlySpan<byte> envelope, in CookedAssetEnvelopeHeader header, Type? expectedType)
        {
            string typeReference = header.DecodeTypeReference(envelope);
            Type resolvedType = ResolveAssetType(typeReference, expectedType);

            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
            {
                throw new NotSupportedException(
                    $"Cooked asset type '{resolvedType}' was published with generic '{header.Format}', which is not supported in published AOT runtime builds. Register the asset type with {nameof(PublishedCookedAssetRegistry)} and republish content so it uses '{CookedAssetFormat.RuntimeBinaryV1}'.");
            }

            AotParityDiagnostics.Report(
                resolvedType,
                EAotParityCategory.ReflectiveCookedDeserialization,
                $"{nameof(CookedAssetReader)}.{nameof(DeserializeBinary)}",
                $"Register the asset type with {nameof(PublishedCookedAssetRegistry)} so it cooks and loads as '{CookedAssetFormat.RuntimeBinaryV1}' instead of the reflective '{header.Format}' reader.");

            ReadOnlySpan<byte> payload = header.Payload(envelope);
            if (header.Format == CookedAssetFormat.BinaryV2 &&
                (payload.IsEmpty || payload[0] != (byte)CookedBinaryTypeMarker.ReferenceDefinition))
                throw new InvalidDataException("CookedBinary.ReferenceGraphMissing: generic cooked asset has an invalid reference-graph payload; recook the source asset.");

            return CookedBinarySerializer.Deserialize(resolvedType, payload);
        }

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        private static Type ResolveAssetType(string? typeReference, Type? expectedType)
        {
            Type resolvedType = CookedAssetTypeReference.Resolve(typeReference, expectedType)
                ?? throw new InvalidOperationException($"Unable to resolve cooked asset type '{typeReference}'.");

            if (expectedType is not null && !expectedType.IsAssignableFrom(resolvedType))
                throw new InvalidOperationException($"Cooked asset type '{resolvedType}' does not match expected type '{expectedType}'.");

            return resolvedType;
        }
    }
}
