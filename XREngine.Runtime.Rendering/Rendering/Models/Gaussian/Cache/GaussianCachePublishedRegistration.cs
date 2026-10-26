using System;
using System.Linq;
using XREngine.Core.Files;
using CookedBinarySerializer = XREngine.Core.Files.RuntimeCookedBinarySerializer;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Registers the published cooked codecs of the cache asset types.</summary>
internal static class GaussianCachePublishedRegistration
{
    private const string OwnerName = "XREngine.Runtime.Rendering";

    internal static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(GaussianCache),
                SerializeAsset,
                DeserializeAsset,
                OwnerName,
                static asset => ((GaussianCache)asset).ChunkAssetPaths
                    .Select(static path => new PublishedCookedAssetDependency(path, typeof(GaussianCacheChunk)))
                    .ToArray()));
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(GaussianCacheChunk),
                SerializeAsset,
                DeserializeAsset,
                OwnerName,
                static _ => Array.Empty<PublishedCookedAssetDependency>()));
        });

#pragma warning disable IL2026, IL3050 // The cache types use the runtime cooked binary serializer.
    private static void SerializeAsset(object asset, System.Buffers.IBufferWriter<byte> writer)
        => CookedBinarySerializer.Serialize(asset, writer);

    private static object? DeserializeAsset(ReadOnlySpan<byte> payload, Type assetType)
        => CookedBinarySerializer.Deserialize(assetType, payload);
#pragma warning restore IL2026, IL3050
}
