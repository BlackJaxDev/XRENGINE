using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine;

/// <summary>Installs shared host cooked serializers for application settings.</summary>
public static class BootstrapPublishedCookedAssetRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(GameStartupSettings),
                static (asset, writer) => MemoryPackSerializer.Serialize(writer, (GameStartupSettings)asset),
                static (payload, _) => MemoryPackSerializer.Deserialize<GameStartupSettings>(payload),
                "XREngine.Runtime.Host",
                static asset => DescribeStartupDependencies((GameStartupSettings)asset)));
            leases.Add(RegisterMemoryPackAsset<EditorPreferences>());
        });

    private static IReadOnlyList<PublishedCookedAssetDependency> DescribeStartupDependencies(GameStartupSettings settings)
    {
        // The browser cooker removes window world references before serializing this
        // settings object; a non-null target would carry a separate authored world.
        if (settings.StartupWindows.Any(static window => window.TargetWorld is not null))
            throw new NotSupportedException("Published browser startup settings must not embed target worlds.");
        return Array.Empty<PublishedCookedAssetDependency>();
    }

    private static IDisposable RegisterMemoryPackAsset<T>() where T : XRAsset
        => PublishedCookedAssetRegistry.Register(
            typeof(T),
            static (asset, writer) => MemoryPackSerializer.Serialize(writer, (T)asset),
            static (payload, _) => MemoryPackSerializer.Deserialize<T>(payload),
            "XREngine.Runtime.Host");
}
