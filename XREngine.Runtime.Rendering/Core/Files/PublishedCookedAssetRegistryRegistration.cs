using XREngine.Rendering;
using XREngine.Data;

namespace XREngine.Core.Files;

public static class RenderingPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(XRMesh), static () => new XRMesh()));
            leases.Add(RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(XRTexture2D), static () => new XRTexture2D()));
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(XRMesh),
                static asset => RuntimeCookedBinarySerializer.Serialize((XRMesh)asset),
                static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload),
                "XREngine.Runtime.Rendering"));
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(XRTexture2D),
                static asset => RuntimeCookedBinarySerializer.Serialize((XRTexture2D)asset),
                static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload),
                "XREngine.Runtime.Rendering"));
        });
}
