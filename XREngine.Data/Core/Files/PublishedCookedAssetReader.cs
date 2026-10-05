using XREngine.Data.Runtime.AotParity;

namespace XREngine.Core.Files;

/// <summary>
/// The published runtime's cooked asset reader. It understands only
/// <see cref="CookedAssetFormat.RuntimeBinaryV1"/>, resolves types through published metadata and
/// registrations, and dispatches to <see cref="PublishedCookedAssetRegistry"/>. It performs no
/// reflection, so it carries no trimming or dynamic-code annotations.
/// </summary>
public static class PublishedCookedAssetReader
{
    public static T? LoadAsset<T>(ReadOnlySpan<byte> cookedData) where T : class
        => LoadAsset(cookedData, typeof(T)) as T;

    /// <summary>Parses the envelope and deserializes the payload through the registered runtime codec.</summary>
    public static object? LoadAsset(ReadOnlySpan<byte> cookedData, Type? expectedType = null)
    {
        if (cookedData.IsEmpty)
            throw new ArgumentException("Cooked data is empty.", nameof(cookedData));

        CookedAssetEnvelopeHeader header = CookedAssetEnvelope.ParseHeader(cookedData);
        return LoadAsset(cookedData, header, expectedType);
    }

    /// <summary>Deserializes an already-parsed envelope. Used by the authoring reader for runtime-format payloads.</summary>
    public static object? LoadAsset(ReadOnlySpan<byte> envelope, in CookedAssetEnvelopeHeader header, Type? expectedType)
    {
        using var parityScope = AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PublishedContentLoad);
        if (header.Format != CookedAssetFormat.RuntimeBinaryV1)
        {
            throw new NotSupportedException(
                $"Cooked asset uses format '{header.Format}', which the published reader does not support. Register the asset type with {nameof(PublishedCookedAssetRegistry)} and re-cook content so it uses '{CookedAssetFormat.RuntimeBinaryV1}'.");
        }

        string typeReference = header.DecodeTypeReference(envelope);
        if (!CookedAssetTypeReference.TryResolvePublished(typeReference, expectedType, out Type? resolvedType) || resolvedType is null)
        {
            throw new InvalidOperationException(
                $"Unable to resolve cooked asset type '{typeReference}' from published metadata or registrations. Add the type to the published runtime metadata known-type table or register it with {nameof(PublishedCookedAssetRegistry)}.");
        }

        if (expectedType is not null && !expectedType.IsAssignableFrom(resolvedType))
            throw new InvalidOperationException($"Cooked asset type '{resolvedType}' does not match expected type '{expectedType}'.");

        if (XRRuntimeEnvironment.IsAotRuntimeBuild && !AotRuntimeMetadataStore.IsPublishedRuntimeAssetType(resolvedType))
        {
            throw new NotSupportedException(
                $"Cooked asset type '{resolvedType}' is not registered in published AOT runtime metadata for format '{CookedAssetFormat.RuntimeBinaryV1}'.");
        }

        if (!PublishedCookedAssetRegistry.TryDeserialize(resolvedType, header.Payload(envelope), out object? asset))
            throw new NotSupportedException($"No published cooked asset serializer is registered for '{resolvedType}'.");

        return asset;
    }
}
