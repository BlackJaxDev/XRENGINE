using MemoryPack;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Core.Files;

/// <summary>Installs published serializers for Data-owned settings assets.</summary>
public static class DataPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(RegisterMemoryPackAsset<UserSettings>());
            leases.Add(RegisterMemoryPackAsset<BuildSettings>());
        });

    private static IDisposable RegisterMemoryPackAsset<T>() where T : XRAsset
        => PublishedCookedAssetRegistry.Register(
            typeof(T),
            static asset => MemoryPackSerializer.Serialize((T)asset),
            static (payload, _) => MemoryPackSerializer.Deserialize<T>(payload),
            "XREngine.Data");

}
